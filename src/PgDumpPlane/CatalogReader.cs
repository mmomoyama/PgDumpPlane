using Npgsql;

namespace PgDumpPlane;

/// <summary>
/// <para>PostgreSQLカタログから、ダンプ対象の定義と権限を読み取ります。</para>
/// <para>Reads dump definitions and security metadata from PostgreSQL catalogs.</para>
/// </summary>
internal static class CatalogReader
{
    /// <summary>
    /// <para>選択されたオブジェクトを同じ接続から順に読み取り、ダンプ用のカタログ情報をまとめます。</para>
    /// <para>Reads selected objects sequentially on one connection and assembles the dump catalog.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>取得した定義・権限を含むカタログを返すタスク。 A task returning the catalog of definitions and security metadata.</returns>
    internal static async Task<CatalogSnapshot> ReadAsync(
        NpgsqlConnection connection,
        PgDumpOptions options,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var database = await ReadDatabaseAsync(connection, capabilities, cancellationToken).ConfigureAwait(false);
        var schemas = await ReadSchemasAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var selected = schemas.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        // 接続上で有効なコマンドは一つだけなので、カタログを並列に読まないでください。
        // Keep catalog reads sequential: Npgsql permits one active command per connection.
        var extensions = await ReadExtensionsAsync(connection, selected, cancellationToken).ConfigureAwait(false);
        var enums = await ReadEnumsAsync(connection, selected, cancellationToken).ConfigureAwait(false);
        var routines = await ReadRoutinesAsync(connection, selected, cancellationToken).ConfigureAwait(false);
        var sequences = await ReadSequencesAsync(connection, selected, capabilities, cancellationToken).ConfigureAwait(false);
        var tables = await ReadTablesAsync(connection, selected, capabilities, cancellationToken).ConfigureAwait(false);
        var views = await ReadViewsAsync(connection, selected, cancellationToken).ConfigureAwait(false);
        var tableOids = tables.Select(x => x.Oid).ToHashSet();

        var constraints = await ReadConstraintsAsync(connection, tableOids, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, tableOids, cancellationToken).ConfigureAwait(false);
        var triggers = await ReadTriggersAsync(connection, tableOids, capabilities, cancellationToken).ConfigureAwait(false);
        var ownership = options.IncludeSchema && options.IncludeOwnership
            ? await ReadOwnershipAsync(connection, selected, cancellationToken).ConfigureAwait(false)
            : [];
        var accessControls = options.IncludeSchema && options.IncludePrivileges
            ? await ReadAccessControlsAsync(connection, selected, cancellationToken).ConfigureAwait(false)
            : [];
        var roleSettings = options.IncludeRoleSettings
            ? await ReadRoleSettingsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];

        return new(
            database, schemas, extensions, enums, routines, sequences, tables, views, constraints, indexes, triggers,
            ownership, accessControls, roleSettings);
    }

    /// <summary>
    /// <para>接続先のデータベース名とサーバーバージョンを取得します。</para>
    /// <para>Reads the connected database name and server version.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>接続先のDBとバージョン情報を返すタスク。 A task returning database and version metadata.</returns>
    private static async Task<DatabaseInfo> ReadDatabaseAsync(
        NpgsqlConnection connection,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), current_setting('server_version')", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(reader.GetString(0), reader.GetString(1), capabilities.Major);
    }

    /// <summary>
    /// <para>システムスキーマを除き、対象・除外条件を満たすスキーマを取得します。</para>
    /// <para>Reads non-system schemas that satisfy the include and exclude filters.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>対象スキーマの一覧を返すタスク。 A task returning the selected schemas.</returns>
    private static async Task<IReadOnlyList<SchemaInfo>> ReadSchemasAsync(
        NpgsqlConnection connection,
        PgDumpOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT nspname
            FROM pg_catalog.pg_namespace
            WHERE nspname <> 'information_schema'
              AND nspname !~ '^pg_'
            ORDER BY nspname
            """;
        var result = new List<SchemaInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (options.Includes(name))
                result.Add(new(name));
        }

        return result;
    }

    /// <summary>
    /// <para>選択されたスキーマに属する拡張機能を取得します。</para>
    /// <para>Reads extensions belonging to the selected schemas.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>対象拡張機能の一覧を返すタスク。 A task returning the selected extensions.</returns>
    private static async Task<IReadOnlyList<ExtensionInfo>> ReadExtensionsAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.extname, n.nspname
            FROM pg_catalog.pg_extension e
            JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
            WHERE e.extname <> 'plpgsql'
            ORDER BY e.extname
            """;
        var result = new List<ExtensionInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (selected.Contains(reader.GetString(1)))
                result.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>
    /// <para>拡張機能に属さない列挙型を取得し、ラベルを定義順にまとめます。</para>
    /// <para>Reads enums outside extensions and groups labels in their declared order.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>定義順のラベルを含む列挙型一覧を返すタスク。 A task returning enums with ordered labels.</returns>
    private static async Task<IReadOnlyList<EnumTypeInfo>> ReadEnumsAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.oid, n.nspname, t.typname, e.enumlabel
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_catalog.pg_enum e ON e.enumtypid = t.oid
            WHERE NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                  AND d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname, t.typname, e.enumsortorder
            """;
        var rows = new List<(uint Oid, string Schema, string Name, string Label)>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(1);
            if (selected.Contains(schema))
                rows.Add((reader.GetFieldValue<uint>(0), schema, reader.GetString(2), reader.GetString(3)));
        }
        return rows.GroupBy(x => (x.Oid, x.Schema, x.Name))
            .Select(g => new EnumTypeInfo(g.Key.Schema, g.Key.Name, g.Select(x => x.Label).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// <para>拡張機能に属さない関数とプロシージャの定義を取得します。</para>
    /// <para>Reads definitions of functions and procedures outside extensions.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>関数・プロシージャの定義一覧を返すタスク。 A task returning routine definitions.</returns>
    private static async Task<IReadOnlyList<RoutineInfo>> ReadRoutinesAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname, p.proname, pg_catalog.pg_get_functiondef(p.oid),
                   p.prokind::text, pg_catalog.pg_get_function_identity_arguments(p.oid)
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p')
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                  AND d.objid = p.oid AND d.deptype = 'e')
            -- pg_dumpは作成OIDではなく、引数数と引数型のスキーマ・名前を比較します。
            -- pg_dump compares argument counts and type schema/name, rather than creation OIDs.
            ORDER BY n.nspname COLLATE "C", p.proname COLLATE "C", p.pronargs,
                     ARRAY(SELECT tn.nspname::text || '.' || t.typname::text
                           FROM unnest(p.proargtypes::oid[]) WITH ORDINALITY AS args(type_oid, position)
                           JOIN pg_catalog.pg_type t ON t.oid = args.type_oid
                           JOIN pg_catalog.pg_namespace tn ON tn.oid = t.typnamespace
                           ORDER BY args.position) COLLATE "C", p.oid
            """;
        var result = new List<RoutineInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            if (selected.Contains(schema))
                result.Add(new(schema, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3) == "p" ? SecuredObjectKind.Procedure : SecuredObjectKind.Function,
                    reader.GetString(4)));
        }
        return result;
    }

    /// <summary>
    /// <para>シーケンスの設定、所有列、およびIDENTITYとの関連を取得します。</para>
    /// <para>Reads sequence settings, owning columns, and identity associations.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>設定と所有列を含むシーケンス一覧を返すタスク。 A task returning sequences with settings and owning columns.</returns>
    private static async Task<IReadOnlyList<SequenceInfo>> ReadSequencesAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        // 古いサーバーで新機能を参照しないよう、SQL式を機能境界で切り替えます。
        // Select SQL expressions by capability to avoid referencing unsupported features.
        var unloggedExpression = capabilities.SupportsUnloggedSequences
            ? "c.relpersistence = 'u'"
            : "false";
        var sql = $"""
            SELECT c.oid, n.nspname, c.relname, {unloggedExpression},
                   pg_catalog.format_type(s.seqtypid, NULL),
                   s.seqstart, s.seqmin, s.seqmax, s.seqincrement, s.seqcache, s.seqcycle,
                   tn.nspname, tc.relname, a.attname, d.deptype = 'i'
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_sequence s ON s.seqrelid = c.oid
            LEFT JOIN pg_catalog.pg_depend d
              ON d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
             AND d.objid = c.oid
             AND d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
             AND d.deptype IN ('a', 'i')
            LEFT JOIN pg_catalog.pg_class tc ON tc.oid = d.refobjid
            LEFT JOIN pg_catalog.pg_namespace tn ON tn.oid = tc.relnamespace
            LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = tc.oid AND a.attnum = d.refobjsubid
            WHERE c.relkind = 'S'
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend x
                WHERE x.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  AND x.objid = c.oid AND x.deptype = 'e')
            ORDER BY n.nspname, c.relname
            """;
        var result = new List<SequenceInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(1);
            if (!selected.Contains(schema))
                continue;
            result.Add(new(
                reader.GetFieldValue<uint>(0), schema, reader.GetString(2), reader.GetBoolean(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), !reader.IsDBNull(14) && reader.GetBoolean(14)));
        }
        return result;
    }

    /// <summary>
    /// <para>テーブルとパーティションの定義を取得し、列情報を関連付けます。</para>
    /// <para>Reads table and partition definitions and attaches their column metadata.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>列情報を含むテーブル一覧を返すタスク。 A task returning tables with column metadata.</returns>
    private static async Task<IReadOnlyList<TableInfo>> ReadTablesAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        const string tableSql = """
            SELECT c.oid, n.nspname, c.relname, c.relkind::text, c.relpersistence = 'u',
                   CASE WHEN c.relkind = 'p' THEN pg_catalog.pg_get_partkeydef(c.oid) END,
                   i.inhparent, pn.nspname, pc.relname,
                   CASE WHEN c.relispartition THEN pg_catalog.pg_get_expr(c.relpartbound, c.oid) END,
                   pg_catalog.array_to_string(c.reloptions, ', '), ts.spcname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_inherits i ON i.inhrelid = c.oid
            LEFT JOIN pg_catalog.pg_class pc ON pc.oid = i.inhparent
            LEFT JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace
            LEFT JOIN pg_catalog.pg_tablespace ts ON ts.oid = c.reltablespace
            WHERE c.relkind IN ('r', 'p')
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  AND d.objid = c.oid AND d.deptype = 'e')
            ORDER BY n.nspname, c.relname
            """;
        var tableRows = new List<(uint Oid, string Schema, string Name, char Kind, bool Unlogged, string? Key, uint? Parent, string? ParentSchema, string? ParentName, string? Bound, string? Options, string? Tablespace)>();
        await using (var command = new NpgsqlCommand(tableSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(1);
                if (!selected.Contains(schema))
                    continue;
                tableRows.Add((reader.GetFieldValue<uint>(0), schema, reader.GetString(2), reader.GetString(3)[0], reader.GetBoolean(4),
                    GetNullableString(reader, 5), reader.IsDBNull(6) ? null : reader.GetFieldValue<uint>(6), GetNullableString(reader, 7),
                    GetNullableString(reader, 8), GetNullableString(reader, 9), GetNullableString(reader, 10), GetNullableString(reader, 11)));
            }
        }

        var selectedOids = tableRows.Select(x => x.Oid).ToHashSet();
        var columns = await ReadColumnsAsync(connection, selectedOids, capabilities, cancellationToken).ConfigureAwait(false);
        return tableRows.Select(x => new TableInfo(x.Oid, x.Schema, x.Name, x.Kind, x.Unlogged, x.Key, x.Parent, x.ParentSchema, x.ParentName, x.Bound,
            x.Options, x.Tablespace, columns.GetValueOrDefault(x.Oid) ?? [])).ToArray();
    }

    /// <summary>
    /// <para>対応バージョンのカタログを使い、テーブルOIDごとに列定義を取得します。</para>
    /// <para>Reads column definitions by table OID using version-appropriate catalogs.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selectedOids">選択されたオブジェクトOIDの集合。 Set of selected object OIDs.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>テーブルOIDをキーとする列定義一覧を返すタスク。 A task returning column lists keyed by table OID.</returns>
    private static async Task<Dictionary<uint, IReadOnlyList<ColumnInfo>>> ReadColumnsAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        // 存在しないカタログ列はSELECTの解析時に失敗するため、古い版では型付きNULLを返します。
        // An absent catalog column fails during query parsing; use typed NULL on older servers.
        var compressionExpression = capabilities.SupportsColumnCompression
            ? "CASE a.attcompression WHEN 'p' THEN 'pglz' WHEN 'l' THEN 'lz4' END"
            : "NULL::text";
        var notNullFields = capabilities.SupportsNamedNotNullConstraints
            ? "nn.conname, COALESCE(nn.connoinherit, false)"
            : "NULL::text, false";
        var notNullJoin = capabilities.SupportsNamedNotNullConstraints
            ? """
              LEFT JOIN pg_catalog.pg_constraint nn
                ON nn.conrelid = a.attrelid
               AND nn.contype = 'n'
               AND nn.conkey = array[a.attnum]
              """
            : string.Empty;
        var sql = $"""
            SELECT a.attrelid, a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull,
                   pg_catalog.pg_get_expr(ad.adbin, ad.adrelid), a.attidentity::text, a.attgenerated::text,
                   CASE WHEN a.attcollation <> t.typcollation
                        THEN pg_catalog.quote_ident(cn.nspname) || '.' || pg_catalog.quote_ident(co.collname) END,
                   {compressionExpression}, {notNullFields},
                   ARRAY(SELECT DISTINCT d.refobjid
                         FROM pg_catalog.pg_depend d
                         JOIN pg_catalog.pg_class referenced ON referenced.oid = d.refobjid
                         WHERE d.classid = 'pg_catalog.pg_attrdef'::pg_catalog.regclass
                           AND d.objid = ad.oid
                           AND d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                           AND referenced.relkind = 'S')
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation co ON co.oid = a.attcollation
            LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace
            {notNullJoin}
            WHERE a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attrelid, a.attnum
            """;
        var result = new Dictionary<uint, IReadOnlyList<ColumnInfo>>();
        var mutable = new Dictionary<uint, List<ColumnInfo>>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var oid = reader.GetFieldValue<uint>(0);
            if (!selectedOids.Contains(oid))
                continue;
            if (!mutable.TryGetValue(oid, out var list))
                mutable.Add(oid, list = []);
            var identityText = reader.GetString(5);
            var generatedText = reader.GetString(6);
            list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), GetNullableString(reader, 4),
                identityText.Length == 0 ? '\0' : identityText[0], generatedText.Length == 0 ? '\0' : generatedText[0],
                GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9), reader.GetBoolean(10))
            {
                SequenceDependencies = reader.GetFieldValue<uint[]>(11)
            });
        }
        foreach (var pair in mutable)
            result.Add(pair.Key, pair.Value);
        return result;
    }

    /// <summary>
    /// <para>ビュー定義を取得し、作成順序を決める依存先を関連付けます。</para>
    /// <para>Reads view definitions and attaches dependencies needed for creation order.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>依存情報を含むビュー一覧を返すタスク。 A task returning views with dependencies.</returns>
    private static async Task<IReadOnlyList<ViewInfo>> ReadViewsAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.oid, n.nspname, c.relname, pg_catalog.pg_get_viewdef(c.oid, true),
                   pg_catalog.array_to_string(c.reloptions, ', ')
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'v'
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  AND d.objid = c.oid AND d.deptype = 'e')
            ORDER BY n.nspname, c.relname
            """;
        var raw = new List<(uint Oid, string Schema, string Name, string Definition, string? Options)>();
        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(1);
                if (selected.Contains(schema))
                    raw.Add((reader.GetFieldValue<uint>(0), schema, reader.GetString(2), reader.GetString(3), GetNullableString(reader, 4)));
            }
        }
        // ビューの依存先はpg_rewriteのルールに記録されるため、定義と別に取得します。
        // View dependencies live on pg_rewrite rules, so read them separately from view definitions.
        var oids = raw.Select(x => x.Oid).ToHashSet();
        var dependencies = await ReadViewDependenciesAsync(connection, oids, cancellationToken).ConfigureAwait(false);
        return raw.Select(x => new ViewInfo(x.Oid, x.Schema, x.Name, x.Definition, x.Options,
            dependencies.GetValueOrDefault(x.Oid) ?? [])).ToArray();
    }

    /// <summary>
    /// <para>リライトルールからビューの依存先テーブル・ビュー・シーケンスを取得します。</para>
    /// <para>Reads view dependencies on tables, views, and sequences through rewrite rules.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selectedOids">選択されたオブジェクトOIDの集合。 Set of selected object OIDs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>ビューOIDごとの依存先OID一覧を返すタスク。 A task returning dependency OIDs by view OID.</returns>
    private static async Task<Dictionary<uint, IReadOnlyList<uint>>> ReadViewDependenciesAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT rw.ev_class, d.refobjid
            FROM pg_catalog.pg_rewrite rw
            JOIN pg_catalog.pg_depend d
              ON d.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass AND d.objid = rw.oid
             AND d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
            JOIN pg_catalog.pg_class dependency ON dependency.oid = d.refobjid
            WHERE dependency.relkind IN ('r', 'p', 'v', 'S') AND rw.ev_class <> d.refobjid
            """;
        var mutable = new Dictionary<uint, List<uint>>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var view = reader.GetFieldValue<uint>(0);
            var dependency = reader.GetFieldValue<uint>(1);
            if (!selectedOids.Contains(view))
                continue;
            if (!mutable.TryGetValue(view, out var list))
                mutable.Add(view, list = []);
            list.Add(dependency);
        }
        return mutable.ToDictionary(x => x.Key, x => (IReadOnlyList<uint>)x.Value);
    }

    /// <summary>
    /// <para>選択されたテーブルに直接定義された制約を復元順序で取得します。</para>
    /// <para>Reads locally defined constraints on selected tables in restoration order.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selectedOids">選択されたオブジェクトOIDの集合。 Set of selected object OIDs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>対象テーブルの制約一覧を返すタスク。 A task returning selected table constraints.</returns>
    private static async Task<IReadOnlyList<ConstraintInfo>> ReadConstraintsAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname, c.relname, con.conname, con.contype::text,
                   pg_catalog.pg_get_constraintdef(con.oid, true), con.conrelid
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype IN ('p', 'u', 'f', 'c', 'x')
              AND con.conparentid = 0
              AND con.conislocal
            ORDER BY CASE con.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'x' THEN 2 WHEN 'c' THEN 3 ELSE 4 END,
                     n.nspname, c.relname, con.conname
            """;
        var result = new List<ConstraintInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (selectedOids.Contains(reader.GetFieldValue<uint>(5)))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)[0], reader.GetString(4)));
        }
        return result;
    }

    /// <summary>
    /// <para>制約によって作成される索引を除き、有効な独立索引を取得します。</para>
    /// <para>Reads valid standalone indexes, excluding indexes created by constraints.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selectedOids">選択されたオブジェクトOIDの集合。 Set of selected object OIDs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>独立索引の一覧を返すタスク。 A task returning standalone indexes.</returns>
    private static async Task<IReadOnlyList<IndexInfo>> ReadIndexesAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname, t.relname, i.relname, pg_catalog.pg_get_indexdef(i.oid),
                   x.indisclustered, x.indisreplident, t.oid
            FROM pg_catalog.pg_index x
            JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid
            JOIN pg_catalog.pg_class t ON t.oid = x.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
            LEFT JOIN pg_catalog.pg_constraint con ON con.conindid = i.oid
            WHERE con.oid IS NULL AND x.indisvalid
            ORDER BY n.nspname, t.relname, i.relname
            """;
        var result = new List<IndexInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (selectedOids.Contains(reader.GetFieldValue<uint>(6)))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }
        return result;
    }

    /// <summary>
    /// <para>内部トリガーと自動複製された子トリガーを除いて定義を取得します。</para>
    /// <para>Reads trigger definitions excluding internal triggers and, where supported, child clones.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selectedOids">選択されたオブジェクトOIDの集合。 Set of selected object OIDs.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>出力対象トリガーの一覧を返すタスク。 A task returning triggers selected for output.</returns>
    private static async Task<IReadOnlyList<TriggerInfo>> ReadTriggersAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        // 親トリガーから自動生成される複製は再作成せず、復元時にPostgreSQLへ任せます。
        // Leave automatically cloned child triggers to PostgreSQL during restoration.
        var cloneFilter = capabilities.SupportsPartitionTriggerClones ? "AND t.tgparentid = 0" : string.Empty;
        var sql = $"""
            SELECT n.nspname, c.relname, t.tgname, pg_catalog.pg_get_triggerdef(t.oid, true), t.tgrelid
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
              {cloneFilter}
            ORDER BY n.nspname, c.relname, t.tgname
            """;
        var result = new List<TriggerInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (selectedOids.Contains(reader.GetFieldValue<uint>(4)))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }

    /// <summary>
    /// <para>対応するオブジェクトの所有者と、関数を識別する引数情報を取得します。</para>
    /// <para>Reads owners of supported objects and identity arguments for routines.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>オブジェクトの所有者一覧を返すタスク。 A task returning object ownership metadata.</returns>
    private static async Task<IReadOnlyList<OwnershipInfo>> ReadOwnershipAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 'Schema', n.nspname::text, n.nspname::text, NULL::text,
                   pg_catalog.pg_get_userbyid(n.nspowner)::text
            FROM pg_catalog.pg_namespace n
            WHERE n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
            UNION ALL
            SELECT 'Type', n.nspname::text, t.typname::text, NULL::text,
                   pg_catalog.pg_get_userbyid(t.typowner)::text
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'e'
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                  AND d.objid = t.oid AND d.deptype = 'e')
            UNION ALL
            SELECT CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END,
                   n.nspname::text, p.proname::text,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   pg_catalog.pg_get_userbyid(p.proowner)::text
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p')
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                  AND d.objid = p.oid AND d.deptype = 'e')
            UNION ALL
            SELECT CASE c.relkind WHEN 'S' THEN 'Sequence' WHEN 'v' THEN 'View' ELSE 'Table' END,
                   n.nspname::text, c.relname::text, NULL::text,
                   pg_catalog.pg_get_userbyid(c.relowner)::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'S', 'v')
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  AND d.objid = c.oid AND d.deptype = 'e')
            ORDER BY 1, 2, 3, 4
            """;
        var result = new List<OwnershipInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = Enum.Parse<SecuredObjectKind>(reader.GetString(0));
            var schema = GetNullableString(reader, 1);
            if (schema is not null && !selected.Contains(schema))
                continue;
            result.Add(new(
                kind, schema, reader.GetString(2), GetNullableString(reader, 3), reader.GetString(4)));
        }
        return result;
    }

    /// <summary>
    /// <para>明示されたオブジェクト権限と列権限を展開し、復元用にまとめます。</para>
    /// <para>Expands explicit object and column ACLs into restoration metadata.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="selected">選択されたスキーマ名の集合。 Set of selected schema names.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>オブジェクト・列の権限一覧を返すタスク。 A task returning object and column access controls.</returns>
    private static async Task<IReadOnlyList<AccessControlInfo>> ReadAccessControlsAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH objects AS (
                SELECT 'Schema'::text AS kind, n.nspname::text AS schema_name, n.nspname::text AS object_name,
                       NULL::text AS identity_arguments, NULL::text AS column_name,
                       n.nspowner AS owner_oid, n.nspacl AS acl
                FROM pg_catalog.pg_namespace n
                WHERE n.nspname <> 'information_schema' AND n.nspname !~ '^pg_' AND n.nspacl IS NOT NULL
                UNION ALL
                SELECT 'Type', n.nspname::text, t.typname::text, NULL::text, NULL::text,
                       t.typowner, t.typacl
                FROM pg_catalog.pg_type t
                JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
                WHERE t.typtype = 'e' AND t.typacl IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_depend d
                    WHERE d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                      AND d.objid = t.oid AND d.deptype = 'e')
                UNION ALL
                SELECT CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END,
                       n.nspname::text, p.proname::text,
                       pg_catalog.pg_get_function_identity_arguments(p.oid), NULL::text,
                       p.proowner, p.proacl
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE p.prokind IN ('f', 'p') AND p.proacl IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_depend d
                    WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                      AND d.objid = p.oid AND d.deptype = 'e')
                UNION ALL
                SELECT CASE c.relkind WHEN 'S' THEN 'Sequence' WHEN 'v' THEN 'View' ELSE 'Table' END,
                       n.nspname::text, c.relname::text, NULL::text, NULL::text,
                       c.relowner, c.relacl
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'S', 'v') AND c.relacl IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_depend d
                    WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                      AND d.objid = c.oid AND d.deptype = 'e')
                UNION ALL
                SELECT 'Table', n.nspname::text, c.relname::text, NULL::text, a.attname::text,
                       c.relowner, a.attacl
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v') AND a.attnum > 0 AND NOT a.attisdropped
                  AND a.attacl IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_depend d
                    WHERE d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                      AND d.objid = c.oid AND d.deptype = 'e')
            )
            SELECT o.kind, o.schema_name, o.object_name, o.identity_arguments, o.column_name,
                   pg_catalog.pg_get_userbyid(o.owner_oid)::text,
                   CASE WHEN acl.grantee = 0 THEN NULL ELSE pg_catalog.pg_get_userbyid(acl.grantee)::text END,
                   acl.privilege_type, acl.is_grantable
            FROM objects o
            LEFT JOIN LATERAL pg_catalog.aclexplode(o.acl) acl ON true
            ORDER BY o.kind, o.schema_name, o.object_name, o.identity_arguments, o.column_name,
                     acl.grantee, acl.privilege_type
            """;
        // 空ACLもLEFT JOINの行として残すことで、権限をすべて取り消した状態を復元できます。
        // Preserve empty ACLs as LEFT JOIN rows so fully revoked privilege states can be restored.
        var rows = new List<(SecuredObjectKind Kind, string? Schema, string Name, string? Arguments, string? Column,
            string Owner, string? Grantee, string? Privilege, bool Grantable)>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = GetNullableString(reader, 1);
            if (schema is not null && !selected.Contains(schema))
                continue;
            rows.Add((
                Enum.Parse<SecuredObjectKind>(reader.GetString(0)), schema, reader.GetString(2),
                GetNullableString(reader, 3), GetNullableString(reader, 4), reader.GetString(5),
                GetNullableString(reader, 6), GetNullableString(reader, 7),
                !reader.IsDBNull(8) && reader.GetBoolean(8)));
        }

        // 列名と関数の識別引数もグループキーに含め、別の列やオーバーロードを混同しません。
        // Include columns and routine identity arguments in grouping keys to distinguish columns and overloads.
        return rows
            .GroupBy(x => (x.Kind, x.Schema, x.Name, x.Arguments, x.Column, x.Owner))
            .Select(group => new AccessControlInfo(
                group.Key.Kind, group.Key.Schema, group.Key.Name, group.Key.Arguments, group.Key.Column,
                group.Key.Owner,
                group.Where(x => x.Privilege is not null)
                    .GroupBy(x => (x.Grantee, x.Privilege))
                    .Select(x => new PrivilegeInfo(
                        x.Key.Grantee, x.Key.Privilege!, x.Any(entry => entry.Grantable)))
                    .ToArray()))
            .ToArray();
    }

    /// <summary>
    /// <para>SQL NULLをnullに変換し、それ以外は指定列の文字列を返します。</para>
    /// <para>Returns null for SQL NULL or the string at the specified ordinal.</para>
    /// </summary>
    /// <param name="reader">テキストまたはカタログ行の読み取り元。 Reader supplying text or catalog rows.</param>
    /// <param name="ordinal">カタログ行の0始まりの列位置。 Zero-based column ordinal in a catalog row.</param>
    /// <returns>指定列の文字列、またはSQL NULLの場合はnull。 The column string, or null for SQL NULL.</returns>
    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// <para>データベースに限定されない、クラスタ共通のロール設定を取得します。</para>
    /// <para>Reads cluster-wide role settings that are not limited to a database.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>クラスタ共通ロール設定の一覧を返すタスク。 A task returning cluster-wide role settings.</returns>
    private static async Task<IReadOnlyList<RoleSettingInfo>> ReadRoleSettingsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        // setdatabase=0は全DB共通の設定です。最初の等号で分割し、値中の等号を保持します。
        // setdatabase=0 identifies cluster-wide settings; split at the first equals sign to preserve equals signs in values.
        const string sql = """
            SELECT r.rolname,
                   pg_catalog.split_part(config.value, '=', 1),
                   pg_catalog.substr(config.value, pg_catalog.strpos(config.value, '=') + 1)
            FROM pg_catalog.pg_db_role_setting setting
            JOIN pg_catalog.pg_roles r ON r.oid = setting.setrole
            CROSS JOIN LATERAL pg_catalog.unnest(setting.setconfig) AS config(value)
            WHERE setting.setdatabase = 0
              AND setting.setrole <> 0
              AND pg_catalog.strpos(config.value, '=') > 0
            ORDER BY r.rolname, config.value
            """;
        var result = new List<RoleSettingInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }
}

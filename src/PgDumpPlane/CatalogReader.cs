using Npgsql;

namespace PgDumpPlane;

internal static class CatalogReader
{
    internal static async Task<CatalogSnapshot> ReadAsync(
        NpgsqlConnection connection,
        PgDumpOptions options,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var database = await ReadDatabaseAsync(connection, capabilities, cancellationToken).ConfigureAwait(false);
        var schemas = await ReadSchemasAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var selected = schemas.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

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
        var triggers = await ReadTriggersAsync(connection, tableOids, cancellationToken).ConfigureAwait(false);

        return new(database, schemas, extensions, enums, routines, sequences, tables, views, constraints, indexes, triggers);
    }

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

    private static async Task<IReadOnlyList<ExtensionInfo>> ReadExtensionsAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.extname, n.nspname, e.extversion
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
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

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

    private static async Task<IReadOnlyList<RoutineInfo>> ReadRoutinesAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname, p.proname, pg_catalog.pg_get_functiondef(p.oid)
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p')
              AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                  AND d.objid = p.oid AND d.deptype = 'e')
            ORDER BY n.nspname, p.proname, p.oid
            """;
        var result = new List<RoutineInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            if (selected.Contains(schema))
                result.Add(new(schema, reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SequenceInfo>> ReadSequencesAsync(
        NpgsqlConnection connection,
        ISet<string> selected,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
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

    private static async Task<Dictionary<uint, IReadOnlyList<ColumnInfo>>> ReadColumnsAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
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
                   {compressionExpression}, {notNullFields}
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
                GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9), reader.GetBoolean(10)));
        }
        foreach (var pair in mutable)
            result.Add(pair.Key, pair.Value);
        return result;
    }

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
        var oids = raw.Select(x => x.Oid).ToHashSet();
        var dependencies = await ReadViewDependenciesAsync(connection, oids, cancellationToken).ConfigureAwait(false);
        return raw.Select(x => new ViewInfo(x.Oid, x.Schema, x.Name, x.Definition, x.Options,
            dependencies.GetValueOrDefault(x.Oid) ?? [])).ToArray();
    }

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
            JOIN pg_catalog.pg_class dependency ON dependency.oid = d.refobjid
            WHERE dependency.relkind = 'v' AND rw.ev_class <> d.refobjid
            """;
        var mutable = new Dictionary<uint, List<uint>>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var view = reader.GetFieldValue<uint>(0);
            var dependency = reader.GetFieldValue<uint>(1);
            if (!selectedOids.Contains(view) || !selectedOids.Contains(dependency))
                continue;
            if (!mutable.TryGetValue(view, out var list))
                mutable.Add(view, list = []);
            list.Add(dependency);
        }
        return mutable.ToDictionary(x => x.Key, x => (IReadOnlyList<uint>)x.Value);
    }

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

    private static async Task<IReadOnlyList<TriggerInfo>> ReadTriggersAsync(
        NpgsqlConnection connection,
        ISet<uint> selectedOids,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname, c.relname, t.tgname, pg_catalog.pg_get_triggerdef(t.oid, true), t.tgrelid
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
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

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

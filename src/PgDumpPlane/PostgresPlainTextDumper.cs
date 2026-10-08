using System.Data;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PgDumpPlane;

/// <summary>
/// <para>Npgsqlでサーバーを読み取り、PostgreSQLのプレーンテキストダンプを生成します。</para>
/// <para>Creates a PostgreSQL plain-text dump by querying the server through Npgsql.</para>
/// </summary>
public sealed class PostgresPlainTextDumper
{
    // PostgreSQLのGUC_LIST_QUOTE設定は各要素を引用します。全体を一つのリテラルにすると意味が変わります。
    // GUC_LIST_QUOTE settings need per-element quoting; quoting the entire value changes its meaning.
    private static readonly HashSet<string> ListQuotedRoleSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "local_preload_libraries",
        "oauth_validator_libraries",
        "search_path",
        "session_preload_libraries",
        "shared_preload_libraries",
        "temp_tablespaces",
        "unix_socket_directories"
    };

    /// <summary>
    /// <para><paramref name="connectionString"/>で指定されたDBをUTF-8ストリームへダンプします。</para>
    /// <para>Dumps a database identified by <paramref name="connectionString"/> to a UTF-8 stream.</para>
    /// </summary>
    /// <param name="connectionString">対象DBのNpgsql接続文字列。 Npgsql connection string for the target database.</param>
    /// <param name="destination">ダンプの出力先。 Dump output destination.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task DumpAsync(
        string connectionString,
        Stream destination,
        PgDumpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new PgDumpOptions();
        options.Validate();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await DumpToStreamAsync(connection, destination, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>Npgsqlデータソースから開いたDBをUTF-8ストリームへダンプします。</para>
    /// <para>Dumps a database from an Npgsql data source to a UTF-8 stream.</para>
    /// </summary>
    /// <param name="dataSource">接続を開くためのNpgsqlデータソース。 Npgsql data source used to open a connection.</param>
    /// <param name="destination">ダンプの出力先。 Dump output destination.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task DumpAsync(
        NpgsqlDataSource dataSource,
        Stream destination,
        PgDumpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new PgDumpOptions();
        options.Validate();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await DumpToStreamAsync(connection, destination, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>既存接続からダンプします。必要なら接続を開き、このメソッドでは破棄しません。完了まで接続を他のコマンドと共有しないでください。</para>
    /// <para>Dumps through an existing connection. The connection is opened if necessary and is never disposed. No other command may use it until this operation completes.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="destination">ダンプの出力先。 Dump output destination.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task DumpAsync(
        NpgsqlConnection connection,
        TextWriter destination,
        PgDumpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new PgDumpOptions();
        options.Validate();

        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DumpCoreAsync(connection, destination, options, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync().ConfigureAwait(false);
            if (!options.LeaveOpen)
                await destination.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>UTF-8・LFのライターでダンプを書き込み、残りの出力をフラッシュします。</para>
    /// <para>Writes the dump through a UTF-8/LF writer and flushes buffered output.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="destination">ダンプの出力先。 Dump output destination.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task DumpToStreamAsync(
        NpgsqlConnection connection,
        Stream destination,
        PgDumpOptions options,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, options.LeaveOpen)
        {
            NewLine = "\n"
        };
        await DumpCoreAsync(connection, writer, options, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>読み取り専用スナップショットから定義・データ・権限を順に出力します。</para>
    /// <para>Writes definitions, data, and security from a read-only snapshot in restoration order.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task DumpCoreAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        PgDumpOptions options,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open.");
        if (connection.Database is null)
            throw new InvalidOperationException("The connection must select a database.");

        var capabilities = PostgresVersionCapabilities.Create(connection.PostgreSqlVersion);
        await ConfigureSessionAsync(connection, capabilities, cancellationToken).ConfigureAwait(false);
        // 定義とテーブル行を一つのスナップショットから取得します。ただしシーケンス値はMVCCの対象外です。
        // Read definitions and table rows from one snapshot; sequence state is outside MVCC.
        var isolation = options.SerializableDeferrable ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead;
        await using var transaction = await connection.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);
        try
        {
            var transactionMode = options.SerializableDeferrable
                ? "SET TRANSACTION READ ONLY, DEFERRABLE"
                : "SET TRANSACTION READ ONLY";
            await ExecuteAsync(connection, transactionMode, cancellationToken).ConfigureAwait(false);

            var snapshot = await CatalogReader.ReadAsync(connection, options, capabilities, cancellationToken).ConfigureAwait(false);

            var restrictKey = options.UsePsqlRestrict ? RandomNumberGenerator.GetHexString(32) : null;
            await WriteHeaderAsync(writer, snapshot.Database, restrictKey).ConfigureAwait(false);

            // 定義→データ→制約の順に出力し、参照先の存在とデータ投入時の制約作成順序を維持します。
            // Emit definitions, data, then constraints so objects exist before loading and constraints are created afterward.
            if (options.IncludeSchema)
                await WritePreDataAsync(writer, snapshot).ConfigureAwait(false);

            if (options.IncludeData)
            {
                await WriteTableDataAsync(connection, writer, snapshot, options, cancellationToken).ConfigureAwait(false);
                await WriteSequenceDataAsync(connection, writer, snapshot, cancellationToken).ConfigureAwait(false);
            }

            if (options.IncludeSchema)
            {
                await WritePostDataAsync(writer, snapshot).ConfigureAwait(false);
                await WriteSecurityAsync(writer, snapshot).ConfigureAwait(false);
            }

            await WriteRoleSettingsAsync(writer, snapshot.RoleSettings).ConfigureAwait(false);

            if (restrictKey is not null)
                await writer.WriteAsync($"\\unrestrict {restrictKey}\n\n").ConfigureAwait(false);
            await writer.WriteAsync("--\n-- PostgreSQL database dump complete\n--\n").ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // キャンセル済みトークンで後片付けまで中止しないよう、ロールバックには無効化されないトークンを使います。
            // Use a non-cancelled token for rollback so cancellation does not prevent cleanup.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// <para>型の文字列表現と名前解決を固定し、ダンプ読み取り用のセッションを設定します。</para>
    /// <para>Configures the source session for consistent type rendering and name resolution.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="capabilities">接続先サーバーのバージョン別機能。 Version-specific capabilities of the connected server.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task ConfigureSessionAsync(
        NpgsqlConnection connection,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        // 空のsearch_pathでサーバーの定義出力を修飾し、標準文字列のバックスラッシュ解釈を固定します。
        // An empty search_path forces qualification in rendered definitions; standard strings fix backslash semantics.
        var sql = """
            SET statement_timeout = 0;
            SET lock_timeout = 0;
            SET idle_in_transaction_session_timeout = 0;
            SET DateStyle = ISO;
            SET IntervalStyle = postgres;
            SET standard_conforming_strings = on;
            SET extra_float_digits = 3;
            SET synchronize_seqscans = off;
            SET row_security = off;
            SELECT pg_catalog.set_config('search_path', '', false);
            """;
        if (capabilities.SupportsTransactionTimeout)
            sql += "\nSET transaction_timeout = 0;";
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>指定されたSQLを接続上で非同期に実行します。</para>
    /// <para>Executes the specified SQL asynchronously on the connection.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>作成元バージョン、任意のpsqlガード、復元用セッション設定を出力します。</para>
    /// <para>Writes source-version headers, optional psql guards, and restore session settings.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="database">対象DB名またはそのメタデータ。 Target database name or metadata.</param>
    /// <param name="restrictKey">psqlガードの対応キー。nullの場合はガードなし。 Matching psql guard key, or null to omit guards.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteHeaderAsync(TextWriter writer, DatabaseInfo database, string? restrictKey)
    {
        await writer.WriteAsync($"--\n{PgDumpFormat.HeaderTitle}\n--\n\n").ConfigureAwait(false);
        if (restrictKey is not null)
            await writer.WriteAsync($"\\restrict {restrictKey}\n\n").ConfigureAwait(false);
        await writer.WriteAsync($"{PgDumpFormat.SourceVersionPrefix}{database.ServerVersion}\n").ConfigureAwait(false);
        await writer.WriteAsync($"{PgDumpFormat.ProducerVersionPrefix}{PgDumpFormat.ProducerVersion}\n\n").ConfigureAwait(false);
        await writer.WriteAsync("SET statement_timeout = 0;\nSET lock_timeout = 0;\nSET idle_in_transaction_session_timeout = 0;\n").ConfigureAwait(false);
        if (database.ServerMajorVersion >= 17)
            await writer.WriteAsync("SET transaction_timeout = 0;\n").ConfigureAwait(false);
        await writer.WriteAsync("SET client_encoding = 'UTF8';\nSET standard_conforming_strings = on;\nSELECT pg_catalog.set_config('search_path', '', false);\n").ConfigureAwait(false);
        await writer.WriteAsync("SET check_function_bodies = false;\nSET xmloption = content;\nSET client_min_messages = warning;\nSET row_security = off;\n\n").ConfigureAwait(false);
    }

    /// <summary>
    /// <para>データ投入前に必要なスキーマ、型、関数、シーケンス、テーブルを出力します。</para>
    /// <para>Writes schemas, types, routines, sequences, and tables needed before loading data.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">出力対象のカタログ情報。 Catalog metadata to write.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    internal static async Task WritePreDataAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        await SectionAsync(writer, "PRE-DATA").ConfigureAwait(false);
        var owners = snapshot.Ownership.ToDictionary(x => (x.Kind, x.Schema, x.Name, x.IdentityArguments));
        foreach (var schema in snapshot.Schemas.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, schema.Name, "SCHEMA", null,
                owners.GetValueOrDefault((SecuredObjectKind.Schema, schema.Name, schema.Name, null))?.Owner).ConfigureAwait(false);
            if (schema.Name != "public")
                await writer.WriteAsync($"CREATE SCHEMA {SqlText.Identifier(schema.Name)};\n\n").ConfigureAwait(false);
            await WriteOwnershipAsync(writer,
                owners.GetValueOrDefault((SecuredObjectKind.Schema, schema.Name, schema.Name, null))).ConfigureAwait(false);
        }

        // VERSIONを固定しないことで、復元先にインストール可能な既定バージョンを使用します。
        // Omit VERSION to use the default extension version available on the destination.
        foreach (var extension in snapshot.Extensions.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, extension.Name, "EXTENSION", null, null).ConfigureAwait(false);
            await writer.WriteAsync($"CREATE EXTENSION IF NOT EXISTS {SqlText.Identifier(extension.Name)} WITH SCHEMA {SqlText.Identifier(extension.Schema)};\n\n").ConfigureAwait(false);
        }

        foreach (var item in snapshot.Enums.OrderBy(x => x.Schema, StringComparer.Ordinal)
                     .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, item.Name, "TYPE", item.Schema,
                owners.GetValueOrDefault((SecuredObjectKind.Type, item.Schema, item.Name, null))?.Owner).ConfigureAwait(false);
            var labels = string.Join(", ", item.Labels.Select(SqlText.Literal));
            await writer.WriteAsync($"CREATE TYPE {SqlText.Qualified(item.Schema, item.Name)} AS ENUM ({labels});\n\n").ConfigureAwait(false);
            await WriteOwnershipAsync(writer,
                owners.GetValueOrDefault((SecuredObjectKind.Type, item.Schema, item.Name, null))).ConfigureAwait(false);
        }

        foreach (var routine in snapshot.Routines)
        {
            await WriteObjectHeaderAsync(writer, $"{routine.Name}({routine.IdentityArguments})",
                OwnershipKeyword(routine.Kind), routine.Schema,
                owners.GetValueOrDefault((routine.Kind, routine.Schema, routine.Name, routine.IdentityArguments))?.Owner).ConfigureAwait(false);
            var definition = routine.Definition.TrimEnd();
            await writer.WriteAsync(definition).ConfigureAwait(false);
            if (!definition.EndsWith(';'))
                await writer.WriteAsync(';').ConfigureAwait(false);
            await writer.WriteAsync("\n\n").ConfigureAwait(false);
            await WriteOwnershipAsync(writer,
                owners.GetValueOrDefault((routine.Kind, routine.Schema, routine.Name, routine.IdentityArguments))).ConfigureAwait(false);
        }

        // SERIALの既定値は後置し、テーブル→所有シーケンス→OWNED BY→DEFAULTの順を保ちます。
        // Defer SERIAL defaults to preserve table, owned sequence, OWNED BY, then DEFAULT order.
        var ownedSequences = snapshot.Sequences.Where(x => !x.IsIdentity && x.OwnedTableName is not null)
            .Select(x => x.Oid).ToHashSet();
        var deferredDefaults = snapshot.Tables.SelectMany(table => table.Columns
            .Where(column => column.Generated == '\0' && column.Identity == '\0' &&
                column.DefaultExpression is not null && column.SequenceDependencies.Any(ownedSequences.Contains))
            .Select(column => (Table: table, Column: column))).ToArray();
        var deferredColumns = deferredDefaults.Select(x => (x.Table.Oid, x.Column.Name)).ToHashSet();

        foreach (var relation in OrderRelations(snapshot, deferredColumns))
        {
            if (relation.Table is { } table)
            {
                await WriteObjectHeaderAsync(writer, table.Name, "TABLE", table.Schema,
                    owners.GetValueOrDefault((SecuredObjectKind.Table, table.Schema, table.Name, null))?.Owner).ConfigureAwait(false);
                var definition = table with
                {
                    Columns = table.Columns.Select(column => deferredColumns.Contains((table.Oid, column.Name))
                        ? column with { DefaultExpression = null } : column).ToArray()
                };
                await WriteTableAsync(writer, definition).ConfigureAwait(false);
                await WriteOwnershipAsync(writer,
                    owners.GetValueOrDefault((SecuredObjectKind.Table, table.Schema, table.Name, null))).ConfigureAwait(false);
            }
            else if (relation.Sequence is { } sequence)
            {
                var owner = owners.GetValueOrDefault((SecuredObjectKind.Sequence, sequence.Schema, sequence.Name, null))?.Owner;
                await WriteObjectHeaderAsync(writer, sequence.Name, "SEQUENCE", sequence.Schema, owner).ConfigureAwait(false);
                await WriteSequenceAsync(writer, sequence).ConfigureAwait(false);
                await WriteOwnershipAsync(writer,
                    owners.GetValueOrDefault((SecuredObjectKind.Sequence, sequence.Schema, sequence.Name, null))).ConfigureAwait(false);
                if (sequence.OwnedTableName is not null)
                {
                    await WriteObjectHeaderAsync(writer, sequence.Name, "SEQUENCE OWNED BY", sequence.Schema, owner).ConfigureAwait(false);
                    await writer.WriteAsync($"ALTER SEQUENCE {SqlText.Qualified(sequence.Schema, sequence.Name)} OWNED BY " +
                        $"{SqlText.Qualified(sequence.OwnedTableSchema!, sequence.OwnedTableName)}.{SqlText.Identifier(sequence.OwnedColumn!)};\n\n").ConfigureAwait(false);
                }
            }
            else if (relation.View is { } view)
            {
                await WriteObjectHeaderAsync(writer, view.Name, "VIEW", view.Schema,
                    owners.GetValueOrDefault((SecuredObjectKind.View, view.Schema, view.Name, null))?.Owner).ConfigureAwait(false);
                var options = string.IsNullOrWhiteSpace(view.Options) ? string.Empty : $" WITH ({view.Options})";
                await writer.WriteAsync($"CREATE VIEW {SqlText.Qualified(view.Schema, view.Name)}{options} AS\n{view.Definition.TrimEnd()};\n\n").ConfigureAwait(false);
                await WriteOwnershipAsync(writer,
                    owners.GetValueOrDefault((SecuredObjectKind.View, view.Schema, view.Name, null))).ConfigureAwait(false);
            }
        }

        foreach (var (table, column) in deferredDefaults.OrderBy(x => x.Table.Schema, StringComparer.Ordinal)
                     .ThenBy(x => x.Table.Name, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, $"{table.Name} {column.Name}", "DEFAULT", table.Schema,
                owners.GetValueOrDefault((SecuredObjectKind.Table, table.Schema, table.Name, null))?.Owner).ConfigureAwait(false);
            await writer.WriteAsync($"ALTER TABLE ONLY {SqlText.Qualified(table.Schema, table.Name)} " +
                $"ALTER COLUMN {SqlText.Identifier(column.Name)} SET DEFAULT {column.DefaultExpression};\n\n").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>pg_dump形式のオブジェクト見出しを出力し、改行によるコメント外へのSQL混入を防ぎます。</para>
    /// <para>Writes a pg_dump-style object heading, preventing embedded newlines from escaping the SQL comment.</para>
    /// </summary>
    /// <param name="writer">出力先。 Output writer.</param>
    /// <param name="name">オブジェクトの表示名。 Object display name.</param>
    /// <param name="type">オブジェクト種別。 Object type.</param>
    /// <param name="schema">スキーマ名。nullは適用なし。 Schema name, or null when not applicable.</param>
    /// <param name="owner">所有者名。nullは所有者出力なし。 Owner name, or null when ownership is omitted.</param>
    /// <param name="data">データ用見出しかどうか。 Whether this is a data heading.</param>
    /// <returns>出力完了を表すタスク。 A task representing output completion.</returns>
    internal static Task WriteObjectHeaderAsync(
        TextWriter writer, string name, string type, string? schema, string? owner, bool data = false)
    {
        // PostgreSQLのsanitize_lineと同様、名前内の改行を空白に変えます。
        // Like PostgreSQL's sanitize_line, replace line breaks in names with spaces.
        static string SingleLine(string? value) => string.IsNullOrEmpty(value) ? "-" :
            value.Replace('\r', ' ').Replace('\n', ' ');
        return writer.WriteAsync($"--\n-- {(data ? "Data for " : string.Empty)}Name: {SingleLine(name)}; " +
            $"Type: {SingleLine(type)}; Schema: {SingleLine(schema)}; Owner: {SingleLine(owner)}\n--\n\n");
    }

    /// <summary>
    /// <para>通常のシーケンスの型、範囲、増分、キャッシュ設定を出力します。</para>
    /// <para>Writes a standalone sequence with its type, bounds, increment, and cache settings.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="sequence">シーケンスの定義。 Sequence metadata.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteSequenceAsync(TextWriter writer, SequenceInfo sequence)
    {
        var unlogged = sequence.Unlogged ? "UNLOGGED " : string.Empty;
        await writer.WriteAsync($"CREATE {unlogged}SEQUENCE {SqlText.Qualified(sequence.Schema, sequence.Name)}\n").ConfigureAwait(false);
        if (!string.Equals(sequence.DataType, "bigint", StringComparison.OrdinalIgnoreCase))
            await writer.WriteAsync($"    AS {sequence.DataType}\n").ConfigureAwait(false);
        await writer.WriteAsync($"    START WITH {SqlText.Number(sequence.Start)}\n").ConfigureAwait(false);
        await writer.WriteAsync($"    INCREMENT BY {SqlText.Number(sequence.Increment)}\n").ConfigureAwait(false);
        await writer.WriteAsync($"    MINVALUE {SqlText.Number(sequence.Minimum)}\n").ConfigureAwait(false);
        await writer.WriteAsync($"    MAXVALUE {SqlText.Number(sequence.Maximum)}\n").ConfigureAwait(false);
        await writer.WriteAsync($"    CACHE {SqlText.Number(sequence.Cache)}{(sequence.Cycle ? "\n    CYCLE" : string.Empty)};\n\n").ConfigureAwait(false);
    }

    /// <summary>
    /// <para>通常テーブルまたはパーティションのCREATE文と列プロパティを出力します。</para>
    /// <para>Writes CREATE statements and column properties for tables or partitions.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="table">テーブルまたはパーティションの定義。 Table or partition metadata.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableAsync(TextWriter writer, TableInfo table)
    {
        var qualified = SqlText.Qualified(table.Schema, table.Name);
        // 宣言的パーティションは親の列定義を継承するため、列一覧を再定義せずPARTITION OFを使います。
        // Declarative partitions inherit column definitions; use PARTITION OF rather than redefining columns.
        if (table.ParentOid is not null && table.ParentSchema is not null && table.ParentName is not null && table.PartitionBound is not null)
        {
            await writer.WriteAsync($"CREATE TABLE {qualified} PARTITION OF {SqlText.Qualified(table.ParentSchema, table.ParentName)} {table.PartitionBound}").ConfigureAwait(false);
            await WriteTableTailAsync(writer, table).ConfigureAwait(false);
            await WriteColumnPropertiesAsync(writer, table).ConfigureAwait(false);
            return;
        }

        var unlogged = table.Unlogged ? "UNLOGGED " : string.Empty;
        await writer.WriteAsync($"CREATE {unlogged}TABLE {qualified} (\n").ConfigureAwait(false);
        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            await writer.WriteAsync($"    {SqlText.Identifier(column.Name)} {column.DataType}").ConfigureAwait(false);
            if (column.Collation is not null)
                await writer.WriteAsync($" COLLATE {column.Collation}").ConfigureAwait(false);
            // 生成列・IDENTITY・通常のDEFAULTは排他的です。生成列の式をDEFAULTとして出力してはいけません。
            // Generated, identity, and ordinary DEFAULT clauses are mutually exclusive; generated expressions are not defaults.
            if (column.Generated is 's' or 'v')
                await writer.WriteAsync($" GENERATED ALWAYS AS ({column.DefaultExpression}){(column.Generated == 's' ? " STORED" : string.Empty)}").ConfigureAwait(false);
            else if (column.Identity is 'a' or 'd')
                await writer.WriteAsync($" GENERATED {(column.Identity == 'a' ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY").ConfigureAwait(false);
            else if (column.DefaultExpression is not null)
                await writer.WriteAsync($" DEFAULT {column.DefaultExpression}").ConfigureAwait(false);
            if (column.NotNull)
            {
                if (column.NotNullConstraintName is not null)
                    await writer.WriteAsync($" CONSTRAINT {SqlText.Identifier(column.NotNullConstraintName)}").ConfigureAwait(false);
                await writer.WriteAsync(" NOT NULL").ConfigureAwait(false);
                if (column.NotNullNoInherit)
                    await writer.WriteAsync(" NO INHERIT").ConfigureAwait(false);
            }
            await writer.WriteAsync(index + 1 == table.Columns.Count ? "\n" : ",\n").ConfigureAwait(false);
        }
        await writer.WriteAsync(")").ConfigureAwait(false);
        if (table.Kind == 'p' && table.PartitionKey is not null)
            await writer.WriteAsync($" PARTITION BY {table.PartitionKey}").ConfigureAwait(false);
        await WriteTableTailAsync(writer, table).ConfigureAwait(false);
        await WriteColumnPropertiesAsync(writer, table).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>テーブル定義末尾に保存オプションとテーブルスペースを追加します。</para>
    /// <para>Appends storage options and tablespace clauses to a table definition.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="table">テーブルまたはパーティションの定義。 Table or partition metadata.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableTailAsync(TextWriter writer, TableInfo table)
    {
        if (!string.IsNullOrWhiteSpace(table.RelOptions))
            await writer.WriteAsync($" WITH ({table.RelOptions})").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(table.Tablespace))
            await writer.WriteAsync($" TABLESPACE {SqlText.Identifier(table.Tablespace)}").ConfigureAwait(false);
        await writer.WriteAsync(";\n\n").ConfigureAwait(false);
    }

    /// <summary>
    /// <para>テーブル作成後に列単位の圧縮設定を出力します。</para>
    /// <para>Writes per-column compression settings after table creation.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="table">テーブルまたはパーティションの定義。 Table or partition metadata.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteColumnPropertiesAsync(TextWriter writer, TableInfo table)
    {
        var qualified = SqlText.Qualified(table.Schema, table.Name);
        foreach (var column in table.Columns.Where(x => x.Compression is not null))
        {
            await writer.WriteAsync(
                $"ALTER TABLE ONLY {qualified} ALTER COLUMN {SqlText.Identifier(column.Name)} " +
                $"SET COMPRESSION {column.Compression};\n").ConfigureAwait(false);
        }

        if (table.Columns.Any(x => x.Compression is not null))
            await writer.WriteAsync('\n').ConfigureAwait(false);
    }

    /// <summary>
    /// <para>通常テーブルのデータを選択されたCOPYまたはINSERT形式で出力します。</para>
    /// <para>Writes ordinary table data in the selected COPY or INSERT format.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">テーブルと所有者を含むカタログ情報。 Catalog metadata including tables and owners.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableDataAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        CatalogSnapshot snapshot,
        PgDumpOptions options,
        CancellationToken cancellationToken)
    {
        await SectionAsync(writer, "DATA").ConfigureAwait(false);
        foreach (var table in snapshot.Tables.Where(x => x.Kind == 'r' && (options.IncludeUnloggedTableData || !x.Unlogged))
                     .OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var owner = snapshot.Ownership.FirstOrDefault(x => x.Kind == SecuredObjectKind.Table &&
                x.Schema == table.Schema && x.Name == table.Name)?.Owner;
            await WriteObjectHeaderAsync(writer, table.Name, "TABLE DATA", table.Schema, owner, data: true).ConfigureAwait(false);
            if (options.DataFormat == PgDumpDataFormat.Inserts)
                await WriteTableInsertsAsync(connection, writer, table, cancellationToken).ConfigureAwait(false);
            else
                await WriteTableCopyAsync(connection, writer, table, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>生成列を除いたテーブルデータをCOPYテキストとして逐次転送します。</para>
    /// <para>Streams table data as COPY text, excluding generated columns.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="table">テーブルまたはパーティションの定義。 Table or partition metadata.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableCopyAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        TableInfo table,
        CancellationToken cancellationToken)
    {
        // 生成列は復元先で計算されるため、COPYの入出力列から除外します。
        // Exclude generated columns from COPY because the destination computes them.
        var columns = table.Columns.Where(x => x.Generated == '\0').Select(x => x.Name).ToArray();
        if (columns.Length == 0)
            return;

        var columnList = string.Join(", ", columns.Select(SqlText.Identifier));
        var qualified = SqlText.Qualified(table.Schema, table.Name);
        var copy = $"COPY {qualified} ({columnList}) TO STDOUT";
        await writer.WriteAsync($"COPY {qualified} ({columnList}) FROM stdin;\n").ConfigureAwait(false);
        await using var reader = await connection.BeginTextExportAsync(copy, cancellationToken).ConfigureAwait(false);
        await CopyTextAsync(reader, writer, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("\\.\n\n").ConfigureAwait(false);
    }

    /// <summary>
    /// <para>サーバーで値をSQLリテラル化し、行単位のINSERTを出力します。</para>
    /// <para>Uses server-side SQL literal formatting to write one INSERT per row.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="table">テーブルまたはパーティションの定義。 Table or partition metadata.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableInsertsAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        TableInfo table,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(x => x.Generated == '\0').ToArray();
        var qualified = SqlText.Qualified(table.Schema, table.Name);
        // 型ごとのSQL表現とNULLの扱いをquote_nullableに任せ、.NETの文字列変換による情報損失を避けます。
        // Use quote_nullable for type-aware SQL and NULL formatting rather than .NET string conversions.
        var selectList = columns.Length == 0
            ? "1"
            : string.Join(", ", columns.Select(x => $"pg_catalog.quote_nullable({SqlText.Identifier(x.Name)})"));

        await using var command = new NpgsqlCommand($"SELECT {selectList} FROM {qualified}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (columns.Length == 0)
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                await writer.WriteAsync($"INSERT INTO {qualified} DEFAULT VALUES;\n").ConfigureAwait(false);
        }
        else
        {
            var columnList = string.Join(", ", columns.Select(x => SqlText.Identifier(x.Name)));
            // GENERATED ALWAYS列にも保存された値を挿入するため、明示的な上書き指定を付けます。
            // Use an explicit override to insert saved values into GENERATED ALWAYS identity columns.
            var overriding = columns.Any(x => x.Identity == 'a') ? " OVERRIDING SYSTEM VALUE" : string.Empty;
            var prefix = $"INSERT INTO {qualified} ({columnList}){overriding} VALUES (";
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(prefix).ConfigureAwait(false);
                for (var index = 0; index < columns.Length; index++)
                {
                    if (index > 0)
                        await writer.WriteAsync(", ").ConfigureAwait(false);
                    await writer.WriteAsync(reader.GetString(index)).ConfigureAwait(false);
                }
                await writer.WriteAsync(");\n").ConfigureAwait(false);
            }
        }

        await writer.WriteAsync('\n').ConfigureAwait(false);
    }

    /// <summary>
    /// <para>シーケンスの現在値と使用済みフラグをsetval呼び出しとして出力します。</para>
    /// <para>Writes setval calls preserving sequence values and the is_called flag.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">シーケンスと所有者を含むカタログ情報。 Catalog metadata including sequences and owners.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteSequenceDataAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var sequence in snapshot.Sequences.OrderBy(x => x.Schema, StringComparer.Ordinal)
                     .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var owner = snapshot.Ownership.FirstOrDefault(x => x.Kind == SecuredObjectKind.Sequence &&
                x.Schema == sequence.Schema && x.Name == sequence.Name)?.Owner;
            await WriteObjectHeaderAsync(writer, sequence.Name, "SEQUENCE SET", sequence.Schema, owner).ConfigureAwait(false);
            var qualified = SqlText.Qualified(sequence.Schema, sequence.Name);
            await using var command = new NpgsqlCommand($"SELECT last_value, is_called FROM {qualified}", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Sequence {qualified} did not return its state.");
            var lastValue = reader.GetInt64(0);
            // is_called=falseでは次のnextvalがlast_valueそのものを返すため、このフラグも必ず保存します。
            // When is_called is false, nextval returns last_value itself; preserve this flag as well.
            var isCalled = reader.GetBoolean(1);
            var regclass = SqlText.Literal(qualified);
            await writer.WriteAsync($"SELECT pg_catalog.setval({regclass}, {SqlText.Number(lastValue)}, {SqlText.Boolean(isCalled)});\n\n").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>データ投入後に制約、索引、トリガー、外部キーを出力します。</para>
    /// <para>Writes constraints, indexes, triggers, and foreign keys after data loading.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">出力対象のカタログ情報。 Catalog metadata to write.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WritePostDataAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        await SectionAsync(writer, "POST-DATA").ConfigureAwait(false);
        var partitionedTables = snapshot.Tables
            .Where(x => x.Kind == 'p')
            .Select(x => (x.Schema, x.Name))
            .ToHashSet();
        var tableOwners = snapshot.Ownership.Where(x => x.Kind == SecuredObjectKind.Table)
            .ToDictionary(x => (x.Schema, x.Name), x => x.Owner);
        // 参照される主キー・一意制約と索引を先に作り、外部キーを後から追加します。
        // Create referenced primary and unique keys and indexes before adding foreign keys.
        // pg_dumpは同じ種別内でスキーマ・オブジェクト名を比較します。テーブル名やPK優先ではありません。
        // Within an object type, pg_dump compares schema and object name, not table name or PK priority.
        foreach (var constraint in snapshot.Constraints.Where(x => x.Type != 'f')
                     .OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
                     .ThenBy(x => x.Table, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, $"{constraint.Table} {constraint.Name}",
                constraint.Type == 'c' ? "CHECK CONSTRAINT" : "CONSTRAINT", constraint.Schema,
                tableOwners.GetValueOrDefault((constraint.Schema, constraint.Table))).ConfigureAwait(false);
            var only = partitionedTables.Contains((constraint.Schema, constraint.Table)) ? string.Empty : "ONLY ";
            await writer.WriteAsync($"ALTER TABLE {only}{SqlText.Qualified(constraint.Schema, constraint.Table)} ADD CONSTRAINT " +
                $"{SqlText.Identifier(constraint.Name)} {constraint.Definition};\n\n").ConfigureAwait(false);
        }
        foreach (var index in snapshot.Indexes.OrderBy(x => x.Schema, StringComparer.Ordinal)
                     .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, index.Name, "INDEX", index.Schema,
                tableOwners.GetValueOrDefault((index.Schema, index.Table))).ConfigureAwait(false);
            await writer.WriteAsync($"{index.Definition};\n").ConfigureAwait(false);
            if (index.Clustered)
                await writer.WriteAsync($"ALTER TABLE {SqlText.Qualified(index.Schema, index.Table)} CLUSTER ON {SqlText.Identifier(index.Name)};\n").ConfigureAwait(false);
            if (index.ReplicaIdentity)
                await writer.WriteAsync($"ALTER TABLE ONLY {SqlText.Qualified(index.Schema, index.Table)} REPLICA IDENTITY USING INDEX {SqlText.Identifier(index.Name)};\n").ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
        }
        foreach (var trigger in snapshot.Triggers.OrderBy(x => x.Schema, StringComparer.Ordinal)
                     .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Table, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, $"{trigger.Table} {trigger.Name}", "TRIGGER", trigger.Schema,
                tableOwners.GetValueOrDefault((trigger.Schema, trigger.Table))).ConfigureAwait(false);
            await writer.WriteAsync($"{trigger.Definition};\n\n").ConfigureAwait(false);
        }
        foreach (var constraint in snapshot.Constraints.Where(x => x.Type == 'f')
                     .OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
                     .ThenBy(x => x.Table, StringComparer.Ordinal))
        {
            await WriteObjectHeaderAsync(writer, $"{constraint.Table} {constraint.Name}", "FK CONSTRAINT", constraint.Schema,
                tableOwners.GetValueOrDefault((constraint.Schema, constraint.Table))).ConfigureAwait(false);
            var only = partitionedTables.Contains((constraint.Schema, constraint.Table)) ? string.Empty : "ONLY ";
            await writer.WriteAsync($"ALTER TABLE {only}{SqlText.Qualified(constraint.Schema, constraint.Table)} ADD CONSTRAINT " +
                $"{SqlText.Identifier(constraint.Name)} {constraint.Definition};\n\n").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>pg_dumpと同様に、定義とデータの出力後に明示的権限を再構成します。</para>
    /// <para>Reconstructs explicit privileges after definitions and data, matching pg_dump's ACL pass.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">出力対象のカタログ情報。 Catalog metadata to write.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    internal static async Task WriteSecurityAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        if (snapshot.AccessControls.Count == 0)
            return;

        await SectionAsync(writer, "PRIVILEGES").ConfigureAwait(false);

        // テーブル単位のREVOKEは列権限も取り消すため、テーブルACLを先に再構成します。
        // A table-level REVOKE also removes column privileges, so table ACLs must
        // be reset before any column ACLs are reconstructed.
        foreach (var accessControl in snapshot.AccessControls.OrderBy(x => x.Column is null ? 0 : 1))
            await WriteAccessControlAsync(writer, accessControl).ConfigureAwait(false);

        await writer.WriteAsync('\n').ConfigureAwait(false);
    }

    /// <summary>
    /// <para>pg_dumpの_printTocEntryと同様に、作成したオブジェクトの直後で所有者を設定します。</para>
    /// <para>Sets ownership immediately after an object's definition, like pg_dump's _printTocEntry.</para>
    /// </summary>
    /// <param name="writer">SQLの出力先。 SQL output writer.</param>
    /// <param name="ownership">対象の所有者情報。nullの場合は出力しません。 Ownership metadata; null emits nothing.</param>
    /// <returns>出力完了を表すタスク。 A task representing completion of output.</returns>
    private static async Task WriteOwnershipAsync(TextWriter writer, OwnershipInfo? ownership)
    {
        if (ownership is null)
            return;
        var identity = SecurityObjectIdentity(
            ownership.Kind, ownership.Schema, ownership.Name, ownership.IdentityArguments);
        await writer.WriteAsync(
            $"ALTER {OwnershipKeyword(ownership.Kind)} {identity} OWNER TO {SqlText.Identifier(ownership.Owner)};\n\n")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <para>ロールを作成せずに、クラスタ共通のALTER ROLE SET文を出力します。</para>
    /// <para>Writes cluster-wide ALTER ROLE SET statements without creating roles.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="roleSettings">クラスタ共通のロール設定一覧。 Cluster-wide role settings.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    internal static async Task WriteRoleSettingsAsync(
        TextWriter writer,
        IReadOnlyList<RoleSettingInfo> roleSettings)
    {
        if (roleSettings.Count == 0)
            return;

        await SectionAsync(writer, "ROLE SETTINGS").ConfigureAwait(false);
        foreach (var setting in roleSettings)
        {
            await writer.WriteAsync(
                $"ALTER ROLE {SqlText.Identifier(setting.Role)} SET {SqlText.Identifier(setting.Name)} TO " +
                $"{RoleSettingValueSql(setting)};\n").ConfigureAwait(false);
        }
        await writer.WriteAsync('\n').ConfigureAwait(false);
    }

    /// <summary>
    /// <para>設定の種類に応じ、単一値または個別に引用したリスト要素を返します。</para>
    /// <para>Formats a setting as a scalar literal or individually quoted list elements.</para>
    /// </summary>
    /// <param name="setting">値を整形するロール設定。 Role setting whose value is formatted.</param>
    /// <returns>設定値を表すSQLリテラル一覧。空リストの場合はNULL。 SQL literals representing the value, or NULL for an empty list.</returns>
    private static string RoleSettingValueSql(RoleSettingInfo setting)
    {
        if (!ListQuotedRoleSettings.Contains(setting.Name))
            return SqlText.Literal(setting.Value);

        // search_pathのカンマを一つの文字列に閉じ込めず、識別子リストとして個別に引用します。
        // Quote search_path elements separately so commas retain their list-separator meaning.
        var values = SplitGucList(setting.Value);
        return values.Count == 0
            ? "NULL"
            : string.Join(", ", values.Select(SqlText.Literal));
    }

    /// <summary>
    /// <para>GUC設定のカンマ区切り値を、二重引用符とそのエスケープを考慮して分割します。</para>
    /// <para>Splits a comma-separated GUC value while honoring double quotes and doubled-quote escapes.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>引用符を外し、二重引用符のエスケープを戻したリスト要素。 List elements with delimiters removed and doubled quotes decoded.</returns>
    private static IReadOnlyList<string> SplitGucList(string value)
    {
        var values = new List<string>();
        var index = 0;

        while (true)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;
            if (index == value.Length)
                return values;

            var item = new StringBuilder();
            if (value[index] == '"')
            {
                index++;
                var closed = false;
                while (index < value.Length)
                {
                    if (value[index] != '"')
                    {
                        item.Append(value[index++]);
                        continue;
                    }

                    index++;
                    if (index < value.Length && value[index] == '"')
                    {
                        item.Append('"');
                        index++;
                        continue;
                    }

                    closed = true;
                    break;
                }

                if (!closed)
                    throw new InvalidDataException($"Invalid list value for role setting: {value}");
            }
            else
            {
                while (index < value.Length && value[index] != ',' && !char.IsWhiteSpace(value[index]))
                    item.Append(value[index++]);
                if (item.Length == 0)
                    throw new InvalidDataException($"Invalid list value for role setting: {value}");
            }

            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;
            if (index == value.Length)
            {
                values.Add(item.ToString());
                return values;
            }
            if (value[index] != ',')
                throw new InvalidDataException($"Invalid list value for role setting: {value}");

            values.Add(item.ToString());
            index++;
        }
    }

    /// <summary>
    /// <para>復元時の既定権限を取り消してから、保存したGRANTを再構成します。</para>
    /// <para>Revokes restoration defaults before reconstructing the saved GRANT statements.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="accessControl">所有者と明示的権限を含むACL情報。 ACL metadata including the owner and explicit privileges.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteAccessControlAsync(TextWriter writer, AccessControlInfo accessControl)
    {
        var name = accessControl.Kind is SecuredObjectKind.Function or SecuredObjectKind.Procedure
            ? $"{accessControl.Name}({accessControl.IdentityArguments})" : accessControl.Name;
        await WriteObjectHeaderAsync(writer, $"{OwnershipKeyword(accessControl.Kind)} {name}" +
            (accessControl.Column is null ? string.Empty : $" COLUMN {accessControl.Column}"),
            "ACL", accessControl.Kind == SecuredObjectKind.Schema ? null : accessControl.Schema,
            accessControl.Owner).ConfigureAwait(false);
        var keyword = PrivilegeKeyword(accessControl.Kind);
        var identity = SecurityObjectIdentity(
            accessControl.Kind, accessControl.Schema, accessControl.Name, accessControl.IdentityArguments);
        var column = accessControl.Column is null
            ? string.Empty
            : $" ({SqlText.Identifier(accessControl.Column)})";

        await writer.WriteAsync(
            $"REVOKE ALL PRIVILEGES{column} ON {keyword} {identity} FROM CURRENT_USER;\n")
            .ConfigureAwait(false);
        // 復元で生じた既定権限を消すため、保存ACLだけでなくPUBLIC・所有者・実行ユーザーもリセット対象にします。
        // Reset PUBLIC, the owner, and the restore user as well as saved grantees to remove restoration defaults.
        var grantees = accessControl.Privileges
            .Select(x => x.Grantee)
            .Append(null)
            .Append(accessControl.Owner)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var grantee in grantees)
        {
            var role = grantee is null ? "PUBLIC" : SqlText.Identifier(grantee);
            await writer.WriteAsync(
                $"REVOKE ALL PRIVILEGES{column} ON {keyword} {identity} FROM {role};\n")
                .ConfigureAwait(false);
        }

        foreach (var privilege in accessControl.Privileges)
        {
            var role = privilege.Grantee is null ? "PUBLIC" : SqlText.Identifier(privilege.Grantee);
            var grantOption = privilege.IsGrantable ? " WITH GRANT OPTION" : string.Empty;
            await writer.WriteAsync(
                $"GRANT {privilege.Privilege}{column} ON {keyword} {identity} TO {role}{grantOption};\n")
                .ConfigureAwait(false);
        }
        await writer.WriteAsync('\n').ConfigureAwait(false);
    }

    /// <summary>
    /// <para>オブジェクト種別に応じ、引用済みの名前と関数の引数一覧を組み立てます。</para>
    /// <para>Builds a quoted object identity, including routine identity arguments when needed.</para>
    /// </summary>
    /// <param name="kind">対象オブジェクトの種別。 Target object kind.</param>
    /// <param name="schema">スキーマ名。 Schema name.</param>
    /// <param name="name">対象の名前。 Target name.</param>
    /// <param name="identityArguments">オーバーロードを識別する関数・プロシージャの引数。 Routine arguments identifying an overload.</param>
    /// <returns>引用済みのオブジェクト名。関数とプロシージャの場合は識別引数付き。 Quoted object name, including identity arguments for functions and procedures.</returns>
    private static string SecurityObjectIdentity(
        SecuredObjectKind kind,
        string? schema,
        string name,
        string? identityArguments)
    {
        if (kind == SecuredObjectKind.Schema)
            return SqlText.Identifier(name);

        var qualified = SqlText.Qualified(schema!, name);
        return kind is SecuredObjectKind.Function or SecuredObjectKind.Procedure
            ? $"{qualified}({identityArguments})"
            : qualified;
    }

    /// <summary>
    /// <para>所有者変更文に使うオブジェクト種別のSQLキーワードを返します。</para>
    /// <para>Returns the object-type SQL keyword used for ownership changes.</para>
    /// </summary>
    /// <param name="kind">対象オブジェクトの種別。 Target object kind.</param>
    /// <returns>ALTER OWNER文に対応するオブジェクト種別のキーワード。 The object-type keyword for an ALTER OWNER statement.</returns>
    private static string OwnershipKeyword(SecuredObjectKind kind) => kind switch
    {
        SecuredObjectKind.Schema => "SCHEMA",
        SecuredObjectKind.Type => "TYPE",
        SecuredObjectKind.Function => "FUNCTION",
        SecuredObjectKind.Procedure => "PROCEDURE",
        SecuredObjectKind.Table => "TABLE",
        SecuredObjectKind.Sequence => "SEQUENCE",
        SecuredObjectKind.View => "VIEW",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported secured object kind.")
    };

    /// <summary>
    /// <para>ビューをTABLEとして扱い、権限文に使うSQLキーワードを返します。</para>
    /// <para>Returns the privilege keyword, treating views as TABLE objects.</para>
    /// </summary>
    /// <param name="kind">対象オブジェクトの種別。 Target object kind.</param>
    /// <returns>GRANT・REVOKE文に対応するオブジェクト種別のキーワード。 The object-type keyword for GRANT and REVOKE statements.</returns>
    private static string PrivilegeKeyword(SecuredObjectKind kind) => kind switch
    {
        SecuredObjectKind.View => "TABLE",
        _ => OwnershipKeyword(kind)
    };

    /// <summary>
    /// <para>SQLコメント形式のセクション見出しを出力します。</para>
    /// <para>Writes a section heading as SQL comments.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="name">対象の名前。 Target name.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static Task SectionAsync(TextWriter writer, string name) =>
        writer.WriteAsync($"--\n-- {name}\n--\n\n");

    /// <summary>
    /// <para>プールした固定サイズバッファで、入力を出力へ逐次コピーします。</para>
    /// <para>Copies text incrementally using a pooled fixed-size buffer.</para>
    /// </summary>
    /// <param name="reader">テキストまたはカタログ行の読み取り元。 Reader supplying text or catalog rows.</param>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task CopyTextAsync(TextReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        // テーブル全体をメモリに保持せず、一定サイズのバッファを再利用して転送します。
        // Transfer through a reusable fixed-size buffer rather than buffering an entire table.
        var buffer = ArrayPool<char>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// <para>型を問わず同じ名前順で処理するリレーションと、その依存先です。</para>
    /// <para>A relation and its dependencies, ordered by name regardless of relation kind.</para>
    /// </summary>
    private sealed record RelationDefinition(
        uint Oid, string Schema, string Name, IReadOnlyList<uint> Dependencies,
        TableInfo? Table = null, SequenceInfo? Sequence = null, ViewInfo? View = null);

    /// <summary>
    /// <para>pg_dumpと同様に名前順を基準とし、必要な依存先をその前へ移動します。</para>
    /// <para>Uses pg_dump-style name ordering, moving required dependencies before their dependents.</para>
    /// </summary>
    /// <param name="snapshot">出力対象のカタログ。 Catalog selected for output.</param>
    /// <param name="deferredColumns">既定値を後置するテーブルOIDと列名。 Table OIDs and column names with deferred defaults.</param>
    /// <returns>テーブル・ビュー・通常シーケンスの作成順序。 Creation order of tables, views, and standalone sequences.</returns>
    private static IReadOnlyList<RelationDefinition> OrderRelations(
        CatalogSnapshot snapshot,
        ISet<(uint Oid, string Name)> deferredColumns)
    {
        var tableNames = snapshot.Tables.ToDictionary(x => (x.Schema, x.Name), x => x.Oid);
        var relations = snapshot.Tables.Select(table => new RelationDefinition(
                table.Oid, table.Schema, table.Name,
                (table.ParentOid is uint parent ? new[] { parent } : [])
                    .Concat(table.Columns.Where(column => !deferredColumns.Contains((table.Oid, column.Name)))
                        .SelectMany(column => column.SequenceDependencies)).Distinct().ToArray(),
                Table: table))
            .Concat(snapshot.Sequences.Where(sequence => !sequence.IsIdentity).Select(sequence =>
                new RelationDefinition(sequence.Oid, sequence.Schema, sequence.Name,
                    sequence.OwnedTableSchema is not null && sequence.OwnedTableName is not null &&
                    tableNames.TryGetValue((sequence.OwnedTableSchema, sequence.OwnedTableName), out var owner)
                        ? [owner] : [], Sequence: sequence)))
            .Concat(snapshot.Views.Select(view =>
                new RelationDefinition(view.Oid, view.Schema, view.Name, view.Dependencies, View: view)))
            .OrderBy(x => x.Schema, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Oid).ToArray();

        var byOid = relations.ToDictionary(x => x.Oid);
        var rank = relations.Select((relation, index) => (relation.Oid, index))
            .ToDictionary(x => x.Oid, x => x.index);
        var emitted = new HashSet<uint>();
        var visiting = new HashSet<uint>();
        var result = new List<RelationDefinition>(relations.Length);

        // 依存先のない全項目を先に出す方法では、名前順のビューなどを後ろへ押し出してしまいます。
        // Emitting every ready item first would push alphabetically early dependent views to the end.
        foreach (var relation in relations)
            Visit(relation);
        return result;

        // この項目を一度だけ出力し、選択済みの依存先を先に訪問します。
        // Emit this item once, visiting selected dependencies first.
        void Visit(RelationDefinition relation)
        {
            // 循環するビューのSQLを書き換える機能ではないため、再訪問を止めて復元先に検証を任せます。
            // Cyclic view SQL is not rewritten here; stop revisiting and leave validation to the restore server.
            if (emitted.Contains(relation.Oid) || !visiting.Add(relation.Oid))
                return;
            foreach (var dependency in relation.Dependencies.Where(byOid.ContainsKey).OrderBy(oid => rank[oid]))
                Visit(byOid[dependency]);
            visiting.Remove(relation.Oid);
            emitted.Add(relation.Oid);
            result.Add(relation);
        }
    }
}

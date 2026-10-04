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
                await WriteTableDataAsync(connection, writer, snapshot.Tables, options, cancellationToken).ConfigureAwait(false);
                await WriteSequenceDataAsync(connection, writer, snapshot.Sequences, cancellationToken).ConfigureAwait(false);
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
    private static async Task WritePreDataAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        await SectionAsync(writer, "PRE-DATA").ConfigureAwait(false);
        foreach (var schema in snapshot.Schemas.Where(x => x.Name != "public"))
            await writer.WriteAsync($"CREATE SCHEMA {SqlText.Identifier(schema.Name)};\n\n").ConfigureAwait(false);

        // VERSIONを固定しないことで、復元先にインストール可能な既定バージョンを使用します。
        // Omit VERSION to use the default extension version available on the destination.
        foreach (var extension in snapshot.Extensions)
        {
            await writer.WriteAsync($"CREATE EXTENSION IF NOT EXISTS {SqlText.Identifier(extension.Name)} WITH SCHEMA {SqlText.Identifier(extension.Schema)};\n\n").ConfigureAwait(false);
        }

        foreach (var item in snapshot.Enums)
        {
            var labels = string.Join(", ", item.Labels.Select(SqlText.Literal));
            await writer.WriteAsync($"CREATE TYPE {SqlText.Qualified(item.Schema, item.Name)} AS ENUM ({labels});\n\n").ConfigureAwait(false);
        }

        foreach (var routine in snapshot.Routines)
        {
            var definition = routine.Definition.TrimEnd();
            await writer.WriteAsync(definition).ConfigureAwait(false);
            if (!definition.EndsWith(';'))
                await writer.WriteAsync(';').ConfigureAwait(false);
            await writer.WriteAsync("\n\n").ConfigureAwait(false);
        }

        // IDENTITY用シーケンスは列定義によって作成されるため、独立したCREATEを出力しません。
        // Identity columns create their own sequences, so do not emit standalone CREATE statements for them.
        foreach (var sequence in snapshot.Sequences.Where(x => !x.IsIdentity))
            await WriteSequenceAsync(writer, sequence).ConfigureAwait(false);

        foreach (var table in TopologicalTables(snapshot.Tables))
            await WriteTableAsync(writer, table).ConfigureAwait(false);

        // OWNED BYはテーブル作成後に設定し、まだ存在しない所有列への参照を避けます。
        // Apply OWNED BY after table creation to avoid referencing a column that does not exist yet.
        foreach (var sequence in snapshot.Sequences.Where(x => !x.IsIdentity && x.OwnedTableName is not null))
        {
            await writer.WriteAsync($"ALTER SEQUENCE {SqlText.Qualified(sequence.Schema, sequence.Name)} OWNED BY " +
                $"{SqlText.Qualified(sequence.OwnedTableSchema!, sequence.OwnedTableName!)}.{SqlText.Identifier(sequence.OwnedColumn!)};\n\n").ConfigureAwait(false);
        }
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
    /// <param name="tables">出力対象テーブル一覧。 Tables selected for output.</param>
    /// <param name="options">ダンプ設定。公開APIではnullの場合に既定値を使用します。 Dump options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteTableDataAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        IReadOnlyList<TableInfo> tables,
        PgDumpOptions options,
        CancellationToken cancellationToken)
    {
        await SectionAsync(writer, "DATA").ConfigureAwait(false);
        foreach (var table in tables.Where(x => x.Kind == 'r' && (options.IncludeUnloggedTableData || !x.Unlogged)))
        {
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
        await writer.WriteAsync($"-- Data for Name: {table.Name}; Schema: {table.Schema}\n\nCOPY {qualified} ({columnList}) FROM stdin;\n").ConfigureAwait(false);
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

        await writer.WriteAsync($"-- Data for Name: {table.Name}; Schema: {table.Schema}\n\n").ConfigureAwait(false);
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
    /// <param name="sequences">状態を出力するシーケンス一覧。 Sequences whose state is written.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task WriteSequenceDataAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        IReadOnlyList<SequenceInfo> sequences,
        CancellationToken cancellationToken)
    {
        foreach (var sequence in sequences)
        {
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
    /// <para>データ投入後に制約、索引、ビュー、トリガーを依存順に出力します。</para>
    /// <para>Writes constraints, indexes, views, and triggers after data loading.</para>
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
        // 参照される主キー・一意制約と索引を先に作り、外部キーを後から追加します。
        // Create referenced primary and unique keys and indexes before adding foreign keys.
        foreach (var constraint in snapshot.Constraints.Where(x => x.Type != 'f'))
        {
            var only = partitionedTables.Contains((constraint.Schema, constraint.Table)) ? string.Empty : "ONLY ";
            await writer.WriteAsync($"ALTER TABLE {only}{SqlText.Qualified(constraint.Schema, constraint.Table)} ADD CONSTRAINT " +
                $"{SqlText.Identifier(constraint.Name)} {constraint.Definition};\n\n").ConfigureAwait(false);
        }
        foreach (var index in snapshot.Indexes)
        {
            await writer.WriteAsync($"{index.Definition};\n").ConfigureAwait(false);
            if (index.Clustered)
                await writer.WriteAsync($"ALTER TABLE {SqlText.Qualified(index.Schema, index.Table)} CLUSTER ON {SqlText.Identifier(index.Name)};\n").ConfigureAwait(false);
            if (index.ReplicaIdentity)
                await writer.WriteAsync($"ALTER TABLE ONLY {SqlText.Qualified(index.Schema, index.Table)} REPLICA IDENTITY USING INDEX {SqlText.Identifier(index.Name)};\n").ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
        }
        foreach (var constraint in snapshot.Constraints.Where(x => x.Type == 'f'))
        {
            var only = partitionedTables.Contains((constraint.Schema, constraint.Table)) ? string.Empty : "ONLY ";
            await writer.WriteAsync($"ALTER TABLE {only}{SqlText.Qualified(constraint.Schema, constraint.Table)} ADD CONSTRAINT " +
                $"{SqlText.Identifier(constraint.Name)} {constraint.Definition};\n\n").ConfigureAwait(false);
        }
        foreach (var view in TopologicalViews(snapshot.Views))
        {
            var options = string.IsNullOrWhiteSpace(view.Options) ? string.Empty : $" WITH ({view.Options})";
            await writer.WriteAsync($"CREATE VIEW {SqlText.Qualified(view.Schema, view.Name)}{options} AS\n{view.Definition.TrimEnd()};\n\n").ConfigureAwait(false);
        }
        foreach (var trigger in snapshot.Triggers)
            await writer.WriteAsync($"{trigger.Definition};\n\n").ConfigureAwait(false);
    }

    /// <summary>
    /// <para>権限を再構成した後、スキーマ所有者を最後に変更します。</para>
    /// <para>Reconstructs privileges, then transfers ownership with schemas last.</para>
    /// </summary>
    /// <param name="writer">SQLまたはテキストの出力先。 Destination for SQL or text output.</param>
    /// <param name="snapshot">出力対象のカタログ情報。 Catalog metadata to write.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    internal static async Task WriteSecurityAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        if (snapshot.AccessControls.Count == 0 && snapshot.Ownership.Count == 0)
            return;

        await SectionAsync(writer, "OWNERSHIP AND PRIVILEGES").ConfigureAwait(false);

        // テーブル単位のREVOKEは列権限も取り消すため、テーブルACLを先に再構成します。
        // A table-level REVOKE also removes column privileges, so table ACLs must
        // be reset before any column ACLs are reconstructed.
        foreach (var accessControl in snapshot.AccessControls.OrderBy(x => x.Column is null ? 0 : 1))
            await WriteAccessControlAsync(writer, accessControl).ConfigureAwait(false);

        // 先にスキーマ所有者を変更すると内部オブジェクトの移管に必要な権限を失う可能性があります。
        // Transfer contained objects first. Changing a schema owner earlier can remove
        // permissions needed to finish transferring the objects inside it.
        foreach (var ownership in snapshot.Ownership.OrderBy(x => OwnershipOrder(x.Kind)))
        {
            var identity = SecurityObjectIdentity(
                ownership.Kind, ownership.Schema, ownership.Name, ownership.IdentityArguments);
            await writer.WriteAsync(
                $"ALTER {OwnershipKeyword(ownership.Kind)} {identity} OWNER TO {SqlText.Identifier(ownership.Owner)};\n")
                .ConfigureAwait(false);
        }
        await writer.WriteAsync('\n').ConfigureAwait(false);
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
    /// <para>内部オブジェクトを先に、スキーマを最後にする所有者変更の順序を返します。</para>
    /// <para>Ranks ownership changes so contained objects precede schemas.</para>
    /// </summary>
    /// <param name="kind">対象オブジェクトの種別。 Target object kind.</param>
    /// <returns>通常オブジェクトは0、スキーマは1。 Zero for contained objects and one for schemas.</returns>
    private static int OwnershipOrder(SecuredObjectKind kind) => kind switch
    {
        SecuredObjectKind.Schema => 1,
        _ => 0
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
    /// <para>親テーブルを子テーブルより先に作成できる順序を返します。</para>
    /// <para>Orders tables so parents can be created before children.</para>
    /// </summary>
    /// <param name="items">依存順序で並べ替える項目一覧。 Items to order by their dependencies.</param>
    /// <returns>親テーブルを先に配置したテーブル一覧。 Tables ordered with parents first.</returns>
    private static IReadOnlyList<TableInfo> TopologicalTables(IReadOnlyList<TableInfo> items) =>
        TopologicalSort(items, x => x.Oid, x => x.ParentOid is uint parent ? [parent] : []);

    /// <summary>
    /// <para>参照先ビューを参照元より先に作成できる順序を返します。</para>
    /// <para>Orders views so referenced views precede their dependents.</para>
    /// </summary>
    /// <param name="items">依存順序で並べ替える項目一覧。 Items to order by their dependencies.</param>
    /// <returns>選択された依存先を先に配置したビュー一覧。 Views ordered with selected dependencies first.</returns>
    private static IReadOnlyList<ViewInfo> TopologicalViews(IReadOnlyList<ViewInfo> items) =>
        TopologicalSort(items, x => x.Oid, x => x.Dependencies);

    /// <summary>
    /// <para>選択された依存先を先に並べ、循環時も決定的な順序で処理を継続します。</para>
    /// <para>Orders selected dependencies first and uses deterministic fallback ordering for cycles.</para>
    /// </summary>
    /// <param name="items">依存順序で並べ替える項目一覧。 Items to order by their dependencies.</param>
    /// <param name="key">項目のOIDを取得する関数。 Function returning an item's OID.</param>
    /// <param name="dependencies">項目が依存するOID一覧を取得する関数。 Function returning an item's dependency OIDs.</param>
    /// <returns>依存先を先に配置した一覧。循環があれば決定的な順序で続行します。 Items ordered with dependencies first and deterministic fallback for cycles.</returns>
    /// <typeparam name="T">依存順に並べる項目の型。 Type of items ordered by dependency.</typeparam>
    private static IReadOnlyList<T> TopologicalSort<T>(
        IReadOnlyList<T> items,
        Func<T, uint> key,
        Func<T, IReadOnlyList<uint>> dependencies)
    {
        var remaining = items.ToDictionary(key);
        var emitted = new HashSet<uint>();
        var result = new List<T>(items.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(x => dependencies(x).All(d => emitted.Contains(d) || !remaining.ContainsKey(d)))
                .OrderBy(key)
                .ToArray();
            if (ready.Length == 0)
            {
                // 循環を解決したことにはせず、最小OIDを選んで出力を決定的にし、復元時にサーバーへ検証を任せます。
                // Views can be mutually recursive. Keep deterministic output and let PostgreSQL report it on restore.
                ready = [remaining.Values.OrderBy(key).First()];
            }
            foreach (var item in ready)
            {
                var id = key(item);
                remaining.Remove(id);
                emitted.Add(id);
                result.Add(item);
            }
        }
        return result;
    }
}

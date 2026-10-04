using System.Data;
using System.Text;
using Npgsql;

namespace PgDumpPlane;

/// <summary>
/// <para>Npgsql経由でPgDumpPlaneまたはpg_dumpのプレーンテキストダンプを復元します。</para>
/// <para>Restores a PgDumpPlane or native pg_dump plain-text dump through Npgsql.</para>
/// </summary>
public sealed class PostgresPlainTextRestorer
{
    /// <summary>
    /// <para>ファイルが対応するPostgreSQLダンプヘッダーを持つか確認します。SQL本体の検証ではありません。</para>
    /// <para>Checks whether a file has a supported PostgreSQL plain-text dump header.</para>
    /// </summary>
    /// <param name="path">読み取り対象ファイルのパス。 Path to the input file.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>対応ヘッダーならtrueを返すタスク。SQL本体の妥当性は検証しません。 A task returning true for a supported header; it does not validate the SQL body.</returns>
    public async Task<bool> IsValidDumpFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = CreateReader(stream, leaveOpen: false);
        try
        {
            _ = await PgDumpFormat.ReadAndValidateHeaderAsync(reader, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// <para>UTF-8ダンプファイルのヘッダーを検証し、指定されたDBへ復元します。</para>
    /// <para>Validates and restores a UTF-8 dump file into the specified database.</para>
    /// </summary>
    /// <param name="connectionString">対象DBのNpgsql接続文字列。 Npgsql connection string for the target database.</param>
    /// <param name="path">読み取り対象ファイルのパス。 Path to the input file.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task RestoreFileAsync(
        string connectionString,
        string path,
        PgRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await RestoreAsync(connectionString, stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para><paramref name="connectionString"/>で指定されたDBへUTF-8ダンプを復元します。</para>
    /// <para>Restores a UTF-8 dump into the database identified by <paramref name="connectionString"/>.</para>
    /// </summary>
    /// <param name="connectionString">対象DBのNpgsql接続文字列。 Npgsql connection string for the target database.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task RestoreAsync(
        string connectionString,
        Stream source,
        PgRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new PgRestoreOptions();
        options.Validate();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await RestoreFromStreamAsync(connection, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>Npgsqlデータソースから開いたDBへUTF-8ダンプを復元します。</para>
    /// <para>Restores a UTF-8 dump into a database opened from an Npgsql data source.</para>
    /// </summary>
    /// <param name="dataSource">接続を開くためのNpgsqlデータソース。 Npgsql data source used to open a connection.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task RestoreAsync(
        NpgsqlDataSource dataSource,
        Stream source,
        PgRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new PgRestoreOptions();
        options.Validate();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await RestoreFromStreamAsync(connection, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>既存接続へ復元します。必要なら接続を開き、このメソッドでは破棄しません。完了まで接続を他のコマンドと共有しないでください。</para>
    /// <para>Restores through an existing connection. The connection is opened if necessary and is never disposed. No other command may use it until this operation completes.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    public async Task RestoreAsync(
        NpgsqlConnection connection,
        TextReader source,
        PgRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new PgRestoreOptions();
        options.Validate();

        // 呼び出し元が開いた接続の寿命を維持し、このメソッドで開いた接続だけを閉じます。
        // Preserve caller-owned connection lifetime; close only connections opened by this method.
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await RestoreCoreAsync(connection, source, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync().ConfigureAwait(false);
            if (!options.LeaveOpen)
                source.Dispose();
        }
    }

    /// <summary>
    /// <para>ストリームをテキストリーダーに包み、指定された所有権設定で復元します。</para>
    /// <para>Wraps the stream in a text reader and restores using the requested ownership settings.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task RestoreFromStreamAsync(
        NpgsqlConnection connection,
        Stream source,
        PgRestoreOptions options,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(source, options.LeaveOpen);
        await RestoreCoreAsync(connection, reader, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>不正なUTF-8を例外にしつつ、BOM検出を有効にしたリーダーを作成します。</para>
    /// <para>Creates a reader with strict UTF-8 fallback and BOM detection enabled.</para>
    /// </summary>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="leaveOpen">ラッパーの破棄後も入力を開いたままにするか。 Whether to leave the input open when its wrapper is disposed.</param>
    /// <returns>入力を読み取るStreamReader。 The StreamReader wrapping the input.</returns>
    private static StreamReader CreateReader(Stream source, bool leaveOpen) =>
        new(
            source,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: leaveOpen);

    /// <summary>
    /// <para>ヘッダーを検証し、必要な互換変換を適用してSQLとCOPYデータを逐次復元します。</para>
    /// <para>Validates the header, applies required compatibility changes, and streams SQL and COPY data.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="options">復元設定。公開APIではnullの場合に既定値を使用します。 Restore options; null uses defaults in public APIs.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task RestoreCoreAsync(
        NpgsqlConnection connection,
        TextReader source,
        PgRestoreOptions options,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection must be open.");
        if (connection.Database is null)
            throw new InvalidOperationException("The connection must select a database.");
        var targetCapabilities = PostgresVersionCapabilities.Create(connection.PostgreSqlVersion);

        // トランザクション開始やファイル中のSQL実行より先にヘッダーを検証します。
        // Validate before opening a transaction or executing any content from the file.
        var header = await PgDumpFormat.ReadAndValidateHeaderAsync(source, cancellationToken).ConfigureAwait(false);
        // 比較には作成ツールの版ではなく、ダンプ元データベースのメジャーバージョンを使います。
        // Compare the source database major version, not the dump producer version.
        var compatibility = new RestoreCompatibilityProcessor(
            header.SourceMajorVersion,
            targetCapabilities.Major);

        await using var transaction = options.UseTransaction
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var parser = new SqlStatementAccumulator();
            string? line;
            while ((line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                // 関数のドル引用や文字列内のバックスラッシュ行を、psql命令と誤認しないようにします。
                // Do not mistake backslash lines inside dollar-quoted bodies or strings for psql directives.
                if (parser.CanReadPsqlCommand && IsPsqlCommand(line, out var supported))
                {
                    if (!supported)
                        throw new InvalidDataException($"Unsupported psql command in dump: {line.Trim()}");
                    continue;
                }

                var statements = parser.AppendLine(line);
                for (var index = 0; index < statements.Count; index++)
                {
                    var statement = compatibility.Process(statements[index]);
                    if (statement is null)
                        continue;
                    if (TryGetCopyCommand(statement, out var copyCommand))
                    {
                        // COPYの後はSQLではなく生データになるため、同じ行に続くSQL文は受け付けません。
                        // COPY switches to raw data; reject further SQL statements on the same line.
                        if (index + 1 != statements.Count)
                            throw new InvalidDataException("COPY FROM stdin must be the final statement on its line.");
                        await RestoreCopyAsync(connection, source, copyCommand, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ExecuteAsync(connection, transaction, statement, options.CommandTimeout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            parser.Complete();

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transaction is not null)
                // キャンセル後もロールバックを実行して、途中までの復元内容を取り消します。
                // Perform rollback even after cancellation to undo a partial restore.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// <para>指定されたSQLを接続上で非同期に実行します。</para>
    /// <para>Executes the specified SQL asynchronously on the connection.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="transaction">SQLを実行するトランザクション。nullの場合は指定なし。 Transaction for SQL execution, or null when none is specified.</param>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <param name="commandTimeout">SQL実行のタイムアウト秒数。0は無制限。 SQL command timeout in seconds; zero means unlimited.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = commandTimeout
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>COPY終端までをSQL解析せずに読み取り、テキストインポートへ渡します。</para>
    /// <para>Reads raw data through the COPY terminator and passes it to text import without SQL parsing.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="copyCommand">テキストインポート用のCOPY FROM stdin文。 COPY FROM stdin command used for text import.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task RestoreCopyAsync(
        NpgsqlConnection connection,
        TextReader source,
        string copyCommand,
        CancellationToken cancellationToken)
    {
        await using var writer = await connection.BeginTextImportAsync(copyCommand, cancellationToken).ConfigureAwait(false);
        writer.NewLine = "\n";
        while (true)
        {
            var line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new InvalidDataException("The dump ended before the COPY data terminator (\\.).");
            // 単独の\\.だけがCOPY終端です。引用符・セミコロンを含むデータ行は変更せず転送します。
            // Only a standalone \\. terminates COPY; forward data lines with quotes and semicolons unchanged.
            if (line == "\\.")
                break;
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>バックスラッシュで始まる行を検出し、対応するガード命令か判定します。</para>
    /// <para>Detects backslash commands and identifies supported guard directives.</para>
    /// </summary>
    /// <param name="line">現在処理するテキスト行。 Current input line.</param>
    /// <param name="supported">対応するpsqlガード命令の場合にtrueを返す出力値。 Output flag indicating a supported psql guard directive.</param>
    /// <returns>バックスラッシュ命令の行ならtrue。対応可否はsupportedで返します。 True for a backslash command line; supported reports whether it is recognized.</returns>
    private static bool IsPsqlCommand(string line, out bool supported)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '\\')
        {
            supported = false;
            return false;
        }

        // restrict/unrestrictはpsql向けの命令なので、NpgsqlではSQLとして実行せず読み飛ばします。
        // restrict/unrestrict are psql directives; Npgsql skips them instead of executing them as SQL.
        supported = trimmed.StartsWith("\\restrict ", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\unrestrict ", StringComparison.Ordinal);
        return true;
    }

    /// <summary>
    /// <para>完成した文がCOPY FROM stdinなら、インポート用に末尾のセミコロンを除きます。</para>
    /// <para>Recognizes a completed COPY FROM stdin statement and strips its trailing semicolon.</para>
    /// </summary>
    /// <param name="statement">セミコロンで終わる解析済みSQL文。 Parsed SQL statement ending with a semicolon.</param>
    /// <param name="copyCommand">テキストインポート用のCOPY FROM stdin文。 COPY FROM stdin command used for text import.</param>
    /// <returns>COPY FROM stdinを検出した場合はtrue。 True when a COPY FROM stdin statement is detected.</returns>
    private static bool TryGetCopyCommand(string statement, out string copyCommand)
    {
        var start = SkipLeadingTrivia(statement);
        var sql = statement.AsSpan(start).Trim();
        if (!sql.StartsWith("COPY", StringComparison.OrdinalIgnoreCase) ||
            (sql.Length > 4 && !char.IsWhiteSpace(sql[4])))
        {
            copyCommand = string.Empty;
            return false;
        }

        if (sql[^1] != ';')
        {
            copyCommand = string.Empty;
            return false;
        }
        sql = sql[..^1].TrimEnd();
        if (!sql.EndsWith("FROM stdin", StringComparison.OrdinalIgnoreCase))
        {
            copyCommand = string.Empty;
            return false;
        }

        copyCommand = sql.ToString();
        return true;
    }

    /// <summary>
    /// <para>先頭の空白とコメントを読み飛ばし、SQL本体の開始位置を返します。</para>
    /// <para>Skips leading whitespace and comments and returns the SQL start offset.</para>
    /// </summary>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <returns>コメントと空白の後にあるSQLの開始位置。 Offset of SQL after leading comments and whitespace.</returns>
    private static int SkipLeadingTrivia(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                index++;
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                var newline = sql.IndexOf('\n', index + 2);
                return newline < 0 ? sql.Length : SkipLeadingTrivia(sql[newline..]) + newline;
            }
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                return end < 0 ? sql.Length : SkipLeadingTrivia(sql[(end + 2)..]) + end + 2;
            }
            return index;
        }
        return index;
    }
}

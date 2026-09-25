using System.Data;
using System.Text;
using Npgsql;

namespace PgDumpPlane;

/// <summary>Restores a PgDumpPlane or native pg_dump plain-text dump through Npgsql.</summary>
public sealed class PostgresPlainTextRestorer
{
    /// <summary>Checks whether a file has a supported PostgreSQL plain-text dump header.</summary>
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

    /// <summary>Validates and restores a UTF-8 dump file into the specified database.</summary>
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

    /// <summary>Restores a UTF-8 dump into the database identified by <paramref name="connectionString"/>.</summary>
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

    /// <summary>Restores a UTF-8 dump into a database opened from an Npgsql data source.</summary>
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
    /// Restores through an existing connection. The connection is opened if necessary and is never disposed.
    /// No other command may use it until this operation completes.
    /// </summary>
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

    private static async Task RestoreFromStreamAsync(
        NpgsqlConnection connection,
        Stream source,
        PgRestoreOptions options,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(source, options.LeaveOpen);
        await RestoreCoreAsync(connection, reader, options, cancellationToken).ConfigureAwait(false);
    }

    private static StreamReader CreateReader(Stream source, bool leaveOpen) =>
        new(
            source,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: leaveOpen);

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
        _ = PostgresVersionCapabilities.Create(connection.PostgreSqlVersion);

        // Validate before opening a transaction or executing any content from the file.
        _ = await PgDumpFormat.ReadAndValidateHeaderAsync(source, cancellationToken).ConfigureAwait(false);

        await using var transaction = options.UseTransaction
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var parser = new SqlStatementAccumulator();
            string? line;
            while ((line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (parser.CanReadPsqlCommand && IsPsqlCommand(line, out var supported))
                {
                    if (!supported)
                        throw new InvalidDataException($"Unsupported psql command in dump: {line.Trim()}");
                    continue;
                }

                var statements = parser.AppendLine(line);
                for (var index = 0; index < statements.Count; index++)
                {
                    var statement = statements[index];
                    if (TryGetCopyCommand(statement, out var copyCommand))
                    {
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
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

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
            if (line == "\\.")
                break;
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsPsqlCommand(string line, out bool supported)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '\\')
        {
            supported = false;
            return false;
        }

        supported = trimmed.StartsWith("\\restrict ", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\unrestrict ", StringComparison.Ordinal);
        return true;
    }

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

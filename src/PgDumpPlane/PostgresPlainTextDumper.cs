using System.Data;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PgDumpPlane;

/// <summary>Creates a PostgreSQL plain-text dump by querying the server through Npgsql.</summary>
public sealed class PostgresPlainTextDumper
{
    /// <summary>Dumps a database identified by <paramref name="connectionString"/> to a UTF-8 stream.</summary>
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

    /// <summary>Dumps a database from an Npgsql data source to a UTF-8 stream.</summary>
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
    /// Dumps through an existing connection. The connection is opened if necessary and is never disposed.
    /// No other command may use it until this operation completes.
    /// </summary>
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

            if (restrictKey is not null)
                await writer.WriteAsync($"\\unrestrict {restrictKey}\n\n").ConfigureAwait(false);
            await writer.WriteAsync("--\n-- PostgreSQL database dump complete\n--\n").ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureSessionAsync(
        NpgsqlConnection connection,
        PostgresVersionCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var sql = """
            SET statement_timeout = 0;
            SET lock_timeout = 0;
            SET idle_in_transaction_session_timeout = 0;
            SET DateStyle = ISO;
            SET IntervalStyle = postgres;
            SET extra_float_digits = 3;
            SET synchronize_seqscans = off;
            SET row_security = off;
            SELECT pg_catalog.set_config('search_path', '', false);
            """;
        if (capabilities.SupportsTransactionTimeout)
            sql += "\nSET transaction_timeout = 0;";
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private static async Task WritePreDataAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        await SectionAsync(writer, "PRE-DATA").ConfigureAwait(false);
        foreach (var schema in snapshot.Schemas.Where(x => x.Name != "public"))
            await writer.WriteAsync($"CREATE SCHEMA {SqlText.Identifier(schema.Name)};\n\n").ConfigureAwait(false);

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

        foreach (var sequence in snapshot.Sequences.Where(x => !x.IsIdentity))
            await WriteSequenceAsync(writer, sequence).ConfigureAwait(false);

        foreach (var table in TopologicalTables(snapshot.Tables))
            await WriteTableAsync(writer, table).ConfigureAwait(false);

        foreach (var sequence in snapshot.Sequences.Where(x => !x.IsIdentity && x.OwnedTableName is not null))
        {
            await writer.WriteAsync($"ALTER SEQUENCE {SqlText.Qualified(sequence.Schema, sequence.Name)} OWNED BY " +
                $"{SqlText.Qualified(sequence.OwnedTableSchema!, sequence.OwnedTableName!)}.{SqlText.Identifier(sequence.OwnedColumn!)};\n\n").ConfigureAwait(false);
        }
    }

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

    private static async Task WriteTableAsync(TextWriter writer, TableInfo table)
    {
        var qualified = SqlText.Qualified(table.Schema, table.Name);
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

    private static async Task WriteTableTailAsync(TextWriter writer, TableInfo table)
    {
        if (!string.IsNullOrWhiteSpace(table.RelOptions))
            await writer.WriteAsync($" WITH ({table.RelOptions})").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(table.Tablespace))
            await writer.WriteAsync($" TABLESPACE {SqlText.Identifier(table.Tablespace)}").ConfigureAwait(false);
        await writer.WriteAsync(";\n\n").ConfigureAwait(false);
    }

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

    private static async Task WriteTableCopyAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        TableInfo table,
        CancellationToken cancellationToken)
    {
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

    private static async Task WriteTableInsertsAsync(
        NpgsqlConnection connection,
        TextWriter writer,
        TableInfo table,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(x => x.Generated == '\0').ToArray();
        var qualified = SqlText.Qualified(table.Schema, table.Name);
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
            var isCalled = reader.GetBoolean(1);
            var regclass = SqlText.Literal(qualified);
            await writer.WriteAsync($"SELECT pg_catalog.setval({regclass}, {SqlText.Number(lastValue)}, {SqlText.Boolean(isCalled)});\n\n").ConfigureAwait(false);
        }
    }

    private static async Task WritePostDataAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        await SectionAsync(writer, "POST-DATA").ConfigureAwait(false);
        var partitionedTables = snapshot.Tables
            .Where(x => x.Kind == 'p')
            .Select(x => (x.Schema, x.Name))
            .ToHashSet();
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

    internal static async Task WriteSecurityAsync(TextWriter writer, CatalogSnapshot snapshot)
    {
        if (snapshot.AccessControls.Count == 0 && snapshot.Ownership.Count == 0)
            return;

        await SectionAsync(writer, "OWNERSHIP AND PRIVILEGES").ConfigureAwait(false);

        // A table-level REVOKE also removes column privileges, so table ACLs must
        // be reset before any column ACLs are reconstructed.
        foreach (var accessControl in snapshot.AccessControls.OrderBy(x => x.Column is null ? 0 : 1))
            await WriteAccessControlAsync(writer, accessControl).ConfigureAwait(false);

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

    private static string PrivilegeKeyword(SecuredObjectKind kind) => kind switch
    {
        SecuredObjectKind.View => "TABLE",
        _ => OwnershipKeyword(kind)
    };

    private static int OwnershipOrder(SecuredObjectKind kind) => kind switch
    {
        SecuredObjectKind.Schema => 1,
        _ => 0
    };

    private static Task SectionAsync(TextWriter writer, string name) =>
        writer.WriteAsync($"--\n-- {name}\n--\n\n");

    private static async Task CopyTextAsync(TextReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
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

    private static IReadOnlyList<TableInfo> TopologicalTables(IReadOnlyList<TableInfo> items) =>
        TopologicalSort(items, x => x.Oid, x => x.ParentOid is uint parent ? [parent] : []);

    private static IReadOnlyList<ViewInfo> TopologicalViews(IReadOnlyList<ViewInfo> items) =>
        TopologicalSort(items, x => x.Oid, x => x.Dependencies);

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

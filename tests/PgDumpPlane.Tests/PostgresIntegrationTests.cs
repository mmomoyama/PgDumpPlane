using System.Text;
using Npgsql;

namespace PgDumpPlane.Tests;

public sealed class PostgresIntegrationTests
{
    [Fact]
    public async Task RestoreAsync_RejectsNonDumpBeforeExecutingSql()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"restore_invalid_{Guid.NewGuid():N}";
        var qualifiedSchema = SqlText.Identifier(schema);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new PostgresPlainTextRestorer().RestoreAsync(
                    connection,
                    new StringReader($"CREATE SCHEMA {qualifiedSchema};"),
                    cancellationToken: cancellationToken));

            await using var verify = new NpgsqlCommand(
                "SELECT pg_catalog.to_regnamespace(@schema) IS NULL", connection);
            verify.Parameters.AddWithValue("schema", schema);
            Assert.True((bool)(await verify.ExecuteScalarAsync(cancellationToken))!);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RestoreAsync_RollsBackTheWholeDumpOnFailure()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"restore_rollback_{Guid.NewGuid():N}";
        var qualifiedSchema = SqlText.Identifier(schema);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            var script = $$"""
                --
                -- PostgreSQL database dump
                --

                -- Dumped from database version {{connection.PostgreSqlVersion}}
                -- Dumped by PgDumpPlane test

                CREATE SCHEMA {{qualifiedSchema}};
                CREATE TABLE {{qualifiedSchema}}.item (id integer);
                SELECT 1 / 0;
                """;

            await Assert.ThrowsAsync<PostgresException>(() =>
                new PostgresPlainTextRestorer().RestoreAsync(
                    connection,
                    new StringReader(script),
                    cancellationToken: cancellationToken));

            await using var verify = new NpgsqlCommand(
                "SELECT pg_catalog.to_regnamespace(@schema) IS NULL", connection);
            verify.Parameters.AddWithValue("schema", schema);
            Assert.True((bool)(await verify.ExecuteScalarAsync(cancellationToken))!);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DumpAsync_StreamsRestorableObjectKindsAndTableData()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"dump_test_{Guid.NewGuid():N}";
        var qualifiedSchema = SqlText.Identifier(schema);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var serverMajor = connection.PostgreSqlVersion.Major;
        Assert.InRange(serverMajor, 12, 18);
        var allByteValues = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
        var largeBinary = Enumerable.Range(0, 256 * 1024).Select(x => (byte)(x * 31)).ToArray();
        string? alternateOwner = null;
        try
        {
            await using (var setup = new NpgsqlCommand($"""
                CREATE SCHEMA {qualifiedSchema};
                CREATE TYPE {qualifiedSchema}.mood AS ENUM ('happy', 'sad');
                CREATE TABLE {qualifiedSchema}.parent (
                    id bigint GENERATED ALWAYS AS IDENTITY,
                    label text NOT NULL,
                    state {qualifiedSchema}.mood DEFAULT 'happy',
                    amount numeric(12,2),
                    created_at timestamptz DEFAULT now(),
                    CONSTRAINT parent_pk PRIMARY KEY (id),
                    CONSTRAINT positive_amount CHECK (amount >= 0)
                ) PARTITION BY RANGE (id);
                CREATE TABLE {qualifiedSchema}.parent_first PARTITION OF {qualifiedSchema}.parent
                    FOR VALUES FROM (0) TO (1000);
                CREATE INDEX parent_label_idx ON {qualifiedSchema}.parent (label);
                CREATE FUNCTION {qualifiedSchema}.normalize_label() RETURNS trigger
                    LANGUAGE plpgsql AS $$ BEGIN NEW.label := lower(NEW.label); RETURN NEW; END $$;
                CREATE VIEW {qualifiedSchema}.parent_view AS SELECT id, label FROM {qualifiedSchema}.parent;
                CREATE TABLE {qualifiedSchema}.binary_data (
                    id integer PRIMARY KEY,
                    payload bytea
                );
                CREATE SEQUENCE {qualifiedSchema}.default_counter;
                CREATE TABLE {qualifiedSchema}.sequence_default (
                    id bigint DEFAULT nextval('{schema}.default_counter'::regclass)
                );
                ALTER SEQUENCE {qualifiedSchema}.default_counter
                    OWNED BY {qualifiedSchema}.sequence_default.id;
                GRANT SELECT ON TABLE {qualifiedSchema}.binary_data TO PUBLIC;
                GRANT UPDATE (payload) ON TABLE {qualifiedSchema}.binary_data TO PUBLIC;
                REVOKE EXECUTE ON FUNCTION {qualifiedSchema}.normalize_label() FROM PUBLIC;
                """, connection))
            {
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            var triggerTable = serverMajor == 12 ? "parent_first" : "parent";
            await using (var dataSetup = new NpgsqlCommand($"""
                CREATE TRIGGER normalize_label BEFORE INSERT ON {qualifiedSchema}.{SqlText.Identifier(triggerTable)}
                    FOR EACH ROW EXECUTE FUNCTION {qualifiedSchema}.normalize_label();
                INSERT INTO {qualifiedSchema}.parent (id, label, amount)
                    OVERRIDING SYSTEM VALUE VALUES (1, E'Hello\nworld', 12.50);
                """, connection))
            {
                await dataSetup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var binarySetup = new NpgsqlCommand($"""
                INSERT INTO {qualifiedSchema}.binary_data (id, payload)
                VALUES (1, NULL), (2, @empty), (3, @allByteValues), (4, @largeBinary)
                """, connection))
            {
                binarySetup.Parameters.AddWithValue("empty", Array.Empty<byte>());
                binarySetup.Parameters.AddWithValue("allByteValues", allByteValues);
                binarySetup.Parameters.AddWithValue("largeBinary", largeBinary);
                await binarySetup.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverMajor >= 14)
            {
                await using var compressionSetup = new NpgsqlCommand(
                    $"CREATE TABLE {qualifiedSchema}.compressed_data (payload text COMPRESSION pglz)", connection);
                await compressionSetup.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverMajor >= 15)
            {
                await using var version15Setup = new NpgsqlCommand($"""
                    CREATE UNLOGGED SEQUENCE {qualifiedSchema}.unlogged_counter;
                    CREATE TABLE {qualifiedSchema}.nulls_feature (value integer, UNIQUE NULLS NOT DISTINCT (value));
                    """, connection);
                await version15Setup.ExecuteNonQueryAsync(cancellationToken);
            }

            if (serverMajor >= 18)
            {
                await using var version18Setup = new NpgsqlCommand($"""
                    CREATE TABLE {qualifiedSchema}.generated_feature (
                        source integer CONSTRAINT source_required NOT NULL,
                        doubled integer GENERATED ALWAYS AS (source * 2)
                    );
                    INSERT INTO {qualifiedSchema}.generated_feature (source) VALUES (7);
                    """, connection);
                await version18Setup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var roleCheck = new NpgsqlCommand(
                "SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user", connection))
            {
                if ((bool)(await roleCheck.ExecuteScalarAsync(cancellationToken))!)
                {
                    alternateOwner = $"dump_owner_{Guid.NewGuid():N}";
                    var qualifiedOwner = SqlText.Identifier(alternateOwner);
                    await using var ownerSetup = new NpgsqlCommand($"""
                        CREATE ROLE {qualifiedOwner} NOLOGIN;
                        GRANT CREATE ON SCHEMA {qualifiedSchema} TO {qualifiedOwner};
                        ALTER TABLE {qualifiedSchema}.binary_data OWNER TO {qualifiedOwner};
                        """, connection);
                    await ownerSetup.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var options = new PgDumpOptions();
            options.IncludeSchemas.Add(schema);
            await using var stream = new MemoryStream();
            await new PostgresPlainTextDumper().DumpAsync(connection, new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true), options, cancellationToken);
            var script = Encoding.UTF8.GetString(stream.ToArray());

            Assert.Contains($"CREATE TYPE {qualifiedSchema}.\"mood\" AS ENUM ('happy', 'sad');", script);
            Assert.Contains($"CREATE TABLE {qualifiedSchema}.\"parent\"", script);
            Assert.Contains($"nextval('{schema}.default_counter'::regclass)", script);
            Assert.Contains($"CREATE TABLE {qualifiedSchema}.\"parent_first\" PARTITION OF", script);
            Assert.Contains($"COPY {qualifiedSchema}.\"parent_first\"", script);
            Assert.Contains("hello\\nworld", script);
            Assert.Contains("ADD CONSTRAINT \"parent_pk\" PRIMARY KEY", script);
            Assert.Contains("CREATE INDEX parent_label_idx", script);
            Assert.Contains($"CREATE VIEW {qualifiedSchema}.\"parent_view\"", script);
            Assert.Contains("CREATE TRIGGER normalize_label", script);
            Assert.Contains("pg_catalog.setval", script);
            Assert.Contains($"ALTER SCHEMA {qualifiedSchema} OWNER TO", script);
            Assert.Contains($"ALTER TABLE {qualifiedSchema}.\"binary_data\" OWNER TO", script);
            if (alternateOwner is not null)
            {
                Assert.Contains(
                    $"ALTER TABLE {qualifiedSchema}.\"binary_data\" OWNER TO {SqlText.Identifier(alternateOwner)};",
                    script);
            }
            Assert.Contains($"GRANT SELECT ON TABLE {qualifiedSchema}.\"binary_data\" TO PUBLIC;", script);
            Assert.Contains(
                $"GRANT UPDATE (\"payload\") ON TABLE {qualifiedSchema}.\"binary_data\" TO PUBLIC;",
                script);
            Assert.Contains(
                $"REVOKE ALL PRIVILEGES ON FUNCTION {qualifiedSchema}.\"normalize_label\"() FROM PUBLIC;",
                script);
            Assert.Contains($"{PgDumpFormat.ProducerVersionPrefix}{PgDumpFormat.ProducerVersion}", script);
            Assert.Contains("\\restrict ", script);
            Assert.Contains("\\unrestrict ", script);
            Assert.Equal(serverMajor >= 17, script.Contains("SET transaction_timeout = 0;", StringComparison.Ordinal));

            if (serverMajor >= 14)
                Assert.Contains("SET COMPRESSION pglz", script);
            if (serverMajor >= 15)
            {
                Assert.Contains($"CREATE UNLOGGED SEQUENCE {qualifiedSchema}.\"unlogged_counter\"", script);
                Assert.Contains("UNIQUE NULLS NOT DISTINCT", script);
            }
            if (serverMajor >= 18)
            {
                Assert.Contains("CONSTRAINT \"source_required\" NOT NULL", script);
                Assert.Contains("GENERATED ALWAYS AS ((source * 2))", script);
            }

            var insertOptions = new PgDumpOptions
            {
                DataFormat = PgDumpDataFormat.Inserts,
                UsePsqlRestrict = false
            };
            insertOptions.IncludeSchemas.Add(schema);
            await using var insertStream = new MemoryStream();
            await new PostgresPlainTextDumper().DumpAsync(
                connection,
                new StreamWriter(insertStream, new UTF8Encoding(false), leaveOpen: true),
                insertOptions,
                cancellationToken);
            var insertScript = Encoding.UTF8.GetString(insertStream.ToArray());

            Assert.DoesNotContain($"COPY {qualifiedSchema}.", insertScript);
            var parentInsertPrefix =
                $"INSERT INTO {qualifiedSchema}.\"parent_first\" (\"id\", \"label\", \"state\", \"amount\", \"created_at\")";
            Assert.Contains(
                parentInsertPrefix + (serverMajor >= 17 ? " OVERRIDING SYSTEM VALUE" : string.Empty) +
                " VALUES ('1', 'hello",
                insertScript);
            if (serverMajor >= 18)
            {
                Assert.Contains(
                    $"INSERT INTO {qualifiedSchema}.\"generated_feature\" (\"source\") VALUES ('7');",
                    insertScript);
            }

            await using (var dropSource = new NpgsqlCommand($"DROP SCHEMA {qualifiedSchema} CASCADE", connection))
                await dropSource.ExecuteNonQueryAsync(cancellationToken);
            await using var copyRestoreStream = new MemoryStream(Encoding.UTF8.GetBytes(script));
            await new PostgresPlainTextRestorer().RestoreAsync(
                connectionString,
                copyRestoreStream,
                cancellationToken: cancellationToken);
            await AssertRestoredDataAsync(
                connection, qualifiedSchema, serverMajor, allByteValues, largeBinary, cancellationToken);
            await AssertRestoredSecurityAsync(connection, schema, alternateOwner, cancellationToken);

            await using (var dropCopyRestore = new NpgsqlCommand($"DROP SCHEMA {qualifiedSchema} CASCADE", connection))
                await dropCopyRestore.ExecuteNonQueryAsync(cancellationToken);
            await new PostgresPlainTextRestorer().RestoreAsync(
                connection,
                new StringReader(insertScript),
                cancellationToken: cancellationToken);
            await AssertRestoredDataAsync(
                connection, qualifiedSchema, serverMajor, allByteValues, largeBinary, cancellationToken);
            await AssertRestoredSecurityAsync(connection, schema, alternateOwner, cancellationToken);
        }
        finally
        {
            var dropRole = alternateOwner is null
                ? string.Empty
                : $"; DROP ROLE IF EXISTS {SqlText.Identifier(alternateOwner)}";
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE{dropRole}", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task AssertRestoredDataAsync(
        NpgsqlConnection connection,
        string qualifiedSchema,
        int serverMajor,
        byte[] allByteValues,
        byte[] largeBinary,
        CancellationToken cancellationToken)
    {
        await using (var verify = new NpgsqlCommand(
            $"SELECT label FROM {qualifiedSchema}.parent_first WHERE id = 1", connection))
        {
            Assert.Equal("hello\nworld", await verify.ExecuteScalarAsync(cancellationToken));
        }
        if (serverMajor >= 18)
        {
            await using var verifyGenerated = new NpgsqlCommand(
                $"SELECT doubled FROM {qualifiedSchema}.generated_feature WHERE source = 7", connection);
            Assert.Equal(14, await verifyGenerated.ExecuteScalarAsync(cancellationToken));
        }
        await AssertBinaryRowsAsync(
            connection,
            $"{qualifiedSchema}.binary_data",
            allByteValues,
            largeBinary,
            cancellationToken);
    }

    private static async Task AssertRestoredSecurityAsync(
        NpgsqlConnection connection,
        string schema,
        string? expectedTableOwner,
        CancellationToken cancellationToken)
    {
        var table = $"{SqlText.Identifier(schema)}.\"binary_data\"";
        var routine = $"{SqlText.Identifier(schema)}.\"normalize_label\"()";
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_catalog.has_table_privilege('public', @table, 'SELECT'),
                   pg_catalog.has_column_privilege('public', @table, 'payload', 'UPDATE'),
                   pg_catalog.has_function_privilege('public', @routine, 'EXECUTE'),
                   (SELECT pg_catalog.pg_get_userbyid(c.relowner)
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema AND c.relname = 'binary_data')
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("routine", routine);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        if (expectedTableOwner is not null)
            Assert.Equal(expectedTableOwner, reader.GetString(3));
    }

    private static async Task AssertBinaryRowsAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        byte[] allByteValues,
        byte[] largeBinary,
        CancellationToken cancellationToken)
    {
        byte[]?[] expected = [null, [], allByteValues, largeBinary];
        await using var command = new NpgsqlCommand($"SELECT id, payload FROM {qualifiedTable} ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(index + 1, reader.GetInt32(0));
            if (expected[index] is null)
                Assert.True(reader.IsDBNull(1));
            else
                Assert.Equal(expected[index], reader.GetFieldValue<byte[]>(1));
        }
        Assert.False(await reader.ReadAsync(cancellationToken));
    }
}

using System.Text;
using Npgsql;

namespace PgDumpPlane.Tests;

public sealed class PostgresIntegrationTests
{
    [Fact]
    public async Task DumpAsync_StreamsRestorableObjectKindsAndCopyData()
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

            var options = new PgDumpOptions { UsePsqlRestrict = false };
            options.IncludeSchemas.Add(schema);
            await using var stream = new MemoryStream();
            await new PostgresPlainTextDumper().DumpAsync(connection, new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true), options, cancellationToken);
            var script = Encoding.UTF8.GetString(stream.ToArray());

            Assert.Contains($"CREATE TYPE {qualifiedSchema}.\"mood\" AS ENUM ('happy', 'sad');", script);
            Assert.Contains($"CREATE TABLE {qualifiedSchema}.\"parent\"", script);
            Assert.Contains($"CREATE TABLE {qualifiedSchema}.\"parent_first\" PARTITION OF", script);
            Assert.Contains($"COPY {qualifiedSchema}.\"parent_first\"", script);
            Assert.Contains("hello\\nworld", script);
            Assert.Contains("ADD CONSTRAINT \"parent_pk\" PRIMARY KEY", script);
            Assert.Contains("CREATE INDEX parent_label_idx", script);
            Assert.Contains($"CREATE VIEW {qualifiedSchema}.\"parent_view\"", script);
            Assert.Contains("CREATE TRIGGER normalize_label", script);
            Assert.Contains("pg_catalog.setval", script);
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
            Assert.Contains(
                $"INSERT INTO {qualifiedSchema}.\"parent_first\" (\"id\", \"label\", \"state\", \"amount\", \"created_at\") " +
                "OVERRIDING SYSTEM VALUE VALUES ('1', 'hello",
                insertScript);
            if (serverMajor >= 18)
            {
                Assert.Contains(
                    $"INSERT INTO {qualifiedSchema}.\"generated_feature\" (\"source\") VALUES ('7');",
                    insertScript);
            }

            await using (var dropSource = new NpgsqlCommand($"DROP SCHEMA {qualifiedSchema} CASCADE", connection))
                await dropSource.ExecuteNonQueryAsync(cancellationToken);
            await using (var restore = new NpgsqlCommand(insertScript, connection))
                await restore.ExecuteNonQueryAsync(cancellationToken);

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
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

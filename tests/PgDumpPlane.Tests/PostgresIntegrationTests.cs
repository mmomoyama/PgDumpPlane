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
                CREATE TRIGGER normalize_label BEFORE INSERT ON {qualifiedSchema}.parent
                    FOR EACH ROW EXECUTE FUNCTION {qualifiedSchema}.normalize_label();
                CREATE VIEW {qualifiedSchema}.parent_view AS SELECT id, label FROM {qualifiedSchema}.parent;
                INSERT INTO {qualifiedSchema}.parent (id, label, amount)
                    OVERRIDING SYSTEM VALUE VALUES (1, E'Hello\\nworld', 12.50);
                """, connection))
            {
                await setup.ExecuteNonQueryAsync(cancellationToken);
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
            Assert.Contains("Hello\\nworld", script);
            Assert.Contains("ADD CONSTRAINT \"parent_pk\" PRIMARY KEY", script);
            Assert.Contains("CREATE INDEX parent_label_idx", script);
            Assert.Contains($"CREATE VIEW {qualifiedSchema}.\"parent_view\"", script);
            Assert.Contains("CREATE TRIGGER normalize_label", script);
            Assert.Contains("pg_catalog.setval", script);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

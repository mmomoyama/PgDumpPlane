using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Npgsql;

namespace PgDumpPlane.Tests;

/// <summary>
/// <para>PostgresIntegrationの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of PostgresIntegration.</para>
/// </summary>
public sealed class PostgresIntegrationTests
{
    /// <summary>
    /// <para>同じDBのpg_dumpとCREATE・OWNER・COPYの並びを比較し、復元も確認します。</para>
    /// <para>Compares CREATE, OWNER, and COPY order with pg_dump on the same database and verifies restoration.</para>
    /// </summary>
    /// <returns>比較と復元の検証完了を表すタスク。 A task representing completion of comparison and restore checks.</returns>
    [Fact]
    public async Task DumpAsync_MatchesNativePgDumpDefinitionAndOwnerOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        var pgDumpPath = Environment.GetEnvironmentVariable("PGDUMPPLANE_PG_DUMP_PATH");
        // 標準ツールとの比較は明示された実行ファイルを使う場合にだけ実施します。
        // Run native comparisons only when an executable path is explicitly configured.
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(pgDumpPath))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"dump_order_{Guid.NewGuid():N}";
        var quoted = SqlText.Identifier(schema);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using (var setup = new NpgsqlCommand($"""
                CREATE SCHEMA {quoted};
                CREATE TYPE {quoted}.mood AS ENUM ('happy', 'sad');
                CREATE FUNCTION {quoted}.echo(value text) RETURNS text LANGUAGE sql AS 'SELECT value';
                CREATE FUNCTION {quoted}.echo(value integer) RETURNS integer LANGUAGE sql AS 'SELECT value';
                CREATE PROCEDURE {quoted}.refresh() LANGUAGE sql AS 'SELECT 1';
                CREATE FUNCTION {quoted}.fuc_trn_last_update_adserversetting() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                CREATE TABLE {quoted}.z_table(id serial PRIMARY KEY, value text);
                CREATE VIEW {quoted}.a_view AS SELECT id, value FROM {quoted}.z_table;
                CREATE VIEW {quoted}.a_dependent_view AS SELECT id FROM {quoted}.a_view;
                CREATE TABLE {quoted}.b_identity(id integer GENERATED ALWAYS AS IDENTITY, value integer);
                CREATE SEQUENCE {quoted}.a_free;
                CREATE SEQUENCE {quoted}.z_late_sequence;
                CREATE TABLE {quoted}.a_uses_late_sequence(id bigint DEFAULT nextval('{schema}.z_late_sequence'));
                ALTER TABLE {quoted}.z_table ADD CONSTRAINT a_unique UNIQUE (value);
                ALTER TABLE {quoted}.b_identity ADD CONSTRAINT z_unique UNIQUE (value);
                CREATE INDEX a_index ON {quoted}.z_table(value);
                CREATE INDEX z_index ON {quoted}.b_identity(value);
                CREATE FUNCTION {quoted}.trigger_noop() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';
                CREATE TRIGGER a_trigger BEFORE INSERT ON {quoted}.z_table FOR EACH ROW EXECUTE FUNCTION {quoted}.trigger_noop();
                CREATE TRIGGER z_trigger BEFORE INSERT ON {quoted}.b_identity FOR EACH ROW EXECUTE FUNCTION {quoted}.trigger_noop();
                """, connection))
                await setup.ExecuteNonQueryAsync(cancellationToken);
            await using (var data = new NpgsqlCommand($"""
                INSERT INTO {quoted}.z_table(value) VALUES ('preserved');
                INSERT INTO {quoted}.a_uses_late_sequence DEFAULT VALUES;
                INSERT INTO {quoted}.b_identity(value) VALUES (42);
                GRANT SELECT ON TABLE {quoted}.z_table TO PUBLIC;
                """, connection))
                await data.ExecuteNonQueryAsync(cancellationToken);

            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            var start = new ProcessStartInfo(pgDumpPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "--host", settings.Host!, "--port", settings.Port.ToString(),
                "--username", settings.Username!, "--dbname", settings.Database!,
                "--schema", schema, "--no-comments", "--no-password"
            })
                start.ArgumentList.Add(argument);
            start.Environment["PGPASSWORD"] = settings.Password ?? string.Empty;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var native = await stdout;
            Assert.True(process.ExitCode == 0, await stderr);

            var options = new PgDumpOptions { UsePsqlRestrict = false };
            options.IncludeSchemas.Add(schema);
            using var writer = new StringWriter();
            await new PostgresPlainTextDumper().DumpAsync(connection, writer, options, cancellationToken);
            var script = writer.ToString();

            Assert.Equal(DefinitionAndOwnerOrder(native), DefinitionAndOwnerOrder(script));
            Assert.Equal(PostDataHeadingOrder(native), PostDataHeadingOrder(script));
            // 同じDBの標準pg_dumpと、引数なし関数およびデータ見出しを直接比較します。
            // Compare no-argument routine and data headings directly with native pg_dump on the same DB.
            foreach (var heading in Regex.Matches(native,
                         @"^-- (?:Name: (?:fuc_trn_last_update_adserversetting\(\)|refresh\(\)|mood|z_table|a_view|a_free)|Data for Name: z_table);[^\r\n]+",
                         RegexOptions.Multiline).Select(match => match.Value))
                Assert.Contains(heading, script);
            Assert.Contains($"-- Name: fuc_trn_last_update_adserversetting(); Type: FUNCTION; Schema: {schema}; Owner: {settings.Username}", script);
            Assert.True(script.IndexOf("GRANT SELECT ON TABLE", StringComparison.Ordinal) >
                script.LastIndexOf("COPY ", StringComparison.Ordinal));
            options.IncludeOwnership = false;
            using var noOwnerWriter = new StringWriter();
            await new PostgresPlainTextDumper().DumpAsync(connection, noOwnerWriter, options, cancellationToken);
            Assert.DoesNotContain(" OWNER TO ", noOwnerWriter.ToString());
            await using (var drop = new NpgsqlCommand($"DROP SCHEMA {quoted} CASCADE", connection))
                await drop.ExecuteNonQueryAsync(cancellationToken);
            await new PostgresPlainTextRestorer().RestoreAsync(
                connection, new StringReader(script), cancellationToken: cancellationToken);
            await using var verify = new NpgsqlCommand($"""
                SELECT (SELECT value FROM {quoted}.z_table WHERE id = 1) = 'preserved'
                   AND (SELECT id FROM {quoted}.a_uses_late_sequence) = 1
                   AND (SELECT value FROM {quoted}.b_identity WHERE id = 1) = 42
                   AND (SELECT id FROM {quoted}.a_dependent_view) = 1
                """, connection);
            Assert.True((bool)(await verify.ExecuteScalarAsync(cancellationToken))!);
            await using var next = new NpgsqlCommand(
                $"INSERT INTO {quoted}.z_table(value) VALUES ('next') RETURNING id", connection);
            Assert.Equal(2, await next.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {quoted} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// <para>テーブル名順とオブジェクト名順が逆になる制約・索引・トリガーの出力順を抽出します。</para>
    /// <para>Extracts constraint, index, and trigger order where table and object name ordering disagree.</para>
    /// </summary>
    /// <param name="script">比較するSQL。 SQL to compare.</param>
    /// <returns>対象の見出し順。 Selected heading order.</returns>
    private static string[] PostDataHeadingOrder(string script) =>
        Regex.Matches(script, @"^-- Name: [^\r\n]+; Type: (?:CONSTRAINT|INDEX|TRIGGER);[^\r\n]+",
                RegexOptions.Multiline).Select(match => match.Value).ToArray();

    /// <summary>
    /// <para>隔離した一時DBの全体ダンプで、public・拡張機能・型の順を標準pg_dumpと比較します。</para>
    /// <para>Compares public, extension, and type ordering with native pg_dump in an isolated temporary database.</para>
    /// </summary>
    /// <returns>出力順検証を表すタスク。 A task representing ordering verification.</returns>
    [Fact]
    public async Task DumpAsync_MatchesNativePublicAndExtensionOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        var pgDumpPath = Environment.GetEnvironmentVariable("PGDUMPPLANE_PG_DUMP_PATH");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(pgDumpPath))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var database = $"dump_extensions_{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Database = database, Pooling = false };
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync(cancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {SqlText.Identifier(database)} TEMPLATE template0", admin))
            await create.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var setup = new NpgsqlCommand("""
                CREATE SCHEMA ciserver;
                ALTER SCHEMA public OWNER TO CURRENT_USER;
                CREATE EXTENSION pgcrypto WITH SCHEMA ciserver;
                CREATE TYPE ciserver.mood AS ENUM ('happy');
                """, connection))
                await setup.ExecuteNonQueryAsync(cancellationToken);
            var start = new ProcessStartInfo(pgDumpPath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            // publicの所有者を既定から変更し、全体pg_dumpでも見出しと所有者変更が出る条件にします。
            // A nondefault public owner makes native whole-database pg_dump emit its heading and ownership.
            foreach (var argument in new[] { "--host", settings.Host!, "--port", settings.Port.ToString(),
                         "--username", settings.Username!, "--dbname", database,
                         "--no-comments", "--no-password" })
                start.ArgumentList.Add(argument);
            start.Environment["PGPASSWORD"] = settings.Password ?? string.Empty;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var native = await stdout;
            Assert.True(process.ExitCode == 0, await stderr);
            using var writer = new StringWriter();
            var options = new PgDumpOptions { UsePsqlRestrict = false };
            await new PostgresPlainTextDumper().DumpAsync(connection, writer,
                options, cancellationToken);
            var script = writer.ToString();
            Assert.Equal(DefinitionAndOwnerOrder(native), DefinitionAndOwnerOrder(script));
            Assert.Contains($"-- Name: public; Type: SCHEMA; Schema: -; Owner: {settings.Username}\n--\n\n" +
                $"ALTER SCHEMA \"public\" OWNER TO {SqlText.Identifier(settings.Username!)};", script);
            Assert.Contains("-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -\n--\n\n" +
                "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\" WITH SCHEMA \"ciserver\";", script);
            Assert.True(script.IndexOf("ALTER SCHEMA \"public\"", StringComparison.Ordinal) <
                script.IndexOf("CREATE EXTENSION", StringComparison.Ordinal));
            Assert.True(script.IndexOf("CREATE EXTENSION", StringComparison.Ordinal) <
                script.IndexOf("CREATE TYPE", StringComparison.Ordinal));
        }
        finally
        {
            // 作成した一時DBだけを削除します。既存DBの内容は変更しません。
            // Remove only the temporary database created above; leave existing database contents untouched.
            await using var cleanup = new NpgsqlCommand($"DROP DATABASE {SqlText.Identifier(database)}", admin);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// <para>単純名のテスト対象について、引用符とOR REPLACEの差を除いた順序を抽出します。</para>
    /// <para>Extracts order for simple fixture names, ignoring quoting and OR REPLACE differences.</para>
    /// </summary>
    /// <param name="script">比較するダンプSQL。 Dump SQL to compare.</param>
    /// <returns>CREATE・OWNER・COPYの出現順。 CREATE, OWNER, and COPY entries in appearance order.</returns>
    private static string[] DefinitionAndOwnerOrder(string script) =>
        Regex.Matches(script,
            @"^(CREATE (?:OR REPLACE )?(?:EXTENSION IF NOT EXISTS|SCHEMA|TYPE|FUNCTION|PROCEDURE|TABLE|VIEW|SEQUENCE) [^\s(]+|ALTER (?:SCHEMA|TYPE|FUNCTION|PROCEDURE|TABLE|VIEW|SEQUENCE) [^\r\n]+ OWNER TO [^\r\n]+;|COPY [^\s(]+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Value.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("OR REPLACE ", string.Empty, StringComparison.Ordinal).TrimEnd(';'))
            .ToArray();

    /// <summary>
    /// <para>新しいバージョンの構文を実サーバーに復元し、生成列の値を確認します。</para>
    /// <para>Restores newer-version syntax to a live server and verifies the generated value.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task RestoreAsync_DowngradesNewerDumpToConnectedServer()
    {
        // 接続文字列が未指定ならDB検証は実行せず戻ります。実機検証には環境変数が必要です。
        // Return without database verification when the connection string is absent; live checks require this environment variable.
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"restore_compat_{Guid.NewGuid():N}";
        var qualifiedSchema = SqlText.Identifier(schema);
        var script = $$"""
            --
            -- PostgreSQL database dump
            --

            -- Dumped from database version 19beta4
            -- Dumped by PgDumpPlane test

            SET transaction_timeout = 0;
            CREATE SCHEMA {{qualifiedSchema}};
            CREATE UNLOGGED SEQUENCE {{qualifiedSchema}}."counter";
            CREATE TABLE {{qualifiedSchema}}."item" (
                "source" text CONSTRAINT "source_required" NOT NULL NO INHERIT,
                "source_length" integer GENERATED ALWAYS AS (length(source)),
                "nullable_value" integer,
                CONSTRAINT "item_nullable_key" UNIQUE NULLS NOT DISTINCT ("nullable_value")
            );
            ALTER TABLE ONLY {{qualifiedSchema}}."item" ALTER COLUMN "source" SET COMPRESSION pglz;
            INSERT INTO {{qualifiedSchema}}."item" ("source", "nullable_value") VALUES ('value', NULL);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await new PostgresPlainTextRestorer().RestoreAsync(
                connection,
                new StringReader(script),
                cancellationToken: cancellationToken);

            await using var verify = new NpgsqlCommand(
                $"SELECT source_length FROM {qualifiedSchema}.item", connection);
            Assert.Equal(5, await verify.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// <para>標準pg_dump形式のヘッダーを持つSQLを実サーバーへ復元できることを確認します。</para>
    /// <para>Verifies restoration of SQL with a native pg_dump header to a live server.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task RestoreAsync_AcceptsNativePgDumpPlainTextHeader()
    {
        var connectionString = Environment.GetEnvironmentVariable("PGDUMPPLANE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"restore_native_{Guid.NewGuid():N}";
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
                -- Dumped by pg_dump version {{connection.PostgreSqlVersion}}

                CREATE SCHEMA {{qualifiedSchema}};
                CREATE TABLE {{qualifiedSchema}}.item (id integer);
                INSERT INTO {{qualifiedSchema}}.item VALUES (42);
                """;

            await new PostgresPlainTextRestorer().RestoreAsync(
                connection,
                new StringReader(script),
                cancellationToken: cancellationToken);

            await using var verify = new NpgsqlCommand(
                $"SELECT id FROM {qualifiedSchema}.item", connection);
            Assert.Equal(42, await verify.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {qualifiedSchema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// <para>ダンプ以外の入力に含まれるSQLが実行前に拒否されることを確認します。</para>
    /// <para>Verifies rejection of non-dump input before its SQL executes.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
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

    /// <summary>
    /// <para>途中でSQLが失敗した場合に、それ以前の変更もロールバックされることを確認します。</para>
    /// <para>Verifies rollback of earlier changes when a later SQL statement fails.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
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

    /// <summary>
    /// <para>各種オブジェクト・データ・権限をCOPYとINSERTで往復させて検証します。</para>
    /// <para>Verifies round trips of supported objects, data, and security in COPY and INSERT formats.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
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
        Assert.InRange(serverMajor, 12, 19);
        if (serverMajor < 19)
        {
            await using var legacyStringSyntax = new NpgsqlCommand(
                "SET standard_conforming_strings = off", connection);
            await legacyStringSyntax.ExecuteNonQueryAsync(cancellationToken);
        }
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
                    slash text DEFAULT E'\\',
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
                        ALTER ROLE {qualifiedOwner} SET search_path TO ciserver, serial_num_mng, public;
                        GRANT CREATE ON SCHEMA {qualifiedSchema} TO {qualifiedOwner};
                        ALTER TABLE {qualifiedSchema}.binary_data OWNER TO {qualifiedOwner};
                        """, connection);
                    await ownerSetup.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var options = new PgDumpOptions();
            options.IncludeRoleSettings = alternateOwner is not null;
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
                Assert.Contains(
                    $"ALTER ROLE {SqlText.Identifier(alternateOwner)} SET \"search_path\" TO " +
                    "'ciserver', 'serial_num_mng', 'public';",
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
                $"INSERT INTO {qualifiedSchema}.\"parent_first\" (\"id\", \"label\", \"state\", \"amount\", \"created_at\", \"slash\")";
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
            if (alternateOwner is not null)
            {
                await using var resetRoleSetting = new NpgsqlCommand(
                    $"ALTER ROLE {SqlText.Identifier(alternateOwner)} RESET search_path", connection);
                await resetRoleSetting.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var copyRestoreStream = new MemoryStream(Encoding.UTF8.GetBytes(script));
            await new PostgresPlainTextRestorer().RestoreAsync(
                connectionString,
                copyRestoreStream,
                cancellationToken: cancellationToken);
            await AssertRestoredDataAsync(
                connection, qualifiedSchema, serverMajor, allByteValues, largeBinary, cancellationToken);
            await AssertRestoredSecurityAsync(connection, schema, alternateOwner, cancellationToken);
            if (alternateOwner is not null)
            {
                await using var verifyRoleSetting = new NpgsqlCommand(
                    "SELECT rolconfig @> ARRAY['search_path=ciserver, serial_num_mng, public'] " +
                    "FROM pg_catalog.pg_roles WHERE rolname = @role", connection);
                verifyRoleSetting.Parameters.AddWithValue("role", alternateOwner);
                Assert.True((bool)(await verifyRoleSetting.ExecuteScalarAsync(cancellationToken))!);
            }

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

    /// <summary>
    /// <para>復元された文字列、生成列、バイナリ値を検証します。</para>
    /// <para>Verifies restored strings, generated columns, and binary values.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="qualifiedSchema">引用済みのスキーマ名。 Quoted schema name.</param>
    /// <param name="serverMajor">実サーバーのメジャーバージョン。 Live server major version.</param>
    /// <param name="allByteValues">全バイト値を含む期待データ。 Expected data containing every byte value.</param>
    /// <param name="largeBinary">大きなbyteaの期待データ。 Expected large bytea payload.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task AssertRestoredDataAsync(
        NpgsqlConnection connection,
        string qualifiedSchema,
        int serverMajor,
        byte[] allByteValues,
        byte[] largeBinary,
        CancellationToken cancellationToken)
    {
        await using (var verify = new NpgsqlCommand(
            $"SELECT label, slash FROM {qualifiedSchema}.parent_first WHERE id = 1", connection))
        {
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal("hello\nworld", reader.GetString(0));
            Assert.Equal("\\", reader.GetString(1));
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

    /// <summary>
    /// <para>復元されたオブジェクト権限、列権限、所有者を検証します。</para>
    /// <para>Verifies restored object privileges, column privileges, and ownership.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="schema">スキーマ名。 Schema name.</param>
    /// <param name="expectedTableOwner">期待する所有者。nullの場合は所有者の検証を省略します。 Expected owner, or null to skip the ownership assertion.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
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

    /// <summary>
    /// <para>NULL・空配列・全バイト値・大きなbyteaを行順に検証します。</para>
    /// <para>Checks null, empty, all-byte, and large bytea values in row order.</para>
    /// </summary>
    /// <param name="connection">処理に使用するNpgsql接続。 Npgsql connection used by the operation.</param>
    /// <param name="qualifiedTable">スキーマ修飾と引用済みのテーブル名。 Schema-qualified and quoted table name.</param>
    /// <param name="allByteValues">全バイト値を含む期待データ。 Expected data containing every byte value.</param>
    /// <param name="largeBinary">大きなbyteaの期待データ。 Expected large bytea payload.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    private static async Task AssertBinaryRowsAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        byte[] allByteValues,
        byte[] largeBinary,
        CancellationToken cancellationToken)
    {
        // NULLと長さ0のbyteaは異なる値なので、区別して往復検証します。
        // NULL and zero-length bytea are different values; verify both independently.
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

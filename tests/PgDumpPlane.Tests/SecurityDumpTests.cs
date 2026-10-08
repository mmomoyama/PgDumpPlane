namespace PgDumpPlane.Tests;

/// <summary>
/// <para>SecurityDumpの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of SecurityDump.</para>
/// </summary>
public sealed class SecurityDumpTests
{
    /// <summary>
    /// <para>入力順によらず、public所有者変更がpgcrypto拡張の前に出ることを確認します。</para>
    /// <para>Checks that public ownership precedes the pgcrypto extension regardless of input order.</para>
    /// </summary>
    /// <returns>出力順検証を表すタスク。 A task representing ordering verification.</returns>
    [Fact]
    public async Task WritePreDataAsync_WritesSchemasBeforeExtensions()
    {
        var snapshot = Snapshot([new(SecuredObjectKind.Schema, "public", "public", null, "pg_database_owner")], []) with
        {
            Schemas = [new("public"), new("ciserver")],
            Extensions = [new("z_extension", "ciserver"), new("pgcrypto", "ciserver")]
        };
        using var writer = new StringWriter();
        await PostgresPlainTextDumper.WritePreDataAsync(writer, snapshot);
        var sql = writer.ToString();
        Assert.Contains("-- Name: public; Type: SCHEMA; Schema: -; Owner: pg_database_owner\n--\n\n" +
            "ALTER SCHEMA \"public\" OWNER TO \"pg_database_owner\";", sql);
        Assert.Contains("-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -\n--\n\n" +
            "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\" WITH SCHEMA \"ciserver\";", sql);
        Assert.True(sql.IndexOf("CREATE SCHEMA \"ciserver\"", StringComparison.Ordinal) <
            sql.IndexOf("ALTER SCHEMA \"public\"", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("ALTER SCHEMA \"public\"", StringComparison.Ordinal) <
            sql.IndexOf("CREATE EXTENSION IF NOT EXISTS \"pgcrypto\"", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("CREATE EXTENSION IF NOT EXISTS \"pgcrypto\"", StringComparison.Ordinal) <
            sql.IndexOf("CREATE EXTENSION IF NOT EXISTS \"z_extension\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// <para>pg_dump形式の見出し、データ用接頭辞、適用なしのハイフンを確認します。</para>
    /// <para>Checks pg_dump-style headings, data prefixes, and hyphens for absent metadata.</para>
    /// </summary>
    /// <returns>出力検証を表すタスク。 A task representing output verification.</returns>
    [Fact]
    public async Task WriteObjectHeaderAsync_MatchesPgDumpFormat()
    {
        using var writer = new StringWriter();
        await PostgresPlainTextDumper.WriteObjectHeaderAsync(writer,
            "fuc_trn_last_update_adserversetting()", "FUNCTION", "ciserver", "postgres");
        await PostgresPlainTextDumper.WriteObjectHeaderAsync(writer, "items", "TABLE DATA", "public", null, data: true);
        await PostgresPlainTextDumper.WriteObjectHeaderAsync(writer, "public", "SCHEMA", null, null);
        Assert.Equal("--\n-- Name: fuc_trn_last_update_adserversetting(); Type: FUNCTION; Schema: ciserver; Owner: postgres\n--\n\n" +
            "--\n-- Data for Name: items; Type: TABLE DATA; Schema: public; Owner: -\n--\n\n" +
            "--\n-- Name: public; Type: SCHEMA; Schema: -; Owner: -\n--\n\n", writer.ToString());
    }

    /// <summary>
    /// <para>名前・スキーマ・所有者の改行がSQLコメントを抜け出さないことを確認します。</para>
    /// <para>Checks that newlines in names, schemas, and owners cannot escape SQL comments.</para>
    /// </summary>
    /// <returns>出力検証を表すタスク。 A task representing output verification.</returns>
    [Fact]
    public async Task WriteObjectHeaderAsync_SanitizesEmbeddedLineBreaks()
    {
        using var writer = new StringWriter();
        await PostgresPlainTextDumper.WriteObjectHeaderAsync(writer,
            "items\nDROP TABLE x;", "TABLE", "sales\r\nSELECT 1;", "owner\rSELECT 2;");
        Assert.Equal("--\n-- Name: items DROP TABLE x;; Type: TABLE; Schema: sales  SELECT 1;; Owner: owner SELECT 2;\n--\n\n",
            writer.ToString());
    }

    /// <summary>
    /// <para>通常・UNLOGGED・子パーティションのCREATE TABLEで、スキーマと名前を必ず引用することを確認します。</para>
    /// <para>Checks schema qualification and identifier quoting in ordinary, UNLOGGED, and partition CREATE TABLE statements.</para>
    /// </summary>
    /// <param name="schema">入力スキーマ名。 Input schema name.</param>
    /// <param name="name">入力テーブル名。 Input table name.</param>
    /// <param name="expected">引用済み修飾名の期待値。 Expected quoted qualified name.</param>
    /// <returns>出力検証を表すタスク。 A task representing output verification.</returns>
    [Theory]
    [InlineData("public", "items", "\"public\".\"items\"")]
    [InlineData("odd schema", "Order", "\"odd schema\".\"Order\"")]
    [InlineData("odd\"schema", "table\"name", "\"odd\"\"schema\".\"table\"\"name\"")]
    public async Task WritePreDataAsync_AlwaysQualifiesAndQuotesCreateTableNames(
        string schema, string name, string expected)
    {
        var table = Table(1, name) with { Schema = schema };
        var snapshot = Snapshot([], []) with { Tables = [table] };
        using var ordinary = new StringWriter();
        await PostgresPlainTextDumper.WritePreDataAsync(ordinary, snapshot);
        Assert.Contains($"CREATE TABLE {expected} (\n", ordinary.ToString());

        using var unlogged = new StringWriter();
        await PostgresPlainTextDumper.WritePreDataAsync(unlogged,
            snapshot with { Tables = [table with { Unlogged = true }] });
        Assert.Contains($"CREATE UNLOGGED TABLE {expected} (\n", unlogged.ToString());

        using var partition = new StringWriter();
        await PostgresPlainTextDumper.WritePreDataAsync(partition, snapshot with
        {
            Tables = [table with
            {
                ParentOid = 2, ParentSchema = "parent schema", ParentName = "parent\"name",
                PartitionBound = "FOR VALUES FROM (1) TO (2)"
            }]
        });
        Assert.Contains($"CREATE TABLE {expected} PARTITION OF \"parent schema\".\"parent\"\"name\" ",
            partition.ToString());
    }

    /// <summary>
    /// <para>ACL出力は権限だけを含み、所有者変更を末尾へ再出力しないことを確認します。</para>
    /// <para>Checks that the ACL pass contains only privileges and does not repeat ownership at the end.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task WriteSecurityAsync_WritesPrivilegesWithoutRepeatingOwnership()
    {
        var snapshot = Snapshot(
            ownership:
            [
                new(SecuredObjectKind.Schema, "sales", "sales", null, "schema_owner"),
                new(SecuredObjectKind.Table, "sales", "order", null, "table_owner"),
                new(SecuredObjectKind.Function, "sales", "calculate", "integer, text", "routine_owner")
            ],
            accessControls:
            [
                new(
                    SecuredObjectKind.Table,
                    "sales",
                    "order",
                    null,
                    null,
                    "table_owner",
                    [
                        new(null, "SELECT", false),
                        new("reporting", "SELECT", true)
                    ])
            ]);
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WriteSecurityAsync(writer, snapshot);

        var sql = writer.ToString();
        Assert.Contains("REVOKE ALL PRIVILEGES ON TABLE \"sales\".\"order\" FROM CURRENT_USER;", sql);
        Assert.Contains("REVOKE ALL PRIVILEGES ON TABLE \"sales\".\"order\" FROM PUBLIC;", sql);
        Assert.Contains("REVOKE ALL PRIVILEGES ON TABLE \"sales\".\"order\" FROM \"table_owner\";", sql);
        Assert.Contains("GRANT SELECT ON TABLE \"sales\".\"order\" TO PUBLIC;", sql);
        Assert.Contains(
            "GRANT SELECT ON TABLE \"sales\".\"order\" TO \"reporting\" WITH GRANT OPTION;",
            sql);
        Assert.DoesNotContain("OWNER TO", sql);
    }

    /// <summary>
    /// <para>各定義直後の所有者変更、public、関数のオーバーロードとプロシージャを確認します。</para>
    /// <para>Checks ownership after each definition, public, routine overloads, and procedures.</para>
    /// </summary>
    [Fact]
    public async Task WritePreDataAsync_WritesOwnershipImmediatelyAfterEachDefinition()
    {
        var snapshot = Snapshot(
            [
                new(SecuredObjectKind.Schema, "public", "public", null, "public_owner"),
                new(SecuredObjectKind.Schema, "sales", "sales", null, "schema_owner"),
                new(SecuredObjectKind.Type, "sales", "mood", null, "type_owner"),
                new(SecuredObjectKind.Function, "sales", "calculate", "value integer", "integer_owner"),
                new(SecuredObjectKind.Function, "sales", "calculate", "value text", "text_owner"),
                new(SecuredObjectKind.Procedure, "sales", "refresh", "", "procedure_owner"),
                new(SecuredObjectKind.Table, "sales", "item", null, "table_owner"),
                new(SecuredObjectKind.Sequence, "sales", "counter", null, "sequence_owner"),
                new(SecuredObjectKind.View, "sales", "item_view", null, "view_owner")
            ], []) with
        {
            Schemas = [new("sales"), new("public")],
            Enums = [new("sales", "mood", ["happy"])],
            Routines =
            [
                new("sales", "calculate", "CREATE FUNCTION sales.calculate(value integer) RETURNS integer LANGUAGE sql AS 'SELECT value';",
                    SecuredObjectKind.Function, "value integer"),
                new("sales", "calculate", "CREATE FUNCTION sales.calculate(value text) RETURNS text LANGUAGE sql AS 'SELECT value';",
                    SecuredObjectKind.Function, "value text"),
                new("sales", "refresh", "CREATE PROCEDURE sales.refresh() LANGUAGE sql AS 'SELECT 1';",
                    SecuredObjectKind.Procedure, "")
            ],
            Tables = [Table(1, "item")],
            Sequences = [Sequence(2, "counter")],
            Views = [new(3, "sales", "item_view", "SELECT id FROM sales.item", null, [1])]
        };
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WritePreDataAsync(writer, snapshot);

        var sql = writer.ToString();
        Assert.DoesNotContain("CREATE SCHEMA \"public\"", sql);
        Assert.Contains("-- Name: sales; Type: SCHEMA; Schema: -; Owner: schema_owner\n--\n\nCREATE SCHEMA", sql);
        Assert.Contains("-- Name: mood; Type: TYPE; Schema: sales; Owner: type_owner\n--\n\nCREATE TYPE", sql);
        Assert.Contains("-- Name: calculate(value integer); Type: FUNCTION; Schema: sales; Owner: integer_owner\n--\n\nCREATE FUNCTION", sql);
        Assert.Contains("-- Name: refresh(); Type: PROCEDURE; Schema: sales; Owner: procedure_owner\n--\n\nCREATE PROCEDURE", sql);
        Assert.Contains("-- Name: item; Type: TABLE; Schema: sales; Owner: table_owner\n--\n\nCREATE TABLE", sql);
        Assert.Contains("-- Name: counter; Type: SEQUENCE; Schema: sales; Owner: sequence_owner\n--\n\nCREATE SEQUENCE", sql);
        Assert.Contains("-- Name: item_view; Type: VIEW; Schema: sales; Owner: view_owner\n--\n\nCREATE VIEW", sql);
        Assert.Contains("ALTER SCHEMA \"public\" OWNER TO \"public_owner\";", sql);
        Assert.Contains("CREATE SCHEMA \"sales\";\n\nALTER SCHEMA \"sales\" OWNER TO \"schema_owner\";", sql);
        Assert.Contains("AS ENUM ('happy');\n\nALTER TYPE \"sales\".\"mood\" OWNER TO \"type_owner\";", sql);
        Assert.Contains("AS 'SELECT value';\n\nALTER FUNCTION \"sales\".\"calculate\"(value integer) OWNER TO \"integer_owner\";", sql);
        Assert.Contains("AS 'SELECT value';\n\nALTER FUNCTION \"sales\".\"calculate\"(value text) OWNER TO \"text_owner\";", sql);
        Assert.Contains("AS 'SELECT 1';\n\nALTER PROCEDURE \"sales\".\"refresh\"() OWNER TO \"procedure_owner\";", sql);
        Assert.Contains("CACHE 1;\n\nALTER SEQUENCE \"sales\".\"counter\" OWNER TO \"sequence_owner\";", sql);
        Assert.Contains(");\n\nALTER TABLE \"sales\".\"item\" OWNER TO \"table_owner\";", sql);
        Assert.Contains("FROM sales.item;\n\nALTER VIEW \"sales\".\"item_view\" OWNER TO \"view_owner\";", sql);
    }

    /// <summary>
    /// <para>所有シーケンスの循環を既定値の後置で解消し、ビュー依存と名前順を保つことを確認します。</para>
    /// <para>Checks deferred owned-sequence defaults, view dependencies, and name ordering.</para>
    /// </summary>
    [Fact]
    public async Task WritePreDataAsync_OrdersOwnedSequencesViewsAndDeferredDefaults()
    {
        var column = new ColumnInfo("id", "integer", false, "nextval('sales.z_table_id_seq'::regclass)",
            '\0', '\0', null, null, null, false) { SequenceDependencies = [2] };
        var snapshot = Snapshot([], []) with
        {
            Tables = [Table(1, "z_table") with { Columns = [column] }],
            Sequences = [Sequence(2, "z_table_id_seq", "z_table"), Sequence(3, "a_free")],
            Views = [new(4, "sales", "a_view", "SELECT id FROM sales.z_table", null, [1])]
        };
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WritePreDataAsync(writer, snapshot);

        var sql = writer.ToString();
        string[] ordered =
        [
            "CREATE SEQUENCE \"sales\".\"a_free\"", "CREATE TABLE \"sales\".\"z_table\"",
            "CREATE VIEW \"sales\".\"a_view\"", "CREATE SEQUENCE \"sales\".\"z_table_id_seq\"",
            "OWNED BY \"sales\".\"z_table\".\"id\"", "ALTER COLUMN \"id\" SET DEFAULT nextval"
        ];
        var previous = -1;
        foreach (var statement in ordered)
        {
            var position = sql.IndexOf(statement, StringComparison.Ordinal);
            Assert.True(position > previous, $"Unexpected order: {statement}");
            previous = position;
        }
        Assert.DoesNotContain("\"id\" integer DEFAULT", sql);
        Assert.DoesNotContain("OWNER TO", sql);
    }

    /// <summary>
    /// <para>順序テスト用の最小テーブルを作成します。</para>
    /// <para>Creates a minimal table for ordering tests.</para>
    /// </summary>
    /// <param name="oid">テスト用OID。 Test OID.</param>
    /// <param name="name">テーブル名。 Table name.</param>
    /// <returns>最小テーブル定義。 Minimal table metadata.</returns>
    private static TableInfo Table(uint oid, string name) =>
        new(oid, "sales", name, 'r', false, null, null, null, null, null, null, null,
            [new("id", "integer", false, null, '\0', '\0', null, null, null, false)]);

    /// <summary>
    /// <para>順序テスト用の通常シーケンスを作成します。</para>
    /// <para>Creates a standalone sequence for ordering tests.</para>
    /// </summary>
    /// <param name="oid">テスト用OID。 Test OID.</param>
    /// <param name="name">シーケンス名。 Sequence name.</param>
    /// <param name="ownedTable">所有列のテーブル名。nullは所有列なし。 Owning table name, or null for no owning column.</param>
    /// <returns>シーケンス定義。 Sequence metadata.</returns>
    private static SequenceInfo Sequence(uint oid, string name, string? ownedTable = null) =>
        new(oid, "sales", name, false, "bigint", 1, 1, long.MaxValue, 1, 1, false,
            ownedTable is null ? null : "sales", ownedTable, ownedTable is null ? null : "id", false);

    /// <summary>
    /// <para>ロール名の引用とテーブル権限から列権限への出力順序を確認します。</para>
    /// <para>Checks role quoting and table-before-column privilege ordering.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task WriteSecurityAsync_WritesColumnPrivilegesAndQuotesRoles()
    {
        var snapshot = Snapshot(
            ownership: [],
            accessControls:
            [
                new(
                    SecuredObjectKind.Table,
                    "odd schema",
                    "items",
                    null,
                    "private value",
                    "owner",
                    [new("role\"name", "UPDATE", false)]),
                new(
                    SecuredObjectKind.Table,
                    "odd schema",
                    "items",
                    null,
                    null,
                    "owner",
                    [new("role\"name", "SELECT", false)])
            ]);
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WriteSecurityAsync(writer, snapshot);

        var sql = writer.ToString();
        Assert.Contains(
            "GRANT UPDATE (\"private value\") ON TABLE \"odd schema\".\"items\" TO \"role\"\"name\";",
            sql);
        Assert.True(
            sql.IndexOf("GRANT SELECT ON TABLE", StringComparison.Ordinal) <
            sql.IndexOf("GRANT UPDATE (\"private value\")", StringComparison.Ordinal));
    }

    /// <summary>
    /// <para>ロール名とsearch_path要素が別々に引用されることを確認します。</para>
    /// <para>Checks independent quoting of the role name and search_path elements.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task WriteRoleSettingsAsync_WritesQuotedRoleAndSettingValues()
    {
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WriteRoleSettingsAsync(
            writer,
            [new("role\"name", "search_path", "ciserver, serial_num_mng, public")]);

        Assert.Contains(
            "ALTER ROLE \"role\"\"name\" SET \"search_path\" TO 'ciserver', 'serial_num_mng', 'public';",
            writer.ToString());
    }

    /// <summary>
    /// <para>リスト中のカンマ・引用符を保持し、単一値は分割しないことを確認します。</para>
    /// <para>Checks preservation of commas and quotes in lists without splitting scalar settings.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task WriteRoleSettingsAsync_ParsesQuotedListValuesAndLeavesScalarValuesIntact()
    {
        using var writer = new StringWriter();

        await PostgresPlainTextDumper.WriteRoleSettingsAsync(
            writer,
            [
                new("app", "search_path", "\"odd,schema\", \"quoted\"\"name\", public"),
                new("app", "work_mem", "64MB")
            ]);

        var sql = writer.ToString();
        Assert.Contains(
            "ALTER ROLE \"app\" SET \"search_path\" TO 'odd,schema', 'quoted\"name', 'public';",
            sql);
        Assert.Contains("ALTER ROLE \"app\" SET \"work_mem\" TO '64MB';", sql);
    }

    /// <summary>
    /// <para>所有者と権限のテストに必要な最小限のカタログ情報を組み立てます。</para>
    /// <para>Builds minimal catalog metadata for ownership and privilege tests.</para>
    /// </summary>
    /// <param name="ownership">テスト用の所有者情報。 Ownership metadata used by the test.</param>
    /// <param name="accessControls">テスト用の権限情報。 Access-control metadata used by the test.</param>
    /// <returns>テスト用カタログ。 The test catalog.</returns>
    private static CatalogSnapshot Snapshot(
        IReadOnlyList<OwnershipInfo> ownership,
        IReadOnlyList<AccessControlInfo> accessControls) =>
        new(
            new DatabaseInfo("app", "18.0", 18),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            ownership,
            accessControls,
            []);
}

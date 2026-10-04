namespace PgDumpPlane.Tests;

/// <summary>
/// <para>SecurityDumpの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of SecurityDump.</para>
/// </summary>
public sealed class SecurityDumpTests
{
    /// <summary>
    /// <para>権限SQLと内部オブジェクト優先の所有者変更順序を確認します。</para>
    /// <para>Checks privilege SQL and ownership ordering with contained objects first.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task WriteSecurityAsync_WritesOwnershipInSafeOrderAndObjectPrivileges()
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
        Assert.Contains(
            "ALTER FUNCTION \"sales\".\"calculate\"(integer, text) OWNER TO \"routine_owner\";",
            sql);

        var tableOwner = sql.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        var schemaOwner = sql.IndexOf("ALTER SCHEMA", StringComparison.Ordinal);
        Assert.True(tableOwner < schemaOwner);
    }

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

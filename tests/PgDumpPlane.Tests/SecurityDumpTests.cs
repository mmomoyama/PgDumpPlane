namespace PgDumpPlane.Tests;

public sealed class SecurityDumpTests
{
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
            accessControls);
}

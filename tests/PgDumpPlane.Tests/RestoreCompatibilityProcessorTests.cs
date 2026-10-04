namespace PgDumpPlane.Tests;

/// <summary>
/// <para>RestoreCompatibilityProcessorの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of RestoreCompatibilityProcessor.</para>
/// </summary>
public sealed class RestoreCompatibilityProcessorTests
{
    /// <summary>
    /// <para>同一バージョンへの復元ではSQLが変更されないことを確認します。</para>
    /// <para>Verifies that SQL is unchanged when restoring to the same version.</para>
    /// </summary>
    [Fact]
    public void Process_DoesNothingWhenTargetIsNotOlder()
    {
        const string statement = "SET transaction_timeout = 0;\n";

        var result = new RestoreCompatibilityProcessor(18, 18).Process(statement);

        Assert.Equal(statement, result);
    }

    /// <summary>
    /// <para>復元先が対応しないタイムアウトと列圧縮設定を除外することを確認します。</para>
    /// <para>Verifies omission of timeout and compression settings unsupported by the target.</para>
    /// </summary>
    /// <param name="targetMajor">復元先DBのメジャーバージョン。 Target database major version.</param>
    /// <param name="statement">セミコロンで終わる解析済みSQL文。 Parsed SQL statement ending with a semicolon.</param>
    [Theory]
    [InlineData(16, "SET transaction_timeout = 0;\n")]
    [InlineData(13, "ALTER TABLE ONLY public.item ALTER COLUMN value SET COMPRESSION pglz;\n")]
    public void Process_OmitsUnsupportedSessionAndCompressionStatements(int targetMajor, string statement)
    {
        var result = new RestoreCompatibilityProcessor(19, targetMajor).Process(statement);

        Assert.Null(result);
    }

    /// <summary>
    /// <para>復元先が対応しないロールのtransaction_timeout設定を除外することを確認します。</para>
    /// <para>Verifies omission of an unsupported role transaction_timeout setting.</para>
    /// </summary>
    [Fact]
    public void Process_OmitsUnsupportedRoleSetting()
    {
        var result = new RestoreCompatibilityProcessor(19, 16).Process(
            "ALTER ROLE postgres SET transaction_timeout TO '1min';\n");

        Assert.Null(result);
    }

    /// <summary>
    /// <para>PostgreSQL 14向けにUNLOGGEDシーケンスとNULLの一意性構文が変換されることを確認します。</para>
    /// <para>Verifies sequence and NULL uniqueness syntax conversion for PostgreSQL 14.</para>
    /// </summary>
    [Fact]
    public void Process_DowngradesSequenceAndUniqueConstraintForPostgres14()
    {
        var processor = new RestoreCompatibilityProcessor(19, 14);

        var sequence = processor.Process("CREATE UNLOGGED SEQUENCE public.counter;\n");
        var constraint = processor.Process(
            "ALTER TABLE ONLY public.item ADD CONSTRAINT uq UNIQUE NULLS NOT DISTINCT (value);\n");

        Assert.Equal("CREATE SEQUENCE public.counter;\n", sequence);
        Assert.Equal("ALTER TABLE ONLY public.item ADD CONSTRAINT uq UNIQUE (value);\n", constraint);
    }

    /// <summary>
    /// <para>NOT NULLと生成列のPostgreSQL 18構文が縮退されることを確認します。</para>
    /// <para>Verifies downgrade of PostgreSQL 18 NOT NULL and generated-column syntax.</para>
    /// </summary>
    [Fact]
    public void Process_DowngradesPostgres18ColumnFeatures()
    {
        const string statement = """
            CREATE TABLE public.item (
                source integer CONSTRAINT source_required NOT NULL NO INHERIT,
                doubled integer GENERATED ALWAYS AS ((source * 2)) CONSTRAINT doubled_required NOT NULL
            );

            """;
        var processor = new RestoreCompatibilityProcessor(19, 17);

        var result = processor.Process(statement);

        Assert.Equal("""
            CREATE TABLE public.item (
                source integer,
                doubled integer GENERATED ALWAYS AS ((source * 2)) STORED NOT NULL
            );

            """, result);
    }

    /// <summary>
    /// <para>PostgreSQL 12向けにパーティション親のトリガーが除外されることを確認します。</para>
    /// <para>Verifies omission of partitioned-parent triggers for PostgreSQL 12.</para>
    /// </summary>
    [Fact]
    public void Process_OmitsPartitionedTableTriggerForPostgres12()
    {
        var processor = new RestoreCompatibilityProcessor(19, 12);
        _ = processor.Process("CREATE TABLE public.parent (id integer) PARTITION BY RANGE (id);\n");

        var result = processor.Process(
            "CREATE TRIGGER changed BEFORE INSERT ON public.parent FOR EACH ROW EXECUTE FUNCTION public.changed();\n");

        Assert.Null(result);
    }
}

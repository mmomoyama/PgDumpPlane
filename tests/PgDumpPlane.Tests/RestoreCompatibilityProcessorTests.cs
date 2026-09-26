namespace PgDumpPlane.Tests;

public sealed class RestoreCompatibilityProcessorTests
{
    [Fact]
    public void Process_DoesNothingWhenTargetIsNotOlder()
    {
        const string statement = "SET transaction_timeout = 0;\n";

        var result = new RestoreCompatibilityProcessor(18, 18).Process(statement);

        Assert.Equal(statement, result);
    }

    [Theory]
    [InlineData(16, "SET transaction_timeout = 0;\n")]
    [InlineData(13, "ALTER TABLE ONLY public.item ALTER COLUMN value SET COMPRESSION pglz;\n")]
    public void Process_OmitsUnsupportedSessionAndCompressionStatements(int targetMajor, string statement)
    {
        var result = new RestoreCompatibilityProcessor(19, targetMajor).Process(statement);

        Assert.Null(result);
    }

    [Fact]
    public void Process_OmitsUnsupportedRoleSetting()
    {
        var result = new RestoreCompatibilityProcessor(19, 16).Process(
            "ALTER ROLE postgres SET transaction_timeout TO '1min';\n");

        Assert.Null(result);
    }

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

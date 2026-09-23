namespace PgDumpPlane.Tests;

public sealed class SqlStatementAccumulatorTests
{
    [Fact]
    public void AppendLine_SplitsOnlyTopLevelSemicolons()
    {
        var parser = new SqlStatementAccumulator();
        var statements = new List<string>();
        var lines = new[]
        {
            "-- leading comment",
            "CREATE FUNCTION public.example() RETURNS void",
            "LANGUAGE plpgsql AS $body$",
            "BEGIN",
            "    PERFORM ';';",
            "END",
            "$body$;",
            "INSERT INTO public.items (value) VALUES (E'one\\';two');"
        };

        foreach (var line in lines)
            statements.AddRange(parser.AppendLine(line));
        parser.Complete();

        Assert.Equal(2, statements.Count);
        Assert.Contains("PERFORM ';';", statements[0]);
        Assert.Contains("E'one\\';two'", statements[1]);
    }

    [Fact]
    public void AppendLine_HandlesNestedBlockCommentsAndQuotedIdentifiers()
    {
        var parser = new SqlStatementAccumulator();
        var statements = new List<string>();

        statements.AddRange(parser.AppendLine("/* outer /* inner; */ outer; */"));
        statements.AddRange(parser.AppendLine("CREATE TABLE \"semi;colon\" (value text DEFAULT 'a;b');"));
        parser.Complete();

        var statement = Assert.Single(statements);
        Assert.Contains("\"semi;colon\"", statement);
        Assert.Contains("'a;b'", statement);
    }

    [Fact]
    public void Complete_RejectsUnterminatedStatement()
    {
        var parser = new SqlStatementAccumulator();
        parser.AppendLine("CREATE TABLE unfinished (id integer)");

        Assert.Throws<InvalidDataException>(parser.Complete);
    }
}

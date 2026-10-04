namespace PgDumpPlane.Tests;

/// <summary>
/// <para>SqlStatementAccumulatorの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of SqlStatementAccumulator.</para>
/// </summary>
public sealed class SqlStatementAccumulatorTests
{
    /// <summary>
    /// <para>関数本体や文字列内のセミコロンではSQL文を分割しないことを確認します。</para>
    /// <para>Verifies that semicolons inside routine bodies and strings do not split SQL statements.</para>
    /// </summary>
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

    /// <summary>
    /// <para>入れ子コメントと引用済み識別子内のセミコロンを正しく扱うことを確認します。</para>
    /// <para>Checks nested comments and semicolons inside quoted identifiers.</para>
    /// </summary>
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

    /// <summary>
    /// <para>文末のセミコロンがない未完了SQLを拒否することを確認します。</para>
    /// <para>Verifies rejection of unfinished SQL lacking its terminating semicolon.</para>
    /// </summary>
    [Fact]
    public void Complete_RejectsUnterminatedStatement()
    {
        var parser = new SqlStatementAccumulator();
        parser.AppendLine("CREATE TABLE unfinished (id integer)");

        Assert.Throws<InvalidDataException>(parser.Complete);
    }
}

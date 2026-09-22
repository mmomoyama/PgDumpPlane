namespace PgDumpPlane.Tests;

public sealed class SqlTextTests
{
    [Fact]
    public void Identifier_QuotesEmbeddedDoubleQuotes()
    {
        Assert.Equal("\"odd\"\"name\"", SqlText.Identifier("odd\"name"));
    }

    [Fact]
    public void Literal_QuotesEmbeddedSingleQuotes()
    {
        Assert.Equal("'it''s'", SqlText.Literal("it's"));
    }

    [Fact]
    public void Qualified_QuotesBothParts()
    {
        Assert.Equal("\"my schema\".\"Order\"", SqlText.Qualified("my schema", "Order"));
    }
}

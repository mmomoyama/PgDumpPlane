namespace PgDumpPlane.Tests;

/// <summary>
/// <para>SqlTextの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of SqlText.</para>
/// </summary>
public sealed class SqlTextTests
{
    /// <summary>
    /// <para>識別子内の二重引用符が二重化されることを確認します。</para>
    /// <para>Verifies doubling of embedded double quotes in identifiers.</para>
    /// </summary>
    [Fact]
    public void Identifier_QuotesEmbeddedDoubleQuotes()
    {
        Assert.Equal("\"odd\"\"name\"", SqlText.Identifier("odd\"name"));
    }

    /// <summary>
    /// <para>文字列内の一重引用符が二重化されることを確認します。</para>
    /// <para>Verifies doubling of embedded single quotes in literals.</para>
    /// </summary>
    [Fact]
    public void Literal_QuotesEmbeddedSingleQuotes()
    {
        Assert.Equal("'it''s'", SqlText.Literal("it's"));
    }

    /// <summary>
    /// <para>スキーマ名とオブジェクト名を個別に引用することを確認します。</para>
    /// <para>Verifies separate quoting of schema and object names.</para>
    /// </summary>
    [Fact]
    public void Qualified_QuotesBothParts()
    {
        Assert.Equal("\"my schema\".\"Order\"", SqlText.Qualified("my schema", "Order"));
    }
}

namespace PgDumpPlane.Tests;

public sealed class PgDumpOptionsTests
{
    [Fact]
    public void Includes_UsesIncludeThenExclude()
    {
        var options = new PgDumpOptions();
        options.IncludeSchemas.Add("sales");
        options.ExcludeSchemas.Add("audit");

        Assert.True(options.Includes("sales"));
        Assert.False(options.Includes("public"));
        Assert.False(options.Includes("audit"));
    }

    [Fact]
    public void Validate_RejectsEmptyDump()
    {
        var options = new PgDumpOptions { IncludeSchema = false, IncludeData = false };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}

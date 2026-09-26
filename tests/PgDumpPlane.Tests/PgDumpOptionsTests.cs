namespace PgDumpPlane.Tests;

public sealed class PgDumpOptionsTests
{
    [Fact]
    public void DataFormat_DefaultsToCopy()
    {
        var options = new PgDumpOptions();

        Assert.Equal(PgDumpDataFormat.Copy, options.DataFormat);
        Assert.True(options.IncludeOwnership);
        Assert.True(options.IncludePrivileges);
        Assert.False(options.IncludeRoleSettings);
    }

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

    [Fact]
    public void Validate_RejectsUnknownDataFormat()
    {
        var options = new PgDumpOptions { DataFormat = (PgDumpDataFormat)99 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}

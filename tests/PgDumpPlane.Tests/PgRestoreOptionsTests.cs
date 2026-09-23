namespace PgDumpPlane.Tests;

public sealed class PgRestoreOptionsTests
{
    [Fact]
    public void Defaults_AreSafeForRestore()
    {
        var options = new PgRestoreOptions();

        Assert.True(options.UseTransaction);
        Assert.Equal(0, options.CommandTimeout);
        Assert.True(options.LeaveOpen);
    }

    [Fact]
    public void Validate_RejectsNegativeCommandTimeout()
    {
        var options = new PgRestoreOptions { CommandTimeout = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}

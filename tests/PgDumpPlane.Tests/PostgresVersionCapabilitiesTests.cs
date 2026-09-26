namespace PgDumpPlane.Tests;

public sealed class PostgresVersionCapabilitiesTests
{
    [Theory]
    [InlineData(12, false, false, false, false)]
    [InlineData(13, false, false, false, false)]
    [InlineData(14, true, false, false, false)]
    [InlineData(15, true, true, false, false)]
    [InlineData(16, true, true, false, false)]
    [InlineData(17, true, true, true, false)]
    [InlineData(18, true, true, true, true)]
    [InlineData(19, true, true, true, true)]
    public void Create_MapsVersionSpecificCapabilities(
        int major,
        bool columnCompression,
        bool unloggedSequences,
        bool transactionTimeout,
        bool postgresql18Features)
    {
        var capabilities = PostgresVersionCapabilities.Create(new Version(major, 0));

        Assert.Equal(columnCompression, capabilities.SupportsColumnCompression);
        Assert.Equal(major >= 13, capabilities.SupportsPartitionTriggerClones);
        Assert.Equal(unloggedSequences, capabilities.SupportsUnloggedSequences);
        Assert.Equal(transactionTimeout, capabilities.SupportsTransactionTimeout);
        Assert.Equal(postgresql18Features, capabilities.SupportsVirtualGeneratedColumns);
        Assert.Equal(postgresql18Features, capabilities.SupportsNamedNotNullConstraints);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(20)]
    public void Create_RejectsServersOutsideSupportedRange(int major)
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => PostgresVersionCapabilities.Create(new Version(major, 0)));

        Assert.Contains("PostgreSQL 12 through 19", exception.Message);
    }
}

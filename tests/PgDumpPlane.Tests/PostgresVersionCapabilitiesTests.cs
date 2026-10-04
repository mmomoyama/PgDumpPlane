namespace PgDumpPlane.Tests;

/// <summary>
/// <para>PostgresVersionCapabilitiesの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of PostgresVersionCapabilities.</para>
/// </summary>
public sealed class PostgresVersionCapabilitiesTests
{
    /// <summary>
    /// <para>各メジャーバージョンの機能境界が期待値に一致することを確認します。</para>
    /// <para>Checks capability boundaries for each major version.</para>
    /// </summary>
    /// <param name="major">検証対象のメジャーバージョン。 Major version under test.</param>
    /// <param name="columnCompression">列圧縮対応の期待値。 Expected column compression capability.</param>
    /// <param name="unloggedSequences">UNLOGGEDシーケンス対応の期待値。 Expected UNLOGGED sequence capability.</param>
    /// <param name="transactionTimeout">transaction_timeout対応の期待値。 Expected transaction_timeout capability.</param>
    /// <param name="postgresql18Features">PostgreSQL 18の生成列・NOT NULL機能対応の期待値。 Expected PostgreSQL 18 generated-column and NOT NULL capabilities.</param>
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

    /// <summary>
    /// <para>対応範囲外のサーバーバージョンが拒否されることを確認します。</para>
    /// <para>Verifies rejection of server versions outside the supported range.</para>
    /// </summary>
    /// <param name="major">検証対象のメジャーバージョン。 Major version under test.</param>
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

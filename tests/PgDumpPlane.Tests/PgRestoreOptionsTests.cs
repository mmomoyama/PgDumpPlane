namespace PgDumpPlane.Tests;

/// <summary>
/// <para>PgRestoreOptionsの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of PgRestoreOptions.</para>
/// </summary>
public sealed class PgRestoreOptionsTests
{
    /// <summary>
    /// <para>単一トランザクション、入力を開いたままにする設定、タイムアウトの既定値を確認します。</para>
    /// <para>Checks default transaction, input ownership, and timeout settings.</para>
    /// </summary>
    [Fact]
    public void Defaults_AreSafeForRestore()
    {
        var options = new PgRestoreOptions();

        Assert.True(options.UseTransaction);
        Assert.Equal(0, options.CommandTimeout);
        Assert.True(options.LeaveOpen);
    }

    /// <summary>
    /// <para>負のSQLタイムアウトが拒否されることを確認します。</para>
    /// <para>Verifies rejection of a negative SQL command timeout.</para>
    /// </summary>
    [Fact]
    public void Validate_RejectsNegativeCommandTimeout()
    {
        var options = new PgRestoreOptions { CommandTimeout = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}

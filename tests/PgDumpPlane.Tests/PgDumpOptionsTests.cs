namespace PgDumpPlane.Tests;

/// <summary>
/// <para>PgDumpOptionsの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of PgDumpOptions.</para>
/// </summary>
public sealed class PgDumpOptionsTests
{
    /// <summary>
    /// <para>COPY形式と各ダンプオプションの既定値を確認します。</para>
    /// <para>Checks COPY output and the default values of dump options.</para>
    /// </summary>
    [Fact]
    public void DataFormat_DefaultsToCopy()
    {
        var options = new PgDumpOptions();

        Assert.Equal(PgDumpDataFormat.Copy, options.DataFormat);
        Assert.True(options.IncludeOwnership);
        Assert.True(options.IncludePrivileges);
        Assert.False(options.IncludeRoleSettings);
    }

    /// <summary>
    /// <para>対象スキーマの判定後に除外条件が優先されることを確認します。</para>
    /// <para>Verifies that exclusions take precedence after the include filter.</para>
    /// </summary>
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

    /// <summary>
    /// <para>定義とデータをともに無効にしたダンプ設定が拒否されることを確認します。</para>
    /// <para>Verifies rejection when both schema and data output are disabled.</para>
    /// </summary>
    [Fact]
    public void Validate_RejectsEmptyDump()
    {
        var options = new PgDumpOptions { IncludeSchema = false, IncludeData = false };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// <para>未定義のデータ形式が拒否されることを確認します。</para>
    /// <para>Verifies rejection of an undefined data format.</para>
    /// </summary>
    [Fact]
    public void Validate_RejectsUnknownDataFormat()
    {
        var options = new PgDumpOptions { DataFormat = (PgDumpDataFormat)99 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}

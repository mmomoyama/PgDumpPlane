namespace PgDumpPlane;

/// <summary>
/// <para>PgDumpPlaneのプレーンテキストダンプの復元方法を制御します。</para>
/// <para>Controls how a PgDumpPlane plain-text dump is restored.</para>
/// </summary>
public sealed class PgRestoreOptions
{
    /// <summary>
    /// <para>単一のトランザクションでダンプを復元します。既定値は<see langword="true"/>です。</para>
    /// <para>Restore the dump in one transaction. Defaults to <see langword="true"/>.</para>
    /// </summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>
    /// <para>SQL文のタイムアウトを秒で指定します。既定の0はタイムアウトなしです。</para>
    /// <para>Command timeout in seconds for SQL statements. Zero means no timeout and is the default.</para>
    /// </summary>
    public int CommandTimeout { get; set; }

    /// <summary>
    /// <para>復元後も呼び出し元のストリームまたはリーダーを開いたままにします。</para>
    /// <para>Leave the supplied stream or reader open after restoring.</para>
    /// </summary>
    public bool LeaveOpen { get; set; } = true;

    /// <summary>
    /// <para>設定値を検証し、不正な値がある場合は例外を送出します。</para>
    /// <para>Validates option values and throws for invalid configuration.</para>
    /// </summary>
    internal void Validate()
    {
        if (CommandTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), CommandTimeout, "Command timeout cannot be negative.");
    }
}

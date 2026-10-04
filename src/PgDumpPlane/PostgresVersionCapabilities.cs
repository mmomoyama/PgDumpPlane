namespace PgDumpPlane;

/// <summary>
/// <para>接続先のメジャーバージョンに応じたカタログ・SQL機能を表します。</para>
/// <para>Describes catalog and SQL capabilities of the connected major version.</para>
/// </summary>
internal sealed record PostgresVersionCapabilities(int Major)
{
    /// <summary>
    /// <para>対応する最古のメジャーバージョン。</para>
    /// <para>Oldest supported major version.</para>
    /// </summary>
    internal const int MinimumSupportedMajor = 12;
    /// <summary>
    /// <para>対応する最新のメジャーバージョン。</para>
    /// <para>Newest supported major version.</para>
    /// </summary>
    internal const int MaximumSupportedMajor = 19;

    /// <summary>
    /// <para>列圧縮カタログに対応するPostgreSQL 14以降か。</para>
    /// <para>Whether this is PostgreSQL 14 or later with column compression catalogs.</para>
    /// </summary>
    internal bool SupportsColumnCompression => Major >= 14;
    /// <summary>
    /// <para>子トリガーのtgparentidに対応するPostgreSQL 13以降か。</para>
    /// <para>Whether this is PostgreSQL 13 or later with child-trigger tgparentid.</para>
    /// </summary>
    internal bool SupportsPartitionTriggerClones => Major >= 13;
    /// <summary>
    /// <para>UNLOGGEDシーケンスに対応するPostgreSQL 15以降か。</para>
    /// <para>Whether this is PostgreSQL 15 or later with UNLOGGED sequences.</para>
    /// </summary>
    internal bool SupportsUnloggedSequences => Major >= 15;
    /// <summary>
    /// <para>transaction_timeoutに対応するPostgreSQL 17以降か。</para>
    /// <para>Whether this is PostgreSQL 17 or later with transaction_timeout.</para>
    /// </summary>
    internal bool SupportsTransactionTimeout => Major >= 17;
    /// <summary>
    /// <para>仮想生成列に対応するPostgreSQL 18以降か。</para>
    /// <para>Whether this is PostgreSQL 18 or later with virtual generated columns.</para>
    /// </summary>
    internal bool SupportsVirtualGeneratedColumns => Major >= 18;
    /// <summary>
    /// <para>名前付きNOT NULLカタログに対応するPostgreSQL 18以降か。</para>
    /// <para>Whether this is PostgreSQL 18 or later with named NOT NULL catalogs.</para>
    /// </summary>
    internal bool SupportsNamedNotNullConstraints => Major >= 18;

    /// <summary>
    /// <para>サーバーのメジャーバージョンを検証して利用可能な機能を返します。</para>
    /// <para>Validates the server major version and returns its capability descriptor.</para>
    /// </summary>
    /// <param name="serverVersion">接続先サーバーのバージョン。 Connected server version.</param>
    /// <returns>検証済みメジャーバージョンの機能情報。 Capabilities for the validated major version.</returns>
    internal static PostgresVersionCapabilities Create(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        if (serverVersion.Major is < MinimumSupportedMajor or > MaximumSupportedMajor)
        {
            throw new NotSupportedException(
                $"PgDumpPlane supports PostgreSQL {MinimumSupportedMajor} through {MaximumSupportedMajor}; " +
                $"the connected server is PostgreSQL {serverVersion.Major}.");
        }

        return new(serverVersion.Major);
    }
}

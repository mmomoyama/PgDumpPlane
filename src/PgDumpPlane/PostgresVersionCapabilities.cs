namespace PgDumpPlane;

internal sealed record PostgresVersionCapabilities(int Major)
{
    internal const int MinimumSupportedMajor = 12;
    internal const int MaximumSupportedMajor = 18;

    internal bool SupportsColumnCompression => Major >= 14;
    internal bool SupportsUnloggedSequences => Major >= 15;
    internal bool SupportsTransactionTimeout => Major >= 17;
    internal bool SupportsVirtualGeneratedColumns => Major >= 18;
    internal bool SupportsNamedNotNullConstraints => Major >= 18;

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

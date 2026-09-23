using System.Reflection;

namespace PgDumpPlane;

internal sealed record PgDumpHeader(string SourceDatabaseVersion, string PgDumpPlaneVersion);

internal static class PgDumpFormat
{
    internal const string HeaderTitle = "-- PostgreSQL database dump";
    internal const string SourceVersionPrefix = "-- Dumped from database version ";
    internal const string ProducerVersionPrefix = "-- Dumped by PgDumpPlane ";

    internal static string ProducerVersion
    {
        get
        {
            var informationalVersion = typeof(PgDumpFormat).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (string.IsNullOrWhiteSpace(informationalVersion))
                return typeof(PgDumpFormat).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            var metadata = informationalVersion.IndexOf('+');
            return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
        }
    }

    internal static async Task<PgDumpHeader> ReadAndValidateHeaderAsync(
        TextReader source,
        CancellationToken cancellationToken)
    {
        if (await source.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "--" ||
            await source.ReadLineAsync(cancellationToken).ConfigureAwait(false) != HeaderTitle ||
            await source.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "--")
        {
            throw Invalid("the PostgreSQL dump header is missing");
        }

        string? sourceVersion = null;
        for (var lineNumber = 0; lineNumber < 16; lineNumber++)
        {
            var line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw Invalid("the PgDumpPlane producer header is missing");
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("\\restrict ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith(SourceVersionPrefix, StringComparison.Ordinal))
            {
                sourceVersion = line[SourceVersionPrefix.Length..].Trim();
                if (sourceVersion.Length == 0)
                    throw Invalid("the source database version is empty");
                continue;
            }
            if (line.StartsWith(ProducerVersionPrefix, StringComparison.Ordinal))
            {
                var producerVersion = line[ProducerVersionPrefix.Length..].Trim();
                if (producerVersion.Length == 0)
                    throw Invalid("the PgDumpPlane version is empty");
                if (sourceVersion is null)
                    throw Invalid("the source database version header is missing");
                return new(sourceVersion, producerVersion);
            }

            throw Invalid($"unexpected content before the producer header: {line}");
        }

        throw Invalid("the PgDumpPlane producer header is missing");
    }

    private static InvalidDataException Invalid(string reason) =>
        new($"The input is not a PgDumpPlane plain-text dump: {reason}.");
}

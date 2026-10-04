using System.Reflection;

namespace PgDumpPlane;

/// <summary>
/// <para>ダンプヘッダーに記載された作成ツールを識別します。</para>
/// <para>Identifies the producer named in a dump header.</para>
/// </summary>
internal enum PgDumpProducer
{
    /// <summary>本ライブラリによるダンプ。 Dump produced by this library.</summary>
    PgDumpPlane,
    /// <summary>PostgreSQL標準pg_dumpによるダンプ。 Dump produced by native PostgreSQL pg_dump.</summary>
    PgDump
}

/// <summary>
/// <para>作成元DBとダンプ作成ツールのバージョン情報です。</para>
/// <para>Contains source database and dump producer version information.</para>
/// </summary>
internal sealed record PgDumpHeader(
    string SourceDatabaseVersion,
    PgDumpProducer Producer,
    string ProducerVersion)
{
    /// <summary>
    /// <para>ベータ版を含む作成元DBの表記からメジャーバージョンを取得します。</para>
    /// <para>Gets the source database major version, including beta version notation.</para>
    /// </summary>
    internal int SourceMajorVersion
    {
        get
        {
            // 19beta4などをVersion.Parseへ渡さず、先頭の数字だけをメジャーバージョンとして取得します。
            // Extract leading digits for versions such as 19beta4 rather than passing them to Version.Parse.
            var digits = SourceDatabaseVersion.AsSpan().TrimStart();
            var length = 0;
            while (length < digits.Length && char.IsDigit(digits[length]))
                length++;
            if (length == 0 || !int.TryParse(digits[..length], out var major))
                throw new InvalidDataException(
                    $"The input is not a supported PostgreSQL plain-text dump: " +
                    $"the source database version '{SourceDatabaseVersion}' is invalid.");
            return major;
        }
    }
}

/// <summary>
/// <para>対応ダンプのヘッダー文字列と検証処理をまとめます。</para>
/// <para>Defines supported dump header markers and their validation.</para>
/// </summary>
internal static class PgDumpFormat
{
    /// <summary>
    /// <para>対応ダンプの先頭に必要なタイトル行です。</para>
    /// <para>Required title line at the start of a supported dump.</para>
    /// </summary>
    internal const string HeaderTitle = "-- PostgreSQL database dump";
    /// <summary>
    /// <para>作成元DBのバージョンを示すヘッダーの接頭辞です。</para>
    /// <para>Header prefix identifying the source database version.</para>
    /// </summary>
    internal const string SourceVersionPrefix = "-- Dumped from database version ";
    /// <summary>
    /// <para>PgDumpPlaneの作成バージョンを示す接頭辞です。</para>
    /// <para>Header prefix identifying the PgDumpPlane producer version.</para>
    /// </summary>
    internal const string ProducerVersionPrefix = "-- Dumped by PgDumpPlane ";
    /// <summary>
    /// <para>標準pg_dumpの作成バージョンを示す接頭辞です。</para>
    /// <para>Header prefix identifying the native pg_dump producer version.</para>
    /// </summary>
    internal const string NativeProducerVersionPrefix = "-- Dumped by pg_dump version ";

    /// <summary>
    /// <para>アセンブリからビルドメタデータを除いた製品バージョンを取得します。</para>
    /// <para>Gets the assembly's product version without build metadata.</para>
    /// </summary>
    internal static string ProducerVersion
    {
        get
        {
            var informationalVersion = typeof(PgDumpFormat).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (string.IsNullOrWhiteSpace(informationalVersion))
                return typeof(PgDumpFormat).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            // ビルドのコミットハッシュ等の+メタデータを除き、ヘッダーに読みやすい製品バージョンを出します。
            // Strip build metadata such as commit hashes after '+' for a readable producer version.
            var metadata = informationalVersion.IndexOf('+');
            return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
        }
    }

    /// <summary>
    /// <para>ヘッダーを順に消費し、対応する作成ツールとバージョンを検証します。</para>
    /// <para>Consumes and validates the header identifying a supported producer and versions.</para>
    /// </summary>
    /// <param name="source">復元元または読み取り元。 Restore input or text source.</param>
    /// <param name="cancellationToken">処理の中止を通知するトークン。 Token used to request cancellation.</param>
    /// <returns>SQL本体の手前まで読み取ったヘッダー情報を返すタスク。 A task returning header metadata with the reader positioned before the SQL body.</returns>
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
        // 無制限に入力を探し続けず、ヘッダー領域を最大16行までに限定します。
        // Limit the header scan to 16 lines rather than searching the input without a bound.
        for (var lineNumber = 0; lineNumber < 16; lineNumber++)
        {
            var line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw Invalid("the producer header is missing");
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
                return new(sourceVersion, PgDumpProducer.PgDumpPlane, producerVersion);
            }
            if (line.StartsWith(NativeProducerVersionPrefix, StringComparison.Ordinal))
            {
                var producerVersion = line[NativeProducerVersionPrefix.Length..].Trim();
                if (producerVersion.Length == 0)
                    throw Invalid("the pg_dump version is empty");
                if (sourceVersion is null)
                    throw Invalid("the source database version header is missing");
                return new(sourceVersion, PgDumpProducer.PgDump, producerVersion);
            }

            throw Invalid($"unexpected content before the producer header: {line}");
        }

        throw Invalid("the producer header is missing");
    }

    /// <summary>
    /// <para>ダンプヘッダーの検証理由を含む統一された例外を生成します。</para>
    /// <para>Creates a consistent dump-header validation exception containing the failure reason.</para>
    /// </summary>
    /// <param name="reason">ヘッダー検証に失敗した理由。 Reason header validation failed.</param>
    /// <returns>理由を含むInvalidDataException。 An InvalidDataException containing the reason.</returns>
    private static InvalidDataException Invalid(string reason) =>
        new($"The input is not a supported PostgreSQL plain-text dump: {reason}.");
}

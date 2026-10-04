namespace PgDumpPlane.Tests;

/// <summary>
/// <para>PgDumpFormatの動作と境界条件を検証します。</para>
/// <para>Verifies behavior and boundary conditions of PgDumpFormat.</para>
/// </summary>
public sealed class PgDumpFormatTests
{
    /// <summary>
    /// <para>一時ファイルを使い、不正な入力と両対応ツールのヘッダーの検証結果を確認します。</para>
    /// <para>Checks invalid input and both supported producer headers using a temporary file.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task IsValidDumpFileAsync_ReturnsHeaderValidationResult()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not a dump", TestContext.Current.CancellationToken);
            var restorer = new PostgresPlainTextRestorer();
            Assert.False(await restorer.IsValidDumpFileAsync(path, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(path, """
                --
                -- PostgreSQL database dump
                --

                -- Dumped from database version 18.1
                -- Dumped by PgDumpPlane 0.2.0
                """, TestContext.Current.CancellationToken);
            Assert.True(await restorer.IsValidDumpFileAsync(path, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(path, """
                --
                -- PostgreSQL database dump
                --

                -- Dumped from database version 18.1
                -- Dumped by pg_dump version 18.1
                """, TestContext.Current.CancellationToken);
            Assert.True(await restorer.IsValidDumpFileAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <para>restrictガード付きのPgDumpPlaneヘッダーを読み取れることを確認します。</para>
    /// <para>Verifies acceptance of a PgDumpPlane header with a restrict guard.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task ReadAndValidateHeaderAsync_AcceptsPgDumpPlaneHeaderWithRestrictGuard()
    {
        using var reader = new StringReader("""
            --
            -- PostgreSQL database dump
            --

            \restrict ABC123

            -- Dumped from database version 18.1
            -- Dumped by PgDumpPlane 0.2.0

            CREATE TABLE public.item (id integer);
            """);

        var header = await PgDumpFormat.ReadAndValidateHeaderAsync(reader, TestContext.Current.CancellationToken);

        Assert.Equal("18.1", header.SourceDatabaseVersion);
        Assert.Equal(18, header.SourceMajorVersion);
        Assert.Equal(PgDumpProducer.PgDumpPlane, header.Producer);
        Assert.Equal("0.2.0", header.ProducerVersion);
        Assert.Equal(string.Empty, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <para>ベータ版表記から先頭のメジャーバージョンを取得できることを確認します。</para>
    /// <para>Verifies extraction of the major version from beta version text.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task ReadAndValidateHeaderAsync_ParsesBetaSourceMajorVersion()
    {
        using var reader = new StringReader("""
            --
            -- PostgreSQL database dump
            --

            -- Dumped from database version 19beta4
            -- Dumped by PgDumpPlane 0.2.0

            """);

        var header = await PgDumpFormat.ReadAndValidateHeaderAsync(reader, TestContext.Current.CancellationToken);

        Assert.Equal(19, header.SourceMajorVersion);
    }

    /// <summary>
    /// <para>PostgreSQL標準pg_dumpのヘッダーを読み取れることを確認します。</para>
    /// <para>Verifies acceptance of native PostgreSQL pg_dump headers.</para>
    /// </summary>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Fact]
    public async Task ReadAndValidateHeaderAsync_AcceptsNativePgDumpHeader()
    {
        using var reader = new StringReader("""
            --
            -- PostgreSQL database dump
            --

            \restrict ABC123

            -- Dumped from database version 18.0
            -- Dumped by pg_dump version 18.0

            CREATE TABLE public.item (id integer);
            """);

        var header = await PgDumpFormat.ReadAndValidateHeaderAsync(reader, TestContext.Current.CancellationToken);

        Assert.Equal("18.0", header.SourceDatabaseVersion);
        Assert.Equal(PgDumpProducer.PgDump, header.Producer);
        Assert.Equal("18.0", header.ProducerVersion);
        Assert.Equal(string.Empty, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <para>欠落・不正なヘッダーを持つ入力が拒否されることを確認します。</para>
    /// <para>Verifies rejection of inputs with missing or invalid headers.</para>
    /// </summary>
    /// <param name="contents">検証対象のダンプ内容。 Dump content under test.</param>
    /// <returns>処理完了を表すタスク。 A task representing completion of the operation.</returns>
    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("--\n-- PostgreSQL database dump\n--\n\n-- Dumped from database version 18.1\n-- Dumped by another_tool 18.1\n")]
    [InlineData("--\n-- PostgreSQL database dump\n--\n\n-- Dumped by PgDumpPlane 0.2.0\n")]
    public async Task ReadAndValidateHeaderAsync_RejectsOtherFiles(string contents)
    {
        using var reader = new StringReader(contents);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PgDumpFormat.ReadAndValidateHeaderAsync(reader, TestContext.Current.CancellationToken));
    }
}

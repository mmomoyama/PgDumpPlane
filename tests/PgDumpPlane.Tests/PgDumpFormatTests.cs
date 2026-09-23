namespace PgDumpPlane.Tests;

public sealed class PgDumpFormatTests
{
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
        }
        finally
        {
            File.Delete(path);
        }
    }

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
        Assert.Equal("0.2.0", header.PgDumpPlaneVersion);
        Assert.Equal(string.Empty, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("--\n-- PostgreSQL database dump\n--\n\n-- Dumped by pg_dump version 18.1\n")]
    [InlineData("--\n-- PostgreSQL database dump\n--\n\n-- Dumped by PgDumpPlane 0.2.0\n")]
    public async Task ReadAndValidateHeaderAsync_RejectsOtherFiles(string contents)
    {
        using var reader = new StringReader(contents);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PgDumpFormat.ReadAndValidateHeaderAsync(reader, TestContext.Current.CancellationToken));
    }
}

using LoreFetch.Core.Export;
using Xunit;

namespace LoreFetch.Tests.Collection;

public class NativeCsvExporterTests
{
    [Fact]
    public void Format_MatchesTheContract()
    {
        var exporter = new NativeCsvExporter();

        Assert.Equal("native", exporter.Format.Id);
        Assert.Equal("csv", exporter.Format.FileExtension);
        Assert.True(exporter.Format.IsVerified);
        Assert.False(string.IsNullOrWhiteSpace(exporter.Format.DisplayName));
    }

    [Fact]
    public async Task ExportAsync_LeavesTheCallersStreamOpenAndUsable()
    {
        var exporter = new NativeCsvExporter();
        using var stream = new MemoryStream();

        await exporter.ExportAsync([TestSupport.Row()], stream, CancellationToken.None);

        // The exact bug CONTRACTS.md's mechanic 1 calls out: StreamWriter
        // disposes its underlying stream unless leaveOpen: true is used.
        // If that were missing here, both of these would already have
        // failed inside ExportAsync itself.
        Assert.True(stream.CanWrite);
        Assert.True(stream.CanSeek);
    }

    [Fact]
    public async Task ExportAsync_OutputStartsWithUtf8Bom_AndRoundTripsThroughTheCodec()
    {
        var exporter = new NativeCsvExporter();
        var row = TestSupport.Row(oracleName: "Forest");
        using var stream = new MemoryStream();

        await exporter.ExportAsync([row], stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        stream.Position = 0;
        var rows = await LoreFetch.Core.Collection.NativeCsvCodec.ReadAsync(stream, logger: null, CancellationToken.None);
        Assert.Equal(row, Assert.Single(rows));
    }
}

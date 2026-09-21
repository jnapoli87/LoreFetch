using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubCollectionExporterTests
{
    [Fact]
    public void Format_ReportsTheConfiguredFormat()
    {
        var format = new ExportFormat("moxfield", "Moxfield", ".csv", IsVerified: true, Notes: null);
        var exporter = new StubCollectionExporter(format);

        Assert.Equal(format, exporter.Format);
    }

    [Fact]
    public async Task ExportAsync_UnverifiedFormat_IsReportedAsUnverified()
    {
        var format = new ExportFormat("deckbox", "Deckbox", ".csv", IsVerified: false, Notes: "unverified");
        var exporter = new StubCollectionExporter(format);

        Assert.False(exporter.Format.IsVerified);

        using var stream = new MemoryStream();
        await exporter.ExportAsync(Array.Empty<CollectionRow>(), stream, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExportAsync_DoesNotCloseTheDestinationStream()
    {
        var format = new ExportFormat("native", "Native", ".csv", IsVerified: true, Notes: null);
        var exporter = new StubCollectionExporter(format);
        var rows = new[]
        {
            new CollectionRow("oracle-forest", "Forest", 9, null, DateTimeOffset.UtcNow, 42, RowSource.Hash, "art-forest-1"),
        };

        using var stream = new MemoryStream();
        await exporter.ExportAsync(rows, stream, TestContext.Current.CancellationToken);

        Assert.True(stream.CanWrite); // still open/usable — the caller owns its lifetime
        stream.WriteByte(0); // would throw ObjectDisposedException if the exporter had closed it

        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Forest", content);
    }
}

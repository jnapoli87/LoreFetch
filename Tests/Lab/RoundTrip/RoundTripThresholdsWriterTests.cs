using System.Text.Json;
using LoreFetch.Lab.RoundTrip;
using Xunit;

namespace LoreFetch.Tests.Lab.RoundTrip;

/// Package B6-thresholds: `round-trip-gate --out` must not clobber
/// `goodDistance`/`okDistance` (B6's own calibrated fields, written
/// separately by `ThresholdsCalibration.Write`) when it re-runs and
/// rewrites the SAME `thresholds.json`. `RoundTripThresholdsDocument`
/// deliberately has no `GoodDistance`/`OkDistance` properties (see its own
/// doc comment), so a blind `JsonSerializer.Serialize` overwrite would drop
/// them silently -- the exact failure mode this test guards against.
public class RoundTripThresholdsWriterTests
{
    private static RoundTripThresholdsDocument MakeDocument(string indexSha256 = "deadbeef") => new()
    {
        FormatVersion = 1,
        ReferenceFloor = 61,
        IndexArtworkCount = 47418,
        IndexSha256 = indexSha256,
        MeasuredAt = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero),
        Notes = "fresh B2 measurement",
        Provisional = false,
        MeasuredOn = "x64-windows",
        MeasuredOnDetail = "x64-windows (win-x64, Windows)",
        SampleSize = 200,
        Seed = 0,
        LandSampleSize = 20,
        NonLandSampleSize = 180,
        Rank1ArtworkMatchRate = 1.0,
        Rank1ArtworkMatchRateLands = 1.0,
        Rank1ArtworkMatchRateNonLands = 1.0,
        OwnDistanceMin = 8,
        OwnDistanceMean = 21.7,
        OwnDistanceMedian = 19,
        OwnDistanceMax = 61,
        MarginMin = 130,
        MarginMean = 212.5,
        MarginMedian = 208,
        MarginMax = 350,
    };

    [Fact]
    public void Write_TargetAlreadyHasGoodOkDistance_PreservesThemAcrossARewrite()
    {
        var tempDir = Directory.CreateTempSubdirectory("lorefetch-thresholds-writer-test-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "thresholds.json");

            // Simulate B6 having already calibrated goodDistance/okDistance
            // into this file (ThresholdsCalibration.Write's own shape),
            // on top of an earlier B2 measurement.
            File.WriteAllText(path, """
                {
                  "formatVersion": 1,
                  "referenceFloor": 55,
                  "indexArtworkCount": 47418,
                  "indexSha256": "old-sha",
                  "measuredAt": "2026-09-22T00:00:00+00:00",
                  "notes": "old B2 notes",
                  "goodDistance": 208,
                  "okDistance": 240,
                  "goodOkMeasuredOnDetail": "x64-windows",
                  "goodOkNotes": "calibrated from the real corpus"
                }
                """);

            RoundTripThresholdsWriter.Write(path, MakeDocument(), seed: 20260922);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // B6's calibrated fields survive the B2 rewrite untouched.
            Assert.Equal(208, root.GetProperty("goodDistance").GetInt32());
            Assert.Equal(240, root.GetProperty("okDistance").GetInt32());
            Assert.Equal("x64-windows", root.GetProperty("goodOkMeasuredOnDetail").GetString());
            Assert.Equal("calibrated from the real corpus", root.GetProperty("goodOkNotes").GetString());

            // B2's own fields come from the FRESH document, not the stale
            // file -- a rewrite must never leave last run's numbers behind
            // under the guise of "preservation".
            Assert.Equal(61, root.GetProperty("referenceFloor").GetInt32());
            Assert.Equal("deadbeef", root.GetProperty("indexSha256").GetString());
            Assert.Equal("fresh B2 measurement", root.GetProperty("notes").GetString());
            Assert.Equal(20260922, root.GetProperty("seed").GetInt32());
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Write_NoExistingFile_WritesTheFreshDocumentWithNoGoodOkFields()
    {
        var tempDir = Directory.CreateTempSubdirectory("lorefetch-thresholds-writer-test-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "thresholds.json");

            RoundTripThresholdsWriter.Write(path, MakeDocument(), seed: 20260922);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            Assert.False(root.TryGetProperty("goodDistance", out _));
            Assert.False(root.TryGetProperty("okDistance", out _));
            Assert.Equal(61, root.GetProperty("referenceFloor").GetInt32());
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Write_ExistingFileIsNotValidJson_TreatsItAsNothingToPreserveRatherThanThrowing()
    {
        var tempDir = Directory.CreateTempSubdirectory("lorefetch-thresholds-writer-test-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "thresholds.json");
            File.WriteAllText(path, "{ not valid json");

            RoundTripThresholdsWriter.Write(path, MakeDocument(), seed: 20260922);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(61, doc.RootElement.GetProperty("referenceFloor").GetInt32());
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}

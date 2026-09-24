using LoreFetch.Lab.RoundTrip;
using Xunit;

namespace LoreFetch.Tests.Lab.RoundTrip;

/// Package B5c-cleanup: `RoundTripThresholdsDocument.FromStatistics` used
/// to hardcode `Provisional = true` and notes that always said "measured
/// on the Mac", regardless of which architecture actually produced the
/// measurement. A future `lab round-trip-gate --out` run on win-x64 (the
/// ship architecture) would therefore re-mark the committed, FINAL
/// thresholds as provisional, with notes that are simply false on this
/// machine. `Provisional` must instead be derived from the architecture
/// that took the measurement: win-x64 (the ship architecture) -> false,
/// anything else -> true, with a factual reason either way.
///
/// Uses the `internal` overload (`LoreFetch.Lab`'s own `InternalsVisibleTo`
/// grant to this project, `src/LoreFetch.Lab/RepoPaths.cs`) that takes the
/// architecture token/detail explicitly, so both branches are exercised
/// deterministically regardless of which machine actually runs this test
/// suite -- reading `ArchitectureProvenance.CurrentToken()` directly here
/// would make the "ship architecture" branch untestable on any machine
/// other than a real win-x64 box, and untestable at all in CI's
/// `macos-latest` leg.
public class RoundTripThresholdsDocumentTests
{
    private static RoundTripGateStatistics MakeStats() => new(
        SampleSize: 200,
        AvailableCount: 200,
        MissingCount: 0,
        CorrectCount: 200,
        Rank1Rate: 1.0,
        LandSampleSize: 20,
        LandCorrectCount: 20,
        LandRank1Rate: 1.0,
        NonLandSampleSize: 180,
        NonLandCorrectCount: 180,
        NonLandRank1Rate: 1.0,
        OwnDistanceMin: 8,
        OwnDistanceMean: 21.7,
        OwnDistanceMedian: 19,
        OwnDistanceMax: 61,
        MarginMin: 130,
        MarginMean: 212.5,
        MarginMedian: 208,
        MarginMax: 350,
        Failures: [],
        MissingArtworkIds: []);

    private static readonly DateTimeOffset MeasuredAt = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromStatistics_MeasuredOnShipArchitecture_IsNotProvisional()
    {
        var document = RoundTripThresholdsDocument.FromStatistics(
            MakeStats(), "deadbeef", 47418, MeasuredAt,
            "x64-windows", "x64-windows (win-x64, Microsoft Windows 10.0.26200)");

        Assert.False(document.Provisional);
        Assert.Equal("x64-windows", document.MeasuredOn);
        Assert.DoesNotContain("PROVISIONAL", document.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("measured on the Mac", document.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RE-MEASURED", document.Notes, StringComparison.Ordinal);
        Assert.Contains("win-x64 ship architecture", document.Notes, StringComparison.Ordinal);
        Assert.Contains("x64-windows (win-x64, Microsoft Windows 10.0.26200)", document.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void FromStatistics_MeasuredOnAForeignArchitecture_IsProvisional()
    {
        var document = RoundTripThresholdsDocument.FromStatistics(
            MakeStats(), "deadbeef", 47418, MeasuredAt,
            "arm64-darwin", "arm64-darwin (osx-arm64, macOS 26.6.2)");

        Assert.True(document.Provisional);
        Assert.Equal("arm64-darwin", document.MeasuredOn);
        Assert.Contains("PROVISIONAL", document.Notes, StringComparison.Ordinal);
        Assert.Contains("arm64-darwin (osx-arm64, macOS 26.6.2)", document.Notes, StringComparison.Ordinal);
        Assert.Contains("RE-MEASURED on win-x64", document.Notes, StringComparison.Ordinal);
        // No hardcoded "the Mac" -- the notes name the actual architecture
        // that produced the measurement, whatever it happens to be.
        Assert.DoesNotContain("measured on the Mac", document.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromStatistics_ForeignNonMacArchitecture_NotesNameItRatherThanAssumingTheMac()
    {
        // A hypothetical third architecture, chosen specifically to prove
        // the notes text is not silently assuming "foreign == the Mac".
        var document = RoundTripThresholdsDocument.FromStatistics(
            MakeStats(), "deadbeef", 47418, MeasuredAt,
            "arm64-linux", "arm64-linux (linux-arm64, Ubuntu 24.04)");

        Assert.True(document.Provisional);
        Assert.Contains("arm64-linux (linux-arm64, Ubuntu 24.04)", document.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("the Mac", document.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arm64-darwin", document.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void FromStatistics_PublicOverload_UsesTheCurrentMachinesRealArchitecture()
    {
        // The public overload must actually consult ArchitectureProvenance
        // rather than silently defaulting one way -- whichever machine
        // runs this test, `Provisional` must agree with whether THIS
        // machine's own token is the ship architecture.
        var document = RoundTripThresholdsDocument.FromStatistics(MakeStats(), "deadbeef", 47418, MeasuredAt);

        var expectedProvisional = !string.Equals(
            ArchitectureProvenance.CurrentToken(), RoundTripThresholdsDocument.ShipArchitectureToken, StringComparison.Ordinal);

        Assert.Equal(expectedProvisional, document.Provisional);
        Assert.Equal(ArchitectureProvenance.CurrentToken(), document.MeasuredOn);
    }
}

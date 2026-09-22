using LoreFetch.Lab.Printings;
using Xunit;

namespace LoreFetch.Tests.StreamB.Bulk;

/// Synthetic sample covering every reason a printing can fall out of
/// scope, plus the two shapes that matter for the fraction itself: an
/// illustration with exactly one in-scope printing (a singleton) and one
/// with more than one (not a singleton).
///
///   illus-A  1 in-scope printing                                -> singleton
///   illus-B  2 in-scope printings                                -> not singleton
///   illus-C  1 in-scope + 1 foil-only (filtered out)             -> singleton
///            (the foil-only printing must not count toward illus-C's total)
///   illus-D  non-English only                                    -> excluded entirely
///   illus-E  no top-level image_uris (multi-faced) only           -> excluded entirely
///   illus-F  frame "1997" only                                    -> excluded entirely
///   illus-G  layout "token" only                                  -> excluded entirely
///   illus-H  set_type "memorabilia" only                          -> excluded entirely
public class PrintingsAnalyzerTests
{
    private static RawPrinting InScope(string illustrationId) => new(
        IllustrationId: illustrationId,
        Lang: "en",
        Finishes: new[] { "nonfoil" },
        HasImageUris: true,
        Frame: "2015",
        Layout: "normal",
        SetType: "expansion");

    [Fact]
    public void Analyze_SyntheticSample_ComputesTheStatedFraction()
    {
        var printings = new List<RawPrinting>
        {
            InScope("illus-A"),
            InScope("illus-B"),
            InScope("illus-B"), // second in-scope printing of the same art
            InScope("illus-C"),
            InScope("illus-C") with { Finishes = new[] { "foil" } }, // filtered out
            InScope("illus-D") with { Lang = "de" },
            InScope("illus-E") with { HasImageUris = false },
            InScope("illus-F") with { Frame = "1997" },
            InScope("illus-G") with { Layout = "token" },
            InScope("illus-H") with { SetType = "memorabilia" },
        };

        var result = PrintingsAnalyzer.Analyze(printings);

        Assert.Equal(4, result.InScopePrintingCount); // A, B x2, C's nonfoil
        Assert.Equal(3, result.DistinctIllustrationCount); // A, B, C -- not D..H
        Assert.Equal(2, result.SingletonIllustrationCount); // A and C
        Assert.Equal(2.0 / 3.0, result.SingletonFraction, precision: 10);
    }

    [Fact]
    public void Analyze_NoInScopePrintings_ReturnsZeroFractionRatherThanDividingByZero()
    {
        var printings = new List<RawPrinting> { InScope("illus-D") with { Lang = "de" } };

        var result = PrintingsAnalyzer.Analyze(printings);

        Assert.Equal(0, result.DistinctIllustrationCount);
        Assert.Equal(0, result.SingletonIllustrationCount);
        Assert.Equal(0.0, result.SingletonFraction);
    }

    [Fact]
    public void Analyze_FoilOnlyPrinting_IsExcludedByTheFinishesCheck()
    {
        var printings = new List<RawPrinting> { InScope("illus-Z") with { Finishes = new[] { "foil" } } };

        var result = PrintingsAnalyzer.Analyze(printings);

        Assert.Equal(0, result.InScopePrintingCount);
        Assert.Equal(0, result.DistinctIllustrationCount);
    }
}

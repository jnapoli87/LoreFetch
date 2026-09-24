using LoreFetch.Core.Identification;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// The gate B1's own doc calls "the point, not a fallback": a committed
/// expected 1024-bit hash for deterministic, code-generated inputs, run
/// through the REAL `ReferenceTransform`/`QueryTransform` + `CardHasher`
/// paths. Bound-based invariant tests (see `ReferenceTransformTests`) pass
/// just as happily against a transform that quietly changed -- e.g.
/// swapping `CardHasher.ToIcon`'s `INTER_AREA` for `INTER_LINEAR` leaves
/// every one of B1a's 29 tests green, because none of them pin an exact
/// value. Only a golden notices that.
///
/// Generated 2026-09-21 on win-x64
/// (`RuntimeInformation.OSArchitecture` = `X64`), OpenCvSharp4
/// 4.13.0.20260627 -- see CLAUDE.md "The one gate that matters most":
/// `INTER_AREA` is not bit-exact across x86-64/ARM64, so these values are
/// only meaningful pinned to the architecture that produced them, hence
/// `WindowsOnly` rather than a shared cross-platform golden.
///
/// Inputs are deterministic and procedural (`SyntheticImages`, fixed seeds,
/// no time/culture dependence) -- never a file on disk, so there is no
/// card-imagery risk (CLAUDE.md "Never commit card imagery"). The noise
/// input matters most: a regular pattern like a checkerboard degenerates to
/// the exact same flat gray under both `INTER_AREA`'s box mean and
/// `INTER_LINEAR`'s bilinear sample (measured -- its hash is all zero bits
/// either way), so it cannot catch a filter swap. True per-pixel noise's
/// local average genuinely differs between the two filters.
[Trait("Category", "WindowsOnly")]
public class GoldenHashTests
{
    public enum GoldenInput
    {
        CardLike101,
        CardLike202,
        CardLike303,
        Noise404,
    }

    public static readonly TheoryData<GoldenInput, string> ReferencePathGoldens = new()
    {
        { GoldenInput.CardLike101, "fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c080fcfcf8f0e0e0c0c0fcfcf8f8f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fefcf8f0f0e0c0c0fcfcf8f0f0e0c0c0fcf8f8f0f0e0c080fcfcf8f0e0e0c0c0fcfcf8f8f0e0e0c0fcfcf8f8f0e0c0c0" },
        { GoldenInput.CardLike202, "fcf8f8f0e0e0c0c0fcf8f8f8e0e0c0c0fcfcf8f0f0e0c080fcf8f8f0f0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f8f0e0e0c0fcf8f8f0f0e0c0c0fcf8f8f0e0e0c0c0fcfcf8f0f0e0c0c0fcf8f8f0f0e0e0c0fcf8f8f0f0e0c0c0fcf8f8f0e0e0c080fcfcf8f0f0e0e0c0fcfcf8f0f0e0c0c0" },
        { GoldenInput.CardLike303, "fcf8f8f0f0e0c0c0fcf8f0f0e0e0c0c0fcf8f8f0e0e0c0c0fcf8f0f0e0e0c0c0fcf8f8f0e0e0c080fcf8f8f0e0e0c080fcf8f8f0f0e0c0c0fcfcf8f0e0e0e0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f0e0e0e0c0fcfcf8f0f0e0e0c0fcfcf8f0e0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f0f0e0c0c0fcfcf8f0f0e0e0c0" },
        { GoldenInput.Noise404, "12ae3f2c8ed682a6ab989279059308dba0237a907224b7930127a6e0412102d39b88308cc4b7a4be028c56b29c21e60974d3842bcaa366307e1ce0399bc2424c880937a3da1431009c2825761191818c7d27368452a33c8c29451386c0c49a3b111e868136506b864cfb3fc484414c4d119028e31b729a80026230233704f044" },
    };

    public static readonly TheoryData<GoldenInput, string> QueryPathGoldens = new()
    {
        { GoldenInput.CardLike101, "fcf8f8f0e0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c080fcf8f8f0e0e0c0c0fcfcf8f8f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fefcf8f8f0e0e0c0fcfcf8f0f0e0c0c0fcf8f8f0f0e0c080fcfcf8f0e0e0c0c0fcfcf8f0f0e0e0c0fcfcf8f8f0e0c0c0" },
        { GoldenInput.CardLike202, "fcf8f8f0e0e0c0c0fcf8f8f8e0e0c0c0fcfcf8f0f0e0c080fcf8f8f0f0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f8f0e0e0c0fcf8f8f0f0e0c0c0fcf8f8f0e0e0c0c0fcfcf8f0f0e0c0c0fcf8f8f0f0e0e0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f8f0e0e0c0fcfcf8f8f0e0c0c0" },
        { GoldenInput.CardLike303, "fcf8f8f0f0e0c0c0fcf8f0f0e0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f0e0e0e0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f0e0e0e0c0fcfcf8f0f0e0c0c0fcf8f8f0e0e0c0c0fcf8f8f0f0e0c0c0fcfcf8f8f0e0e0c0fcf8f8f0f0e0e0c0" },
        { GoldenInput.Noise404, "12aa3f0d0ed402a2ab98127305d708daa0237ac07624b797a127a6ec417382db9b80308cc4b7a42e029c52b7b420e60970d3842b4b876630de14e0399bc05248882937a3d8183540dc29677e4191818c792706904a833c8c29651386c9d49b3b110c863154502a8644f32f86844d4c8c109018e31d729b84027238a33d04f046" },
    };

    [Theory]
    [MemberData(nameof(ReferencePathGoldens))]
    public void ReferencePath_ProducesTheCommittedGolden(GoldenInput input, string expectedHex)
    {
        using var bgr = BuildBgr(input);
        using var gray = ReferenceTransform.Prepare(bgr);

        var hash = CardHasher.Hash(gray);

        Assert.Equal(expectedHex, hash.ToString());
    }

    [Theory]
    [MemberData(nameof(QueryPathGoldens))]
    public void QueryPath_ProducesTheCommittedGolden(GoldenInput input, string expectedHex)
    {
        using var bgr = BuildBgr(input);
        var card = SyntheticImages.ToRectifiedCard(bgr);
        using var gray = QueryTransform.Prepare(card);

        var hash = CardHasher.Hash(gray);

        Assert.Equal(expectedHex, hash.ToString());
    }

    private static Mat BuildBgr(GoldenInput input) => input switch
    {
        GoldenInput.CardLike101 => SyntheticImages.MakeCardLikeBgr(488, 680, seed: 101),
        GoldenInput.CardLike202 => SyntheticImages.MakeCardLikeBgr(488, 680, seed: 202),
        GoldenInput.CardLike303 => SyntheticImages.MakeCardLikeBgr(488, 680, seed: 303),
        GoldenInput.Noise404 => SyntheticImages.MakeHighFrequencyNoiseBgr(488, 680, seed: 404),
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, null),
    };
}

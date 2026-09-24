using LoreFetch.Lab.Bulk;
using Xunit;

namespace LoreFetch.Tests.Lab.Bulk;

/// Exercises the real filter cascade (`ArtworkFilterCascade.Run`) against
/// the real JSON parsing (`ScryfallJsonl.ReadLines` + `RawArtwork.Parse`)
/// over `synthetic-unique-artwork.jsonl`, so a bug in either the parsing
/// or the cascade order shows up here rather than only against the live
/// 55k-object file.
///
/// The fixture has exactly one record per cascade branch:
///   art-0001  survives everything, frame 2015                (baseline)
///   art-0002  survives everything, frame 1997                (informational-only miss)
///   art-0003  image_status "placeholder"                     -> dropped at step 2
///   art-0004  image_status "missing"                         -> dropped at step 2
///   art-0005  lang "fr"                                       -> dropped at step 3
///   art-0006  no top-level image_uris at all (multi-faced)    -> dropped at step 4, counted
///   art-0007  layout "token" (set_type NOT excluded)          -> dropped at step 5
///   art-0008  set_type "memorabilia" (layout NOT excluded)    -> dropped at step 6
///   art-0009  basic land, survives everything, frame 2015
///   art-0010  same oracle_id as art-0001 (a reprint), frame 1997, survives everything
///   art-0011  digital:true (Alchemy-shaped, own oracle_id)       -> dropped at step 7
public class ArtworkFilterCascadeTests
{
    [Fact]
    public void Run_OnSyntheticSample_ProducesTheExactStepCascade()
    {
        var result = RunOnFixture();

        AssertStep(result.Steps[0], ArtworkFilterCascade.RawStepName, arts: 11, oracleIds: 10);
        AssertStep(result.Steps[1], ArtworkFilterCascade.ImageStatusStepName, arts: 9, oracleIds: 8);
        AssertStep(result.Steps[2], ArtworkFilterCascade.LangStepName, arts: 8, oracleIds: 7);
        AssertStep(result.Steps[3], ArtworkFilterCascade.HasImageUrisStepName, arts: 7, oracleIds: 6);
        AssertStep(result.Steps[4], ArtworkFilterCascade.LayoutStepName, arts: 6, oracleIds: 5);
        AssertStep(result.Steps[5], ArtworkFilterCascade.SetTypeStepName, arts: 5, oracleIds: 4);
        AssertStep(result.Steps[6], ArtworkFilterCascade.DigitalOnlyStepName, arts: 4, oracleIds: 3);

        Assert.Equal(7, result.Steps.Count);
    }

    [Fact]
    public void Run_OnSyntheticSample_DropsDigitalOnlyRecordsButKeepsNonDigitalReprint()
    {
        var result = RunOnFixture();

        // art-0011 is digital:true and otherwise passes every earlier
        // filter (en, highres_scan, has image_uris, normal layout,
        // non-excluded set_type) -- it must be dropped by the digital
        // step specifically, not by coincidence of an earlier one.
        Assert.DoesNotContain(result.Survivors, a => a.Id == "art-0011");

        // art-0010 shares oracle_id with art-0001 but is NOT digital, and
        // must survive -- proving the new step filters on the `digital`
        // field, not on oracle_id or on any name-prefix heuristic.
        Assert.Contains(result.Survivors, a => a.Id == "art-0010");
    }

    [Fact]
    public void Run_OnSyntheticSample_CountsTheMultiFacedSkipExplicitly()
    {
        var result = RunOnFixture();

        // Exactly art-0006 -- the only record with no top-level image_uris.
        Assert.Equal(1, result.SkippedNoImageUrisCount);
    }

    [Fact]
    public void Run_OnSyntheticSample_SurvivorsAreExactlyTheUnexcludedRecords()
    {
        var result = RunOnFixture();

        var survivorIds = result.Survivors.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "art-0001", "art-0002", "art-0009", "art-0010" },
            survivorIds);
    }

    [Fact]
    public void Run_OnSyntheticSample_Frame2015CountIsInformationalOnly()
    {
        var result = RunOnFixture();

        // Of the 4 survivors, only art-0001 and art-0009 are frame "2015";
        // art-0002 and art-0010 are frame "1997" and must still be present
        // in Survivors, proving the frame filter was reported, not applied.
        Assert.Equal(2, result.InformationalFrame2015Count);
        Assert.Equal(4, result.Survivors.Count);
        Assert.Contains(result.Survivors, a => a.Id == "art-0002");
        Assert.Contains(result.Survivors, a => a.Id == "art-0010");
    }

    [Fact]
    public void Run_OnSyntheticSample_TracksDistinctOracleIdSeparatelyFromArtCount()
    {
        var result = RunOnFixture();

        // art-0001 and art-0010 share oracle-0001 -- 4 surviving arts, 3
        // distinct oracle ids, proving the two counters really are
        // independent rather than one being derived by assumption.
        Assert.Equal(4, result.FinalStep.ArtCount);
        Assert.Equal(3, result.FinalStep.DistinctOracleIdCount);
    }

    private static ArtworkCascadeResult RunOnFixture()
    {
        var path = BulkFixtures.UniqueArtworkSamplePath();
        var records = ScryfallJsonl.ReadLines(path).Select(RawArtwork.Parse);
        return ArtworkFilterCascade.Run(records);
    }

    private static void AssertStep(CascadeStepResult step, string expectedName, int arts, int oracleIds)
    {
        Assert.Equal(expectedName, step.Name);
        Assert.Equal(arts, step.ArtCount);
        Assert.Equal(oracleIds, step.DistinctOracleIdCount);
    }
}

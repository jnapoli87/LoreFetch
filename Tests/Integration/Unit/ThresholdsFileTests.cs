using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// `ThresholdsFile.Load` — package S0.4b. Loud-not-lenient per CONTRACTS.md
/// and orchestration V11: a missing file, an unsupported `formatVersion`, a
/// missing/`null` field, or malformed JSON must all throw rather than let a
/// confidence gate fall back to a silently-defaulted value.
public class ThresholdsFileTests
{
    private const string ValidJson = """
        {
          "formatVersion": 1,
          "goodDistance": 100,
          "okDistance": 220,
          "referenceFloor": 37,
          "indexArtworkCount": 54963,
          "indexSha256": "abc123def456",
          "measuredAt": "2026-09-21T12:00:00Z",
          "notes": "measured against unique_artwork build 2026-09-21"
        }
        """;

    [Fact]
    public void Load_ValidV1File_RoundTripsEveryField()
    {
        var path = WriteTempFile(ValidJson);

        var thresholds = ThresholdsFile.Load(path);

        Assert.Equal(1, thresholds.FormatVersion);
        Assert.Equal(100, thresholds.GoodDistance);
        Assert.Equal(220, thresholds.OkDistance);
        Assert.Equal(37, thresholds.ReferenceFloor);
        Assert.Equal(54963, thresholds.IndexArtworkCount);
        Assert.Equal("abc123def456", thresholds.IndexSha256);
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T12:00:00Z"), thresholds.MeasuredAt);
        Assert.Equal("measured against unique_artwork build 2026-09-21", thresholds.Notes);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lorefetch-thresholds-missing-{Guid.NewGuid():N}.json");

        Assert.Throws<FileNotFoundException>(() => ThresholdsFile.Load(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void Load_UnsupportedFormatVersion_ThrowsInvalidDataException(int badVersion)
    {
        var json = WithField(ValidJson, "formatVersion", badVersion.ToString());
        var path = WriteTempFile(json);

        Assert.Throws<InvalidDataException>(() => ThresholdsFile.Load(path));
    }

    public static IEnumerable<object[]> AllFieldNames()
    {
        yield return new object[] { "formatVersion" };
        yield return new object[] { "goodDistance" };
        yield return new object[] { "okDistance" };
        yield return new object[] { "referenceFloor" };
        yield return new object[] { "indexArtworkCount" };
        yield return new object[] { "indexSha256" };
        yield return new object[] { "measuredAt" };
        yield return new object[] { "notes" };
    }

    [Theory]
    [MemberData(nameof(AllFieldNames))]
    public void Load_EachFieldMissing_ThrowsInvalidDataExceptionNamingTheField(string fieldName)
    {
        var json = RemoveField(ValidJson, fieldName);
        var path = WriteTempFile(json);

        var ex = Assert.Throws<InvalidDataException>(() => ThresholdsFile.Load(path));
        Assert.Contains(fieldName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllFieldNames))]
    public void Load_EachFieldExplicitlyNull_ThrowsInvalidDataException(string fieldName)
    {
        var json = WithField(ValidJson, fieldName, "null");
        var path = WriteTempFile(json);

        Assert.Throws<InvalidDataException>(() => ThresholdsFile.Load(path));
    }

    [Fact]
    public void Load_NotesPresentButEmpty_LoadsFine()
    {
        var json = WithField(ValidJson, "notes", "\"\"");
        var path = WriteTempFile(json);

        var thresholds = ThresholdsFile.Load(path);

        Assert.Equal(string.Empty, thresholds.Notes);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsInvalidDataException_NotRawJsonException()
    {
        var path = WriteTempFile("{ this is not valid json ][");

        Assert.Throws<InvalidDataException>(() => ThresholdsFile.Load(path));
    }

    // -- Chaos-test harness (see orchestrator report; not left wired) ------
    //
    // Two deliberate sabotages were applied by hand, run, and reverted while
    // developing this test — not left in the tree:
    //   1. Defaulting a missing field to 0 instead of throwing: every
    //      Load_EachFieldMissing_* case failed, because Load returned a
    //      ThresholdsFile with (e.g.) GoodDistance == 0 instead of throwing.
    //      A defaulted GoodDistance of 0 means every real match — whose
    //      Hamming distance is always > 0 — reads as "worse than good",
    //      silently downgrading every confident hit to low-confidence.
    //   2. Accepting any formatVersion: every Load_UnsupportedFormatVersion_*
    //      case failed, because Load no longer threw for version 0, 2 or 99.

    private static string WriteTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lorefetch-thresholds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    /// Replaces `"field": <value>` with `"field": <newRawValue>` via a
    /// simple, deliberately non-clever string substitution — good enough for
    /// a fixed, known-shape test document without pulling in a JSON writer
    /// just to mutate one field.
    private static string WithField(string json, string fieldName, string newRawValue)
    {
        var lines = json.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith($"\"{fieldName}\"", StringComparison.Ordinal))
            {
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                var trailingComma = lines[i].TrimEnd().EndsWith(',') ? "," : "";
                lines[i] = $"{indent}\"{fieldName}\": {newRawValue}{trailingComma}";
            }
        }

        return string.Join('\n', lines);
    }

    private static string RemoveField(string json, string fieldName)
    {
        var lines = json.Split('\n').ToList();
        var index = lines.FindIndex(l => l.TrimStart().StartsWith($"\"{fieldName}\"", StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"Test fixture bug: field '{fieldName}' not found in fixture JSON.");
        }

        lines.RemoveAt(index);

        // Removing a middle field can leave the PREVIOUS line's trailing
        // comma dangling before the closing brace, or the new last field
        // missing a comma it never needed to begin with — only the former
        // is a real risk here since removal only ever shifts what is now
        // last. Strip a trailing comma from whatever line is now last
        // before the closing "}" and ensure the object still round-trips.
        var closeBraceIndex = lines.FindIndex(l => l.Trim() == "}");
        for (var i = closeBraceIndex - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            lines[i] = lines[i].TrimEnd().TrimEnd(',');
            break;
        }

        return string.Join('\n', lines);
    }
}

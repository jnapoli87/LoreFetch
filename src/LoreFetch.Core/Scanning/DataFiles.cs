namespace LoreFetch.Core.Scanning;

/// Resolves the paths of the two files `App.csproj` ships next to the app:
/// the hash index (format owned by stream B) and its thresholds file
/// (schema owned here, `ThresholdsFile`). Anchored at
/// `AppContext.BaseDirectory` rather than the current directory, which
/// differs between `dotnet run`, a test host and a published exe
/// (orchestration finding V11) — `App.csproj` copies `data/index/**` into
/// both the output and publish directories, so `BaseDirectory` is always
/// where they land.
public static class DataFiles
{
    private static readonly string IndexRelativePath = Path.Combine("data", "index", "cards.lfidx");
    private static readonly string ThresholdsRelativePath = Path.Combine("data", "index", "thresholds.json");

    public static string IndexPath => Path.Combine(AppContext.BaseDirectory, IndexRelativePath);

    public static string ThresholdsPath => Path.Combine(AppContext.BaseDirectory, ThresholdsRelativePath);
}

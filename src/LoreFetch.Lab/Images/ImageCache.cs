namespace LoreFetch.Lab.Images;

/// The one place that maps a Scryfall artwork id to its on-disk cache path.
/// B4c (`build-index`) reuses this so the two commands can never disagree
/// about where a given artwork's `normal` render lives.
public static class ImageCache
{
    /// Where the finished, validated download for `artworkId` lives.
    public static string GetImagePath(string cacheDir, string artworkId) =>
        Path.Combine(cacheDir, artworkId + ".jpg");

    /// Where a download is written while in flight. Never read by anything
    /// other than the downloader itself -- `GetImagePath` is the only path
    /// B4c or a "is this cached?" check should ever look at.
    public static string GetTempPath(string cacheDir, string artworkId) =>
        Path.Combine(cacheDir, artworkId + ".jpg.download");
}

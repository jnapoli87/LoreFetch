using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Opens a `FolderFrameSource`. `ScanSettings` has nowhere to carry a folder
/// path (orchestration finding V20), so the folder and cycle interval are
/// constructor arguments here instead, supplied by whoever wires up the demo
/// path (the app, or a test).
public sealed class FolderFrameSourceFactory : IFrameSourceFactory
{
    private readonly string _folder;
    private readonly TimeSpan _interval;

    public FolderFrameSourceFactory(string folder, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _folder = folder;
        _interval = interval;
    }

    /// Opens the folder source. Throws FrameSourceException when the folder
    /// is missing or contains no decodable image — the folder-source
    /// equivalent of "no such device".
    public Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        IFrameSource source = FolderFrameSource.Open(_folder, _interval);
        return Task.FromResult(source);
    }
}

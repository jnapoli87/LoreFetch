using LoreFetch.Core.Abstractions;

namespace LoreFetch.App;

/// <summary>
/// The run LoreFetch is writing to (CONTEXT.md "Run"): one native CSV per
/// scanning batch, which the user exports and uploads to their real
/// collection. This is the <see cref="ICollectionStore"/> the window commits
/// to and lists from. It delegates to a store for the current file, and can
/// be pointed at another file mid-session (New run, Open, Rename).
/// </summary>
/// <remarks>
/// <para>
/// A new run's file does not exist until its first commit — the store reads
/// a missing file as empty — so launching and closing without scanning
/// leaves nothing behind. Its folder is created at that first commit too,
/// not before, so nothing touches the user's Documents folder unless a card
/// is actually committed.
/// </para>
/// <para>
/// This type knows only paths. <c>AppComposition</c> supplies
/// <c>openStore</c>, which is how the real <c>CsvCollectionStore</c> gets in
/// without the window ever naming it (Tests/Architecture,
/// <c>App_OnlyTheCompositionRootKnowsConcreteImplementations</c>).
/// </para>
/// <para>
/// Switching runs is not safe while a commit is in flight, because that
/// commit would land in whichever run is current when it started. The
/// window refuses every switch while a cohort is pending, and a cohort
/// stays pending until its commit has succeeded, which covers it.
/// </para>
/// </remarks>
public sealed class RunStore : ICollectionStore
{
    public const string FileExtension = ".csv";

    /// The date-and-time name a new run gets, e.g. "2026-10-02 14-30". A
    /// dash, not a colon, between hour and minute: a colon is not legal in a
    /// Windows file name.
    public const string NameFormat = "yyyy-MM-dd HH-mm";

    private readonly Func<string, ICollectionStore> _openStore;
    private readonly TimeProvider _clock;
    private ICollectionStore _current;

    /// <param name="runsFolder">Where new runs are created and recent runs are listed from.</param>
    /// <param name="openStore">Opens the store for one run file.</param>
    /// <param name="clock">Names new runs. Tests pass a fixed clock.</param>
    /// <param name="startPath">
    /// The run to open at startup (<c>LOREFETCH_COLLECTION</c>). When null,
    /// startup begins a new run instead.
    /// </param>
    public RunStore(string runsFolder, Func<string, ICollectionStore> openStore, TimeProvider clock, string? startPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runsFolder);
        ArgumentNullException.ThrowIfNull(openStore);
        ArgumentNullException.ThrowIfNull(clock);

        RunsFolder = System.IO.Path.GetFullPath(runsFolder);
        _openStore = openStore;
        _clock = clock;

        Path = startPath is null ? NewRunPath() : System.IO.Path.GetFullPath(startPath);
        _current = _openStore(Path);
    }

    public string RunsFolder { get; }

    /// <summary>The current run's file. It may not exist yet (see the remarks).</summary>
    public string Path { get; private set; }

    /// <summary>The current run's file name without its extension, shown in the title and header.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Points at a fresh, empty run named after the current time.</summary>
    public void StartNew() => SwitchTo(NewRunPath());

    /// <summary>
    /// Opens an existing run. The file is read before anything switches, so a
    /// file that is not a native run (a Moxfield export, say) throws and
    /// leaves the current run untouched. Throws <see cref="FormatException"/>
    /// for a file that is not in the native format, and
    /// <see cref="CollectionStoreException"/> for one that cannot be read.
    /// </summary>
    public async Task OpenAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"'{System.IO.Path.GetFileName(fullPath)}' no longer exists.", fullPath);
        }

        var candidate = _openStore(fullPath);
        await candidate.ListAsync(ct);

        Path = fullPath;
        _current = candidate;
    }

    /// <summary>
    /// Renames the current run, in its own folder. Moves the file (and its
    /// <c>.bak</c>) when one has been written. Throws
    /// <see cref="ArgumentException"/> for an unusable name and
    /// <see cref="IOException"/> when a run of that name already exists or
    /// the file cannot be moved; either way the current run is unchanged.
    /// </summary>
    public void Rename(string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);

        var name = newName.Trim();
        if (name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^FileExtension.Length].TrimEnd();
        }

        if (name.Length == 0)
        {
            throw new ArgumentException("Enter a name for the run.", nameof(newName));
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"A run name can't contain any of: {InvalidCharsForDisplay()}", nameof(newName));
        }

        var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, name + FileExtension);
        if (string.Equals(newPath, Path, StringComparison.Ordinal))
        {
            return;
        }

        // OrdinalIgnoreCase: a case-only rename is legal, but File.Exists
        // would report the run's own file as the clash on Windows.
        if (File.Exists(newPath) && !string.Equals(newPath, Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"A run called '{name}' already exists.");
        }

        if (File.Exists(Path))
        {
            File.Move(Path, newPath);
            MoveBackupBestEffort(Path, newPath);
        }

        SwitchTo(newPath);
    }

    /// <summary>
    /// The most recently written runs in <see cref="RunsFolder"/>, newest
    /// first, excluding the current one. Listing the folder is the whole
    /// mechanism: there is no recent-files list to keep in sync.
    /// </summary>
    public IReadOnlyList<string> ListRecent(int max = 10)
    {
        if (!Directory.Exists(RunsFolder))
        {
            return [];
        }

        // The pattern narrows the listing; the exact extension check is what
        // keeps out anything else that sits beside a run (its .bak, the
        // store's .tmp files), whatever the platform's pattern rules are.
        return Directory.EnumerateFiles(RunsFolder, "*" + FileExtension)
            .Where(p => string.Equals(System.IO.Path.GetExtension(p), FileExtension, StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.Equals(System.IO.Path.GetFullPath(p), Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(max)
            .ToList();
    }

    /// <inheritdoc />
    public Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct)
    {
        // The store writes its temp file next to the run, so the folder has
        // to exist; creating it here rather than at startup is what keeps a
        // launch with no commits from touching Documents.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        return _current.CommitCohortAsync(cohort, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct) => _current.ListAsync(ct);

    private void SwitchTo(string path)
    {
        Path = path;
        _current = _openStore(path);
    }

    /// <summary>
    /// A path in <see cref="RunsFolder"/> named after the current local time.
    /// Two runs started in the same minute get " (2)", " (3)", ... so a new
    /// run never silently continues an earlier one.
    /// </summary>
    private string NewRunPath()
    {
        var stem = _clock.GetLocalNow().ToString(NameFormat, System.Globalization.CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(RunsFolder, stem + FileExtension);

        for (var n = 2; File.Exists(path) || string.Equals(path, Path, StringComparison.OrdinalIgnoreCase); n++)
        {
            path = System.IO.Path.Combine(RunsFolder, $"{stem} ({n}){FileExtension}");
        }

        return path;
    }

    // The .bak is a convenience copy of the previous write; failing to move
    // it must not fail a rename whose real file already moved.
    private static void MoveBackupBestEffort(string oldPath, string newPath)
    {
        try
        {
            if (File.Exists(oldPath + ".bak"))
            {
                File.Move(oldPath + ".bak", newPath + ".bak");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string InvalidCharsForDisplay() =>
        string.Join(" ", System.IO.Path.GetInvalidFileNameChars().Where(c => !char.IsControl(c)));
}

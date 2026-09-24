using System.Runtime.CompilerServices;

// Lets `Tests/Lab` exercise the internal, testable overload of
// `TryFindRepoRoot` below (arbitrary start directories, not the real
// process ones), and `RoundTripThresholdsDocument`'s internal overload.
[assembly: InternalsVisibleTo("LoreFetch.Tests.Lab")]

namespace LoreFetch.Lab;

/// Locates paths relative to the repository root rather than the process's
/// current working directory, so `bulk`/`printings`/`images`/`build-index`
/// default to the same place whether invoked via `dotnet run` from the
/// project folder or via a built exe from anywhere.
public static class RepoPaths
{
    private const string SolutionFileName = "LoreFetch.slnx";

    /// Walks up from the running executable's own directory, then --  if
    /// that search fails -- from the process's current working directory,
    /// looking for the solution file. The second attempt is what fixes the
    /// B4b-era defect: a Lab build copied OUT of the repo tree has an exe
    /// directory that will never contain (or sit under) `LoreFetch.slnx`
    /// no matter how far up it walks, but it may still be invoked with its
    /// working directory pointed at a real checkout -- that case used to
    /// throw unconditionally.
    ///
    /// Throws `RepoRootNotFoundException` -- never lets a bare stack trace
    /// reach the console -- when neither search finds it. The message is
    /// deliberately generic: this type has no idea which command or which
    /// flag called it, so it names none. EVERY call site must catch this
    /// and report its own command's flags in a clean, non-zero exit; see
    /// `TryFindRepoRoot` for the non-throwing alternative most call sites
    /// should actually use.
    public static string FindRepoRoot()
    {
        if (TryFindRepoRoot(out var repoRoot))
        {
            return repoRoot!;
        }

        throw new RepoRootNotFoundException(
            $"Could not locate {SolutionFileName} above \"{AppContext.BaseDirectory}\" " +
            $"or above the current directory \"{Environment.CurrentDirectory}\".");
    }

    /// Same two-stage search as `FindRepoRoot`, without throwing -- the
    /// form a command should reach for whenever it can fall back to
    /// requiring an explicit path instead of crashing. Returns false
    /// (rather than any exception) when neither the exe directory nor the
    /// working directory sits inside a checkout.
    public static bool TryFindRepoRoot(out string? repoRoot) =>
        TryFindRepoRoot([AppContext.BaseDirectory, Environment.CurrentDirectory], out repoRoot);

    /// The same search over caller-supplied start directories, tried in
    /// order -- `internal` purely so `Tests/Lab` can drive it against
    /// throwaway temp directories instead of the real process paths, which
    /// a unit test cannot control.
    internal static bool TryFindRepoRoot(IEnumerable<string> candidateStartDirectories, out string? repoRoot)
    {
        foreach (var start in candidateStartDirectories)
        {
            var found = SearchUpFrom(start);
            if (found is not null)
            {
                repoRoot = found;
                return true;
            }
        }

        repoRoot = null;
        return false;
    }

    /// `FindRepoRoot`'s throwing form is deliberately NOT used here:
    /// commands that only need the bulk dir as a fallback default (when
    /// `--out`/`--manifest` was not passed explicitly) should report their
    /// own flag name on failure rather than surface this type's generic
    /// message, so this still throws `RepoRootNotFoundException` for them
    /// to catch, not `InvalidOperationException`.
    public static string DefaultScryfallBulkDir() => Path.Combine(FindRepoRoot(), "scryfall-bulk");

    private static string? SearchUpFrom(string startDirectory)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(startDirectory);
        }
        catch (ArgumentException)
        {
            return null;
        }

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

/// Thrown by `FindRepoRoot`/`DefaultScryfallBulkDir` when neither the exe
/// directory nor the working directory search locates the repository.
/// Deliberately generic -- see `FindRepoRoot`'s own doc comment for why --
/// so every call site catches this specifically and reports its OWN
/// command's flags instead of letting this (or any stack trace) reach the
/// console unhandled.
public sealed class RepoRootNotFoundException : Exception
{
    public RepoRootNotFoundException(string message)
        : base(message)
    {
    }
}

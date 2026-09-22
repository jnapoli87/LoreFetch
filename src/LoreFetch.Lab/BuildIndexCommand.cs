using System.Security.Cryptography;
using System.Text.Json;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Index;

namespace LoreFetch.Lab;

/// `lab build-index [--manifest <path>] --cache <dir> --out <file.lfidx>
///   [--subset N] [--parallelism N] [--allow-missing] [--ids <file>]
///   [--fill N]`
///
/// Composes `IndexSelection` (which arts, what order) with `IndexBuilder`
/// (`ReferenceTransform` -> `CardHasher`, the ONLY pixel-touching code in
/// this command) and `AtomicIndexWriter` (temp file, verify, rename) into
/// the actual CLI: argument parsing, error messages, the console summary,
/// and the `<out>.build.json` sidecar. No transform logic lives here.
public static class BuildIndexCommand
{
    public static Task<int> RunAsync(string[] args) => RunAsync(args, null);

    /// `writeIndex` is injectable purely so `BuildIndexCommandTests` can
    /// drive the atomic-write failure path without a real multi-gigabyte
    /// run -- see `AtomicIndexWriter`'s own doc comment. Production always
    /// leaves it null, which defaults to the real `HashIndexFile.Write`.
    ///
    /// Not `async` -- every step here (manifest read, hashing, the atomic
    /// write, the re-read verification) is synchronous; there is nothing to
    /// await. Wrapping the synchronous result in `Task.FromResult` keeps
    /// the signature consistent with `bulk`/`printings`/`images`, which ARE
    /// genuinely async (they make HTTP calls), without an `async` method
    /// that contains no real `await` -- which `src/`'s
    /// `TreatWarningsAsErrors` would turn into a build failure (CS1998).
    internal static Task<int> RunAsync(
        string[] args, Action<string, IReadOnlyList<HashIndexEntry>>? writeIndex) =>
        Task.FromResult(Run(args, writeIndex));

    private static int Run(string[] args, Action<string, IReadOnlyList<HashIndexEntry>>? writeIndex)
    {
        BuildIndexArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var manifestPath = parsed.ManifestPath;
        if (manifestPath is null)
        {
            // Non-throwing lookup: whether the repo can be located decides
            // what this command can DEFAULT, not whether it can run --
            // see RepoPaths.FindRepoRoot's doc comment for the B4b-era
            // defect (a hardcoded "--out" in an unhandled exception) this
            // pattern replaces.
            if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
            {
                Console.Error.WriteLine(
                    "build-index: could not locate the repository automatically. Pass --manifest explicitly.");
                return 1;
            }

            manifestPath = Path.Combine(repoRoot!, "scryfall-bulk", BulkCommand.ManifestFileName);
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine(
                $"build-index: manifest not found at \"{manifestPath}\". Run \"lab bulk\" first, " +
                "or pass --manifest explicitly.");
            return 1;
        }

        if (!Directory.Exists(parsed.CacheDir))
        {
            Console.Error.WriteLine($"build-index: cache directory not found: \"{parsed.CacheDir}\".");
            return 1;
        }

        IReadOnlyList<string>? idsFromFile = null;
        if (parsed.IdsPath is not null)
        {
            if (!File.Exists(parsed.IdsPath))
            {
                Console.Error.WriteLine($"build-index: --ids file not found: \"{parsed.IdsPath}\".");
                return 1;
            }

            idsFromFile = File.ReadAllLines(parsed.IdsPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            if (idsFromFile.Count == 0)
            {
                Console.Error.WriteLine($"build-index: --ids file \"{parsed.IdsPath}\" has no ids in it.");
                return 1;
            }
        }

        var manifestEntries = FilteredArtworkManifest.Read(manifestPath);
        Console.WriteLine($"Manifest: {manifestPath} ({manifestEntries.Count} entries)");
        Console.WriteLine($"Cache: {parsed.CacheDir}");

        string mode;
        IReadOnlyList<ManifestEntry> selected;

        if (idsFromFile is not null)
        {
            selected = IndexSelection.ByIds(manifestEntries, idsFromFile, parsed.Fill, out var unmatched);
            if (unmatched.Count > 0)
            {
                Console.Error.WriteLine(
                    $"build-index: {unmatched.Count} id(s) from --ids were not found in the manifest:");
                foreach (var id in unmatched)
                {
                    Console.Error.WriteLine($"  {id}");
                }

                return 1;
            }

            mode = parsed.Fill > 0 ? $"ids+fill:{parsed.Fill}" : "ids";
            Console.WriteLine(
                $"IDS index: {idsFromFile.Count} named + up to {parsed.Fill} filled " +
                $"= {selected.Count} of {manifestEntries.Count} arts");
        }
        else if (parsed.Subset is int subsetCount)
        {
            selected = IndexSelection.Subset(manifestEntries, subsetCount);
            mode = $"subset:{subsetCount}";
            Console.WriteLine($"SUBSET index: {selected.Count} of {manifestEntries.Count} arts");
        }
        else
        {
            selected = manifestEntries;
            mode = "full";
        }

        if (selected.Count == 0)
        {
            Console.Error.WriteLine("build-index: nothing selected to build -- the index would be empty.");
            return 1;
        }

        var buildOptions = new IndexBuildOptions
        {
            CacheDir = parsed.CacheDir,
            Parallelism = parsed.Parallelism,
        };

        Console.WriteLine($"Hashing {selected.Count} arts at parallelism {buildOptions.Parallelism}...");
        var buildResult = IndexBuilder.Build(selected, buildOptions);

        if (buildResult.MissingArtworkIds.Count > 0)
        {
            if (!parsed.AllowMissing)
            {
                Console.Error.WriteLine(
                    $"build-index: {buildResult.MissingArtworkIds.Count} of {selected.Count} images " +
                    "missing or undecodable:");
                foreach (var id in buildResult.MissingArtworkIds.Take(20))
                {
                    Console.Error.WriteLine($"  {id}");
                }

                if (buildResult.MissingArtworkIds.Count > 20)
                {
                    Console.Error.WriteLine($"  ... and {buildResult.MissingArtworkIds.Count - 20} more.");
                }

                Console.Error.WriteLine("Pass --allow-missing to build anyway, skipping these.");
                return 1;
            }

            Console.WriteLine(
                $"--allow-missing: skipping {buildResult.MissingArtworkIds.Count} missing/undecodable image(s).");
            mode += $"+allow-missing:{buildResult.MissingArtworkIds.Count}skipped";
        }

        Console.WriteLine(
            $"{buildResult.UnexpectedSizeCount} decoded image(s) were not 488x680 (handled; logged only).");

        try
        {
            AtomicIndexWriter.WriteAndPublish(parsed.OutPath, buildResult.Entries, writeIndex);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"build-index: failed to write \"{parsed.OutPath}\": {ex.Message}");
            return 1;
        }

        // Re-read the FINAL published file. AtomicIndexWriter already
        // verified the temp file's counts before publishing it; this is
        // the "assert entry count and oracle count match" step the brief
        // asks for applied to what actually ended up at --out, and it is
        // also where the summary's file size and SHA-256 come from.
        HashIndexData verify;
        try
        {
            verify = HashIndexFile.Read(parsed.OutPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"build-index: wrote \"{parsed.OutPath}\" but could not read it back: {ex.Message}");
            return 1;
        }

        if (verify.Entries.Count != buildResult.Entries.Count)
        {
            Console.Error.WriteLine(
                $"build-index: wrote {buildResult.Entries.Count} entries but read back {verify.Entries.Count} " +
                $"from \"{parsed.OutPath}\".");
            return 1;
        }

        var expectedOracleCount = buildResult.Entries.Select(e => e.OracleId).Distinct(StringComparer.Ordinal).Count();
        if (verify.OracleTable.Count != expectedOracleCount)
        {
            Console.Error.WriteLine(
                $"build-index: expected {expectedOracleCount} oracle cards but read back {verify.OracleTable.Count} " +
                $"from \"{parsed.OutPath}\".");
            return 1;
        }

        var fileInfo = new FileInfo(parsed.OutPath);
        var sha256 = HashIndexFile.ComputeSha256(parsed.OutPath);
        var basicLandCount = buildResult.Entries.Count(e => e.IsBasicLand);
        var throughput = buildResult.Elapsed.TotalSeconds > 0
            ? buildResult.Entries.Count / buildResult.Elapsed.TotalSeconds
            : 0;

        Console.WriteLine();
        Console.WriteLine($"Artworks: {buildResult.Entries.Count}");
        Console.WriteLine($"Oracle cards: {verify.OracleTable.Count}");
        Console.WriteLine($"Basic lands: {basicLandCount}");
        Console.WriteLine($"File: {parsed.OutPath} ({fileInfo.Length:N0} bytes)");
        Console.WriteLine($"SHA-256: {sha256}");
        Console.WriteLine($"Elapsed: {buildResult.Elapsed.TotalSeconds:F1}s ({throughput:F1} arts/s)");

        var manifestSha256 = ComputeFileSha256(manifestPath);
        var summary = new IndexBuildSummary(
            ArtworkCount: buildResult.Entries.Count,
            OracleCount: verify.OracleTable.Count,
            BasicLandCount: basicLandCount,
            FileSizeBytes: fileInfo.Length,
            Sha256: sha256,
            ElapsedSeconds: buildResult.Elapsed.TotalSeconds,
            ThroughputPerSecond: throughput,
            ManifestPath: manifestPath,
            ManifestSha256: manifestSha256,
            ManifestEntryCount: manifestEntries.Count,
            SelectedCount: selected.Count,
            Mode: mode,
            AllowMissing: parsed.AllowMissing,
            MissingCount: buildResult.MissingArtworkIds.Count,
            UnexpectedSizeCount: buildResult.UnexpectedSizeCount,
            BuiltAtUtc: DateTime.UtcNow);

        var sidecarPath = parsed.OutPath + ".build.json";
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(summary, IndexBuildSummaryJson.Options));
        Console.WriteLine($"Sidecar: {sidecarPath}");

        return 0;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private sealed record BuildIndexArgs(
        string? ManifestPath,
        string CacheDir,
        string OutPath,
        int? Subset,
        string? IdsPath,
        int Fill,
        int Parallelism,
        bool AllowMissing);

    private static BuildIndexArgs ParseArgs(string[] args)
    {
        string? manifestPath = null;
        string? cacheDir = null;
        string? outPath = null;
        int? subset = null;
        string? idsPath = null;
        var fill = 0;
        var parallelism = Math.Max(1, Environment.ProcessorCount);
        var allowMissing = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--cache" when i + 1 < args.Length:
                    cacheDir = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--subset" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var subsetValue) || subsetValue < 0)
                    {
                        throw new ArgumentException("build-index: --subset must be a non-negative integer.");
                    }

                    subset = subsetValue;
                    break;
                case "--ids" when i + 1 < args.Length:
                    idsPath = args[++i];
                    break;
                case "--fill" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out fill) || fill < 0)
                    {
                        throw new ArgumentException("build-index: --fill must be a non-negative integer.");
                    }

                    break;
                case "--parallelism" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out parallelism) || parallelism < 1)
                    {
                        throw new ArgumentException("build-index: --parallelism must be a positive integer.");
                    }

                    break;
                case "--allow-missing":
                    allowMissing = true;
                    break;
                default:
                    throw new ArgumentException($"build-index: unrecognised argument \"{args[i]}\".");
            }
        }

        if (cacheDir is null)
        {
            throw new ArgumentException("build-index: --cache <dir> is required.");
        }

        if (outPath is null)
        {
            throw new ArgumentException("build-index: --out <file.lfidx> is required.");
        }

        if (subset is not null && idsPath is not null)
        {
            throw new ArgumentException("build-index: --subset and --ids are mutually exclusive.");
        }

        if (fill > 0 && idsPath is null)
        {
            throw new ArgumentException("build-index: --fill requires --ids.");
        }

        return new BuildIndexArgs(manifestPath, cacheDir, outPath, subset, idsPath, fill, parallelism, allowMissing);
    }
}

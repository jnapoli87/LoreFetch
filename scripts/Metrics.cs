#:property PublishAot=false

// Test-count and coverage ratchet for CI. A .NET 10 file-based app:
//
//   dotnet run scripts/Metrics.cs -- collect <results-dir> <metrics.json>
//   dotnet run scripts/Metrics.cs -- compare <baseline.json> <current.json>
//       [--allow-tests-removed] [--allow-coverage-drop] [--summary <file>]
//
// `collect` reads the TRX and Cobertura files that
// `scripts/lorefetch.sh test --results <dir>` writes. `compare` fails when,
// against main's latest metrics, a test project has fewer tests, runs fewer
// of them (a test that became a skip), or a shipped assembly has lost more
// than half a point of line coverage. The PR labels `tests-removed` and `coverage-drop` pass the
// matching flag, so a deliberate change goes through on the record.
//
// It is a ratchet, not a target: it only asks that nothing goes backwards
// without someone saying so.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

return args switch
{
    ["collect", var resultsDir, var output] => Collect(resultsDir, output),
    ["compare", var baseline, var current, .. var rest] => Compare(baseline, current, rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Metrics.cs collect <results-dir> <metrics.json>");
    Console.Error.WriteLine("       Metrics.cs compare <baseline.json> <current.json> [--allow-tests-removed] [--allow-coverage-drop] [--summary <file>]");
    return 2;
}

// ---------------------------------------------------------------- collect

static int Collect(string resultsDir, string output)
{
    var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
    var coverageFiles = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
    if (trxFiles.Length == 0 || coverageFiles.Length == 0)
    {
        Console.Error.WriteLine($"Found {trxFiles.Length} TRX and {coverageFiles.Length} coverage files under {resultsDir}; need both.");
        return 1;
    }

    var metrics = new Metrics(
        Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "",
        ReadTestCounts(trxFiles),
        ReadCoverage(coverageFiles));

    if (metrics.Projects.Count == 0 || metrics.Projects.Values.Sum(p => p.Total) == 0)
    {
        Console.Error.WriteLine("The TRX files contain no tests. Refusing to record an empty baseline.");
        return 1;
    }

    File.WriteAllText(output, JsonSerializer.Serialize(metrics, Json.Options));
    Console.WriteLine($"Wrote {output}: {metrics.Projects.Count} test projects, {metrics.Coverage.Count} assemblies with coverage.");
    return 0;
}

static SortedDictionary<string, ProjectTests> ReadTestCounts(string[] trxFiles)
{
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var projects = new SortedDictionary<string, ProjectTests>(StringComparer.Ordinal);

    foreach (var file in trxFiles)
    {
        var doc = XDocument.Load(file);
        // TestMethod's codeBase keeps the assembly name's case; the
        // UnitTest's storage attribute is lowercased.
        var definitions = doc.Descendants(ns + "UnitTest").ToDictionary(
            t => (string)t.Attribute("id")!,
            t =>
            {
                var method = t.Element(ns + "TestMethod")!;
                return (Assembly: Path.GetFileNameWithoutExtension((string)method.Attribute("codeBase")!),
                        ClassName: (string?)method.Attribute("className") ?? "");
            });

        foreach (var result in doc.Descendants(ns + "UnitTestResult"))
        {
            var (assembly, className) = definitions[(string)result.Attribute("testId")!];
            var skipped = (string)result.Attribute("outcome")! == "NotExecuted";
            // Soft performance budgets skip or run depending on how fast the
            // machine is, so they would make the skip ratchet flaky.
            var timingDependent = className.EndsWith("PerformanceTests", StringComparison.Ordinal);

            projects.TryGetValue(assembly, out var counts);
            counts ??= new ProjectTests(0, 0);
            projects[assembly] = counts with
            {
                Total = counts.Total + 1,
                Skipped = counts.Skipped + (skipped && !timingDependent ? 1 : 0),
            };
        }
    }
    return projects;
}

static SortedDictionary<string, AssemblyCoverage> ReadCoverage(string[] coverageFiles)
{
    // Each test project reports coverage of the assemblies it loaded, so the
    // same source line appears in several files. A line counts as covered if
    // any test project covered it.
    var lines = new Dictionary<(string Assembly, string File, int Line), bool>();

    foreach (var file in coverageFiles)
    {
        foreach (var package in XDocument.Load(file).Descendants("package"))
        {
            var assembly = (string)package.Attribute("name")!;
            if (!assembly.StartsWith("LoreFetch.", StringComparison.Ordinal) ||
                assembly.StartsWith("LoreFetch.Tests.", StringComparison.Ordinal))
                continue;

            foreach (var @class in package.Descendants("class"))
            {
                var source = (string)@class.Attribute("filename")!;
                // Class-level <lines> only; method-level lines repeat them.
                foreach (var line in @class.Elements("lines").Elements("line"))
                {
                    var key = (assembly, source, (int)line.Attribute("number")!);
                    var hit = (long)line.Attribute("hits")! > 0;
                    lines[key] = lines.TryGetValue(key, out var seen) ? seen || hit : hit;
                }
            }
        }
    }

    return new SortedDictionary<string, AssemblyCoverage>(
        lines.GroupBy(l => l.Key.Assembly).ToDictionary(
            g => g.Key,
            g => new AssemblyCoverage(g.Count(l => l.Value), g.Count())),
        StringComparer.Ordinal);
}

// ---------------------------------------------------------------- compare

static int Compare(string baselinePath, string currentPath, string[] options)
{
    var allowTestsRemoved = options.Contains("--allow-tests-removed");
    var allowCoverageDrop = options.Contains("--allow-coverage-drop");
    var summaryIndex = Array.IndexOf(options, "--summary");
    var summaryPath = summaryIndex >= 0 && summaryIndex + 1 < options.Length ? options[summaryIndex + 1] : null;

    var current = Load(currentPath)!;
    var baseline = File.Exists(baselinePath) ? Load(baselinePath) : null;
    var report = new List<string> { "### Test and coverage ratchet", "" };
    var failures = new List<string>();

    if (baseline is null)
    {
        report.Add("No baseline from `main` yet, so nothing to compare against. This run's numbers become the first baseline once they land on `main`.");
        report.Add("");
        report.AddRange(Table(current, null, failures, allowTestsRemoved, allowCoverageDrop));
    }
    else
    {
        report.Add($"Compared with `main` at `{Short(baseline.Commit)}`.");
        report.Add("");
        report.AddRange(Table(current, baseline, failures, allowTestsRemoved, allowCoverageDrop));
    }

    if (failures.Count > 0)
    {
        report.Add("");
        report.Add("**Failing:**");
        report.AddRange(failures.Select(f => $"- {f}"));
    }

    var text = string.Join(Environment.NewLine, report);
    Console.WriteLine(text);
    if (summaryPath is not null)
        File.AppendAllText(summaryPath, text + Environment.NewLine);

    return failures.Count == 0 ? 0 : 1;
}

static IEnumerable<string> Table(Metrics current, Metrics? baseline, List<string> failures,
                                 bool allowTestsRemoved, bool allowCoverageDrop)
{
    yield return "| Test project | Tests | Ran (excl. timing-based skips) | |";
    yield return "|---|---:|---:|---|";

    var names = current.Projects.Keys.Union(baseline?.Projects.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal);
    foreach (var name in names)
    {
        current.Projects.TryGetValue(name, out var now);
        ProjectTests? before = null;
        baseline?.Projects.TryGetValue(name, out before);
        var verdict = "";

        if (before is not null)
        {
            if (now is null || now.Total < before.Total)
            {
                verdict = allowTestsRemoved ? "fewer tests (allowed by label)" : "**fewer tests**";
                if (!allowTestsRemoved)
                    failures.Add($"`{name}` has {now?.Total ?? 0} tests, down from {before.Total}. If that is deliberate, add the `tests-removed` label and say why in the PR.");
            }
            // Ran, not skipped: in CI every real-data test skips, so a new
            // gated test adds a skip without taking anything away. What must
            // not happen is an existing test that used to run becoming a skip.
            else if (now.Ran < before.Ran)
            {
                verdict = allowTestsRemoved ? "fewer ran (allowed by label)" : "**fewer ran**";
                if (!allowTestsRemoved)
                    failures.Add($"`{name}` ran {now.Ran} tests, down from {before.Ran}: a test that used to run now skips. If that is deliberate, add the `tests-removed` label and say why in the PR.");
            }
        }

        yield return $"| {name} | {Delta(now?.Total, before?.Total)} | {Delta(now?.Ran, before?.Ran)} | {verdict} |";
    }

    yield return "";
    yield return "| Assembly | Line coverage | |";
    yield return "|---|---:|---|";

    foreach (var (name, now) in current.Coverage)
    {
        AssemblyCoverage? before = null;
        baseline?.Coverage.TryGetValue(name, out before);
        var enforced = Enforced.Contains(name);
        var verdict = enforced ? "" : "reported only";

        if (enforced && before is not null && now.Rate < before.Rate - MaxCoverageDrop)
        {
            verdict = allowCoverageDrop ? "dropped (allowed by label)" : "**dropped**";
            if (!allowCoverageDrop)
                failures.Add($"`{name}` line coverage fell from {before.Rate:P1} to {now.Rate:P1}. Add tests for the new code, or the `coverage-drop` label with the reason in the PR.");
        }

        var rate = before is null ? $"{now.Rate:P1}" : $"{now.Rate:P1} (was {before.Rate:P1})";
        yield return $"| {name} | {rate} | {verdict} |";
    }
}

static string Delta(int? now, int? before) => (now, before) switch
{
    (null, null) => "",
    (null, var b) => $"0 (was {b})",
    (var n, null) => $"{n}",
    var (n, b) when n == b => $"{n}",
    var (n, b) => $"{n} (was {b})",
};

static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha == "" ? "unknown" : sha;

static Metrics? Load(string path) => JsonSerializer.Deserialize<Metrics>(File.ReadAllText(path), Json.Options);

// The shipped assemblies. The Lab is maintainer tooling: its coverage is
// reported but never fails a PR.
partial class Program
{
    static readonly HashSet<string> Enforced = ["LoreFetch.Core", "LoreFetch.Capture", "LoreFetch.App"];
    const double MaxCoverageDrop = 0.005;
}

record Metrics(string Commit, SortedDictionary<string, ProjectTests> Projects, SortedDictionary<string, AssemblyCoverage> Coverage);

record ProjectTests(int Total, int Skipped)
{
    [JsonIgnore]
    public int Ran => Total - Skipped;
}

record AssemblyCoverage(int LinesCovered, int LinesValid)
{
    [JsonIgnore]
    public double Rate => LinesValid == 0 ? 0 : (double)LinesCovered / LinesValid;
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

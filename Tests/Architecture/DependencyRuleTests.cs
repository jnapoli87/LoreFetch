using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace LoreFetch.Tests.Architecture;

/// The dependency directions the design docs already state, enforced. Each
/// rule cites where the decision is written down; change the doc and the
/// rule together, in a PR that says why. See docs/CONTRACTS.md, "Dependency
/// rules".
///
/// ArchUnitNET reads compiled IL, so a Release build can in principle hide a
/// dependency the compiler optimised away. That can only make a rule miss a
/// violation, never report a false one.
public class DependencyRuleTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader().LoadAssemblies(
        typeof(LoreFetch.Core.Abstractions.CameraFrame).Assembly,
        typeof(LoreFetch.Capture.WebcamFrameSourceFactory).Assembly,
        typeof(LoreFetch.App.AppComposition).Assembly,
        typeof(LoreFetch.Lab.RepoPaths).Assembly).Build();

    private const string Core = "LoreFetch.Core";

    // Types(true) includes referenced types from assemblies that are not
    // loaded (OpenCvSharp, Avalonia, FlashCap), so rules can name them.
    private static IObjectProvider<IType> Namespace(string name) =>
        Types(true).That().ResideInNamespaceMatching($@"^{Regex(name)}(\..+)?$").As(name);

    private static IObjectProvider<IType> Namespaces(params string[] names) =>
        Types(true).That().ResideInNamespaceMatching(
            "^(" + string.Join("|", names.Select(Regex)) + @")(\..+)?$").As(string.Join(", ", names));

    private static string Regex(string name) => System.Text.RegularExpressions.Regex.Escape(name);

    private static readonly IObjectProvider<IType> OpenCv = Namespace("OpenCvSharp");

    private static void AssertHolds(IArchRule rule)
    {
        var failures = rule.Evaluate(Architecture)
            .Where(result => !result.Passed)
            .Select(result => result.Description)
            .ToList();
        Assert.True(failures.Count == 0,
            $"Architecture rule broken: {rule.Description}{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", failures));
    }

    [Fact]
    public void ContractTypes_DoNotDependOnOpenCvSharp() =>
        // CONTRACTS.md: "No OpenCvSharp types appear in any contract".
        AssertHolds(Types().That().Are(Namespace($"{Core}.Abstractions"))
            .Should().NotDependOnAny(OpenCv)
            .Because("frames cross the seam as byte[], so the UI never writes CV code"));

    [Fact]
    public void Abstractions_DependOnNothingElseInLoreFetch() =>
        // CONTRACTS.md: Abstractions is the seam every domain builds against.
        AssertHolds(Types().That().Are(Namespace($"{Core}.Abstractions"))
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^LoreFetch\.")
                .And().AreNot(Namespace($"{Core}.Abstractions")))
            .Because("the contract types are the bottom of the dependency graph"));

    [Fact]
    public void ContractSurface_DoesNotDependOnAnyDomain() =>
        // CONTRACTS.md domain map: Scanning composes the domains through
        // Abstractions, and the fakes stand in for them; neither may reach
        // for a real implementation.
        AssertHolds(Types().That().Are(Namespaces($"{Core}.Scanning", $"{Core}.Fakes"))
            .Should().NotDependOnAny(Namespaces(
                $"{Core}.Detection", $"{Core}.Identification", $"{Core}.Collection",
                $"{Core}.Export", $"{Core}.Trigger", "LoreFetch.Capture", "LoreFetch.App", "LoreFetch.Lab"))
            .Because("the contract surface must stay replaceable-domain agnostic"));

    [Fact]
    public void Core_DoesNotDependOnAppCaptureLabUiOrCamera() =>
        // src/LoreFetch.Core/README.md: no Avalonia/UI and no FlashCap
        // dependency; Core must stay portable (the macOS CI leg).
        AssertHolds(Types().That().Are(Namespace(Core))
            .Should().NotDependOnAny(Namespaces("LoreFetch.App", "LoreFetch.Capture", "LoreFetch.Lab", "Avalonia", "FlashCap"))
            .Because("Core is the portable domain layer the other projects build on"));

    [Fact]
    public void App_DoesNotDependOnOpenCvSharpOrLab() =>
        // src/LoreFetch.App/README.md: the App does not reference OpenCvSharp;
        // Lab is maintainer tooling that does not ship.
        AssertHolds(Types().That().Are(Namespace("LoreFetch.App"))
            .Should().NotDependOnAny(Namespaces("OpenCvSharp", "LoreFetch.Lab"))
            .Because("the UI never writes CV code, and the shipped app never loads the Lab"));

    [Fact]
    public void App_OnlyTheCompositionRootKnowsConcreteImplementations() =>
        // CONTRACTS.md: the UI enumerates ICollectionExporter and holds no
        // per-format knowledge; AppComposition is the composition root.
        AssertHolds(Types().That().Are(Namespace("LoreFetch.App"))
            .And().DoNotHaveFullName("LoreFetch.App.AppComposition")
            .Should().NotDependOnAny(Namespaces(
                $"{Core}.Detection", $"{Core}.Identification", $"{Core}.Collection", $"{Core}.Export", "LoreFetch.Capture"))
            .Because("everything but the composition root talks to interfaces"));

    [Fact]
    public void Collection_DoesNotDependOnImageProcessing() =>
        // docs/design/collection.md: "No camera, no UI, no hash, no image
        // processing."
        AssertHolds(Types().That().Are(Namespaces($"{Core}.Collection", $"{Core}.Export"))
            .Should().NotDependOnAny(Namespaces("OpenCvSharp", $"{Core}.Detection", $"{Core}.Identification"))
            .Because("the collection store and exporters deal in rows, not pixels"));

    [Fact]
    public void Detection_DoesNotDependOnIdentification() =>
        AssertHolds(Types().That().Are(Namespace($"{Core}.Detection"))
            .Should().NotDependOnAny(Namespace($"{Core}.Identification"))
            .Because("detection finds and rectifies cards; it knows nothing about hashing"));

    [Fact]
    public void Identification_DoesNotDependOnDetection() =>
        // DECISIONS.md hash pipeline: the query side enters with an
        // already-rectified 488x680 card.
        AssertHolds(Types().That().Are(Namespace($"{Core}.Identification"))
            .Should().NotDependOnAny(Namespace($"{Core}.Detection"))
            .Because("identification starts from a RectifiedCard, however it was produced"));

    [Fact]
    public void RealImplementations_DoNotDependOnFakes() =>
        AssertHolds(Types().That().Are(Namespaces(
                $"{Core}.Detection", $"{Core}.Identification", $"{Core}.Collection",
                $"{Core}.Export", $"{Core}.Trigger", "LoreFetch.Capture"))
            .Should().NotDependOnAny(Namespace($"{Core}.Fakes"))
            .Because("fakes exist for demo mode and tests, never as a fallback inside a real implementation"));

    [Fact]
    public void Capture_UsesOnlyTheContractTypesFromCore() =>
        // CONTRACTS.md domain map: Capture consumes Abstractions only.
        AssertHolds(Types().That().Are(Namespace("LoreFetch.Capture"))
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^LoreFetch\.Core\.")
                .And().AreNot(Namespace($"{Core}.Abstractions")))
            .Because("the camera adapter produces CameraFrames and nothing more"));

    [Fact]
    public void NothingUsesSystemDrawingOrOpenCvSharpExtensions() =>
        // DECISIONS.md stack table: OpenCvSharp4.Extensions is GDI+ /
        // System.Drawing.Common and throws off-Windows.
        AssertHolds(Types().That().Are(Namespace("LoreFetch"))
            .Should().NotDependOnAny(Namespaces("System.Drawing", "OpenCvSharp.Extensions"))
            .Because("both are Windows-only and would break the portable build"));
}

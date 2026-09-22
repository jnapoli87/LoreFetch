using System.Runtime.InteropServices;

namespace LoreFetch.Lab.RoundTrip;

/// A short, machine-comparable token for "which architecture/OS produced
/// this measurement" -- e.g. `arm64-darwin`, `x64-windows` -- plus a longer
/// human-readable detail string. Exists because `INTER_AREA` is NOT
/// bit-exact across x86-64/ARM64 (CLAUDE.md "The one gate that matters
/// most": OpenCV #24163 confirmed, #22477 closed won't-fix), and that
/// includes the query side's own 32x32 resize (`CardHasher.ToIcon`,
/// `CardHasher`'s own doc comment) -- so ANY number this stream measures
/// (a distance floor, a margin, a query hash) is only meaningful pinned to
/// the machine that produced it, and every one of B2's committed artifacts
/// records this token rather than assuming a reader already knows.
public static class ArchitectureProvenance
{
    /// `"<arch>-<platform>"`, both lowercase -- e.g. `arm64-darwin`,
    /// `x64-windows`. This is the exact form CLAUDE.md's own example uses
    /// (`"measuredOn": "arm64-darwin"`).
    public static string CurrentToken() => $"{ArchitectureToken()}-{PlatformToken()}";

    /// A fuller line for humans: the token plus the .NET runtime
    /// identifier and OS description, so a reader does not have to go
    /// looking elsewhere to confirm exactly what produced a number.
    public static string CurrentDetail() =>
        $"{CurrentToken()} ({RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.OSDescription})";

    private static string ArchitectureToken() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };

    private static string PlatformToken() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "darwin" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";
}

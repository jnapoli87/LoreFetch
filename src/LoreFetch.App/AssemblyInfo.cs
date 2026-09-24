using System.Runtime.CompilerServices;

// Lets Tests/App exercise DemoFrames (internal — it is an
// implementation detail of the Fakes composition path, not a public API)
// directly, for the temp-folder create/delete regression test. This is a
// C# attribute in an ordinary source file, not a .csproj edit.
[assembly: InternalsVisibleTo("LoreFetch.Tests.App")]

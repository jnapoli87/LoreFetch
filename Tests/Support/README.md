# Tests/Support

Shared fixtures for the image-domain test projects (`Tests/Detection`, `Tests/Identification`, `Tests/Lab`). Not a project of its own: each of those `.csproj` files compiles these sources in with `<Compile Include="..\Support\*.cs" />`, so the helpers stay `internal` and no extra assembly (or extra `xunit.v3` reference) is needed.

| File | What it gives a test |
|---|---|
| `SyntheticImages.cs` | Deterministic, code-generated images for the hash pipeline, so no card imagery is ever committed. Keep signatures and RNG seeding stable: the golden hashes depend on them. |
| `DetectorTestFrames.cs` | Code-generated whole scenes (mat plus cards) shaped for the detector |
| `HashFixtures.cs` | `CardHash` and `HashIndexData` values with exactly known bit properties; no images involved |
| `RealCaptureGate.cs` | The skip-or-fail gate for tests that need real data (below) |

## Gated tests

Three kinds of test in `Tests/Detection`, `Tests/Identification` and `Tests/Lab` are gated, each skipping (not failing) by default when what it needs isn't on the machine:

- **Cache-gated tests** (round-trip gate, query-hash witness, accuracy against the real corpus, and anything else that hashes a real Scryfall render) resolve their cache directory via `RoundTripGateTests.ResolveCacheDir()`, in order: (1) `$LOREFETCH_SCRYFALL_CACHE`, when set and non-blank; (2) `~/LoreFetchData/scryfall-cache`, if that directory already exists; (3) on Windows only, `C:\LoreFetchData\scryfall-cache`, if that directory exists — the plan's documented Windows location, found without setting the env var at all; (4) otherwise the home path from step 2, even though it doesn't exist, so a skip message still names a sensible place. Pointing at a directory that doesn't exist (or resolving to one via step 4) makes these tests skip silently rather than fail: a machine whose cache lives somewhere else entirely just runs a smaller suite with no red flag. Set the variable explicitly before trusting a "0 failed" run, or check the skip reason for the path it actually tried.
- **Real-capture tests** additionally need `test-images/` on disk (gitignored, never committed — card imagery, per `docs/DECISIONS.md`): `test-images/ad-hoc/` for individual detector fixtures, `test-images/fixtures/<height>in/<layout>/` plus `test-images/ground-truth.csv` for the accuracy harness against the real 54-slot corpus. Also skips, not fails, when absent.
- **`LOREFETCH_REQUIRE_REAL=1`** flips both of the above from skip to fail (`RealCaptureGate.SkipOrFail`) — set it to prove, on a machine that does have the cache and fixtures, that the real-data tests actually ran rather than silently passed by skipping.

The `WindowsOnly`-traited goldens (above) are filtered for a different reason than these three — an architecture mismatch rather than missing local data — so they stay filtered even on a Windows machine that has never run `LOREFETCH_REQUIRE_REAL`.

# Contracts — DRAFT FOR REVIEW

These are the seams that let four streams run in parallel. **Frozen after the foundation pass.** If a stream needs a change here, it stops and asks — it does not edit this surface unilaterally, because every unilateral change is a four-way merge conflict.

The contract surface is two directories: **`LoreFetch.Core/Abstractions`** (types and interfaces) and **`LoreFetch.Core/Scanning`** (the scan pipeline that composes them). **No OpenCvSharp types appear in any contract** — that's deliberate, so the UI stream never writes CV code, and so the identification stream can swap its internals freely. (`Core` itself does reference OpenCvSharp, because `Core/Imaging` and `FolderFrameSource` need it; the rule is about the seam, not the dependency graph.)

Vocabulary follows [`../CONTEXT.md`](../CONTEXT.md).

> **Status:** architecture review applied 2026-09-21 — ownership gaps closed, open questions resolved (see the end of this file). Stream reviews come next; their proposed changes are reconciled here once, before Stream 0 builds and freezes it.

---

## Design choice worth challenging: `byte[]` frames, not `Mat`, not `IntPtr`

Frames cross the contract as **pooled managed buffers**, not OpenCV `Mat` and not raw pointers.

- **Not `Mat`:** it would drag OpenCvSharp into the UI's own code and couple the UI stream to the CV stream's types.
- **Not `IntPtr`/`Span`:** zero-copy is tempting, but the capture path is a background thread feeding a UI thread, and pointer lifetime across that boundary is the single most likely source of a heisenbug. A pooled `byte[]` makes the lifetime explicit and `Dispose` return it.
- **Cost of the copy:** 1920×1080×3 = 6.2 MB/frame, ~186 MB/s at 30 fps. Against ~20 GB/s of desktop memory bandwidth that's under 1%. The real concern at 30 fps is GC churn, which pooling solves — not the memcpy.

Optimizable later behind the same interface if it ever measures as a problem.

---

## Frames

```csharp
namespace LoreFetch.Core.Abstractions;

public enum PixelLayout { Bgr24, Bgra32 }

/// One frame. Owns a pooled buffer; Dispose returns it to the pool.
/// Not thread-safe: one owner at a time, and the owner disposes.
public sealed class CameraFrame : IDisposable
{
    /// `buffer` may be longer than Stride*Height (pooled arrays round up).
    /// When `pool` is non-null, Dispose returns `buffer` to it exactly once.
    public CameraFrame(byte[] buffer, int width, int height, int stride,
                       PixelLayout layout, DateTimeOffset capturedAt,
                       ArrayPool<byte>? pool);

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }              // may exceed Width*bpp
    public PixelLayout Layout { get; }
    public DateTimeOffset CapturedAt { get; }
    public ReadOnlyMemory<byte> Pixels { get; }   // exactly Stride*Height bytes
    public void Dispose();                  // idempotent; second call is a no-op
}

public readonly record struct FrameGeometry(int Width, int Height, int RotationDegrees);

/// Produces frames: a camera, or a folder of images.
public interface IFrameSource : IAsyncDisposable
{
    /// Human-readable, for logs and the UI status line — what was actually
    /// negotiated, not what was requested.
    /// e.g. "Logitech C920 1920x1080 MJPG @30fps" or "folder: fixtures/10in"
    string Description { get; }
    FrameGeometry Geometry { get; }

    /// Newest-frame-only semantics: implementations MUST drop stale frames
    /// rather than queue them, so a slow consumer sees latency, not a backlog.
    /// (Internally a Channel with capacity 1 + DropOldest.)
    IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct);
}
```

**Why `IAsyncEnumerable`:** the camera yields live frames and the folder source yields images on a timer, through one shape. The consumer disposes each frame as it goes.

**Exactly one consumer:** the scan pipeline (below). Nothing else reads an `IFrameSource` — the UI's preview gets frames *from the pipeline*, not from the source.

**Rotation lives in the source**, not in consumers — `WebcamFrameSource` applies the rotation so everything downstream sees an already-upright frame and nobody has to remember.

---

## Detection and rectification

```csharp
public readonly record struct PointF2(float X, float Y);

/// Corners in frame coordinates, always ordered TL, TR, BR, BL.
public readonly record struct CardQuad(PointF2 TL, PointF2 TR, PointF2 BR, PointF2 BL)
{
    public float AreaPx { get; }
    public float AspectRatio { get; }       // long/short; card is 1.397
}

public interface ICardDetector
{
    /// Returns at most maxCards quads, ordered by descending area.
    /// Returns empty when nothing card-shaped is present — never guesses.
    IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards);
}

/// A perspective-corrected single card at canonical size.
/// Plain managed memory — deliberately NOT pooled and NOT IDisposable.
public sealed class RectifiedCard
{
    public const int CanonicalWidth  = 488;    // matches Scryfall "normal"
    public const int CanonicalHeight = 680;

    public RectifiedCard(byte[] pixels, int stride, PixelLayout layout, CardQuad sourceQuad);

    public int Stride { get; }
    public PixelLayout Layout { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public CardQuad SourceQuad { get; }
}

public interface IRectifier
{
    RectifiedCard Rectify(CameraFrame frame, CardQuad quad);
}
```

**Why 488×680 canonical:** it matches Scryfall's `normal` size, so reference and query sides start from identical geometry. The hash only needs 96 px wide, so warping to 488×680 first is slightly wasteful — but the same buffer is the UI's tile thumbnail, so one canonical size serves both and there's no second warp.

**Why `RectifiedCard` is not pooled:** it is allocated once per capture (≤ 9 × ~1 MB), not 30 times a second, so pooling buys nothing — and an unpooled buffer means a thumbnail can be held for as long as the UI likes with no lifetime rule to break. The pooling argument applies to `CameraFrame` only.

---

## Identification

```csharp
public readonly record struct CardCandidate(
    string OracleId,
    string OracleName,
    int Distance);         // Hamming, 0..1024 — lower is closer

public interface ICardIdentifier
{
    /// Identifies the implementation in logs and the accuracy table.
    /// e.g. "CardSpotterHash/v1", "Stub"
    string Name { get; }

    /// The maxCandidates nearest cards, ranked by ascending Distance.
    /// NEVER filters by threshold — thresholds are applied by the scan
    /// pipeline, in one place. Empty only if the index is empty.
    IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates);
}

public readonly record struct OracleEntry(string OracleId, string OracleName);

/// Every oracle card the identifier can return. Backs the
/// "Set card manually…" type-ahead.
public interface IOracleCatalog
{
    IReadOnlyList<OracleEntry> All { get; }
}
```

**Why a ranked list rather than one answer:** the right-click menu can offer the runners-up, and the accuracy harness needs rank-N to compute the distance margin that calibrates the thresholds. A single-answer contract would block both.

**Why no `Confidence` field:** it would be a second representation of `Distance` through a mapping nobody specified. Distance plus the two thresholds is the whole story.

---

## Cohorts

```csharp
public enum TileState { Included, Excluded, Unresolved, ManuallySet }

public sealed class CohortTile
{
    public RectifiedCard Image { get; }
    public IReadOnlyList<CardCandidate> Candidates { get; }   // ranked, unfiltered

    public OracleEntry? Chosen { get; }      // null only when Unresolved
    public int? ChosenDistance { get; }      // null when ManuallySet or Unresolved
    public TileState State { get; }
    public bool IsLowConfidence { get; }     // highlight only, never gating

    /// Included ⇄ Excluded, and ManuallySet ⇄ Excluded (remembering which).
    /// No-op on Unresolved: there is nothing to commit anyway.
    public void ToggleExcluded();

    /// The user picked the card. State → ManuallySet, ChosenDistance → null.
    public void SetManually(OracleEntry card);

    /// Revert a manual choice to the hash's own proposal: Included if the
    /// best candidate is within the ok threshold, otherwise Unresolved.
    /// No-op unless State is ManuallySet.
    public void Clear();
}

/// The cards from one capture. Not IDisposable — it owns no pooled memory.
public sealed class Cohort
{
    public Guid Id { get; }
    public DateTimeOffset CapturedAt { get; }
    public int ExpectedCount { get; }
    public CaptureReason Reason { get; }
    public IReadOnlyList<CohortTile> Tiles { get; }
}

public enum CaptureReason { Manual, AutoSettle }
```

**State transitions live on the tile, not in the UI.** They are the interaction model, and putting them behind methods means the UI can't invent a fifth state or forget to null the distance on a manual set.

**Initial state, set by the scan pipeline** (the only place thresholds are applied):

| Best candidate's distance | State | `Chosen` | `IsLowConfidence` |
|---|---|---|---|
| ≤ `GoodDistance` | `Included` | best candidate | false |
| ≤ `OkDistance` | `Included` | best candidate | **true** |
| > `OkDistance`, or no candidates | `Unresolved` | null | false |

Default is `Included` — opt-out, per the interaction model.

---

## Capture trigger

```csharp
/// Pure logic over a sequence of detection snapshots. No camera, no images,
/// no clock of its own — the caller passes `now`, so it is fully unit-testable.
public interface IAutoCaptureTrigger
{
    /// True exactly once when the count-gated settle condition is met.
    bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now);

    /// Called after ANY capture, manual included, so the re-arm rule
    /// applies and a static tableau cannot re-fire.
    void NotifyCaptured();

    void Reset();
}
```

The interface is contract; the **implementation lives in `Core/Trigger/**` and is owned by stream A**. The scan pipeline is its only caller, and the pipeline calls `NotifyCaptured()` on *every* capture, manual or auto — so the "manual capture must notify" coupling is honoured in one frozen place rather than remembered by the UI.

---

## Scan pipeline — `Core/Scanning`

The one component that composes source → detector → rectifier → identifier → trigger into cohorts. Written in Stream 0 against the fakes (the end-to-end suite is its test), then frozen.

```csharp
namespace LoreFetch.Core.Scanning;

public sealed record DetectionSnapshot(
    IReadOnlyList<CardQuad> Quads,
    FrameGeometry Geometry,
    DateTimeOffset CapturedAt);

public interface IScanPipeline : IAsyncDisposable
{
    /// Raised on the pipeline's background thread for every frame it processes.
    /// `frame` is valid ONLY for the duration of the callback — copy what you
    /// need (the preview converts BGR→BGRA into its own bitmap anyway) and
    /// never retain the reference. A slow handler costs latency, not memory.
    event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;

    /// Raised when the auto trigger fires and produced a non-empty cohort.
    event Action<Cohort>? AutoCaptured;

    /// Rectifies and identifies the frame the LATEST snapshot came from —
    /// never a newer frame against older quads. Ignores ExpectedCount.
    /// Returns null when the latest snapshot has zero quads.
    Cohort? Capture();

    /// Consumes the frame source until cancelled.
    Task RunAsync(CancellationToken ct);
}
```

**Frame ownership:** the pipeline holds exactly one frame — the one its latest snapshot was detected on — and disposes it when the next one replaces it. That is what lets `Capture()` rectify the exact pixels the overlay showed, and it keeps "one owner, one `Dispose`" true across the thread boundary.

**Detection rate:** every frame the source yields, on the pipeline's thread. The preview throttle (~15 fps) is the UI's concern, applied inside its handler.

**Thresholds:** read from `ScanSettings` at cohort construction, per the table under *Cohorts*. Nothing else in the codebase compares a distance to a threshold.

---

## Collection and export

**Our native format is the source of truth; every adapter projects *down* from it.** It therefore carries everything we have at commit time, not merely what v1's adapters consume — otherwise the SOT becomes the lossy bottleneck.

```csharp
public enum RowSource { Hash, Manual }

public readonly record struct CollectionRow(
    string OracleId,            // Scryfall oracle_id — the identity key
    string OracleName,          // denormalised for human readability; never a key
    int Quantity,
    string? Condition,          // null = not assessed; v1 never assesses
    DateTimeOffset LastScannedAt,
    int? BestMatchDistance,     // null when Source is Manual
    RowSource Source);

public interface ICollectionStore
{
    /// Commits every tile whose State is Included or ManuallySet.
    /// Tile → row: Chosen gives OracleId/OracleName; ManuallySet → Source.Manual
    /// with BestMatchDistance null; Included → Source.Hash with ChosenDistance.
    /// Returns the number of rows inserted or incremented.
    Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct);

    Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct);
}

public sealed record ExportFormat(
    string Id,                 // "native" | "moxfield" | "manabox" | ...
    string DisplayName,
    string FileExtension,
    bool IsVerified,           // has a generated file actually been imported into the live tool?
    string? Notes);            // e.g. "printing columns left blank"

public interface ICollectionExporter
{
    ExportFormat Format { get; }
    Task ExportAsync(IReadOnlyList<CollectionRow> rows, Stream destination, CancellationToken ct);
}
```

**Storage is CSV, not a database** — nothing relational is happening; the old plan's EF Core + SQLite was inherited, never re-justified. Write via temp-file + atomic rename (atomic on NTFS and APFS), keep a `.bak` of the previous write. UTF-8 **with BOM**, or Excel mangles non-ASCII card names. The implementation lives in `Core/Collection/**`, owned by stream D.

**Dedup identity is `OracleId` + `Condition`** → increment `Quantity`. A blank condition is a value like any other, so two unassessed copies of a card are one row with quantity 2. Keying on the name would break the first time Scryfall renames a card.

**Condition is blank in v1.** Nothing in the scan loop can assess it, and a column that always says "NM" would look authoritative and be wrong. Written as an empty CSV field — never the literal `null`. Adapters emit it blank and let the target tool apply its own default.

**`ExportCsvAsync` deliberately is not on the store.** The native format is just one `ICollectionExporter` among several, so the most-used path shares its code and tests with the adapters instead of being a special case. The UI enumerates `IEnumerable<ICollectionExporter>` and holds **no per-format knowledge** — it must not know the word "Moxfield".

`IsVerified` exists because an export adapter that has never been imported into its target tool is a guess. Surfacing that in the picker is honest; hiding it costs user trust on first failure.

**Why the extra SOT columns earn their place:**

| Field | Why it's worth a column |
|---|---|
| `OracleId` | The identity key — survives card renames, and is what a future exporter needs to resolve printings and prices without re-scanning. Free from the hash index. |
| `Source` | Distinguishes rows a human confirmed from rows the machine set. |
| `BestMatchDistance` | Makes "show me everything the machine set at distance > 200" answerable. Since identification is opt-out, that query is the remedy for wrong matches that slipped through. |
| `LastScannedAt` | Audit and dedupe, one field, no cost. |

**Versioning:** the header row's exact column set *is* the format version, documented in the repo. A reader encountering unknown or missing columns must **fail loudly rather than mis-parse** — a silently shifted column is how a collection file quietly becomes wrong.

---

## Settings

```csharp
public sealed class ScanSettings
{
    public int ExpectedCount { get; set; } = 1;        // 1, 3 or 9
    public int SettleMilliseconds { get; set; } = 500;
    public int GoodDistance { get; set; }              // from stream B's thresholds file
    public int OkDistance { get; set; }                // from stream B's thresholds file
    public int CameraRotationDegrees { get; set; } = 90;
    public string? PreferredDeviceId { get; set; }
}
```

`GoodDistance` / `OkDistance` ship as placeholders; stream B replaces them with measured values in a **thresholds file committed next to the hash index**, loaded into `ScanSettings` at startup. The scan pipeline is the only consumer. Nothing in A, C or D may hardcode a distance.

---

## The five fakes — what actually unblocks stream A

Written in the foundation pass, in `Core` so every stream can use them. Owned by Stream 0; streams don't edit them.

| Fake | Behaviour |
|---|---|
| `FolderFrameSource : IFrameSource` | Cycles images from a directory on a timer (decoded via OpenCvSharp). The demo path too, not just a test double. |
| `StubCardDetector : ICardDetector` | Returns N fixed quads laid out for 1/3/9, so overlay rendering is exercisable. |
| `StubRectifier : IRectifier` | Managed-code crop + nearest-neighbour resize of the quad's bounding box to 488×680. Wrong for hashing, fine for thumbnails and wiring. |
| `StubCardIdentifier : ICardIdentifier` | Canned candidates with **configurable distances**, so the UI can reach all four `TileState` values on demand. |
| `StubOracleCatalog : IOracleCatalog` | A few hundred entries, including names hostile to naive code (commas, quotes, accents). |

With these, **stream A never needs anything real from B, C or D** — not at the start, not at the end.

---

## Stream boundaries

| Stream | Owns (exclusive write access) | Consumes | Must not touch |
|---|---|---|---|
| **A — UI** | `LoreFetch.App/**`, `Core/Trigger/**`, `Tests/StreamA/**` | Abstractions, `Core/Scanning`, the five fakes | `Core/Identification`, `Core/Imaging`, `Core/Collection`, `Core/Export`, `Capture` |
| **B — Identification** | `Core/Identification/**`, `Core/Imaging/**`, `LoreFetch.Lab/**`, `Tests/StreamB/**` | Abstractions + the fixture corpus | `App`, `Capture`, `Core/Trigger`, `Core/Collection`, `Core/Export` |
| **C — Capture** | `LoreFetch.Capture/**`, `Tests/StreamC/**` | Abstractions, `ScanSettings` | `App`, everything else in `Core` |
| **D — Collection & export** | `Core/Collection/**`, `Core/Export/**`, `Tests/StreamD/**` | Abstractions | `App`, `Capture`, `Core/Identification`, `Core/Imaging`, `Core/Trigger` |

**Shared and frozen (hook-enforced):** `Core/Abstractions/**`, `Core/Scanning/**`, every `.csproj`, and `LoreFetch.slnx`. The foundation pass creates all projects with **all** package references already in place, so no stream ever edits a project file — that's the main merge-conflict source removed by construction.

**Owned by Stream 0, not edited by streams:** the fakes, `Tests/Integration/**` (the end-to-end suite; real implementations are injected at integration, not before).

**Shared by section:** `README.md` — Stream 0 writes one headed section per stream, and each stream edits only its own. `THIRD-PARTY-NOTICES` — entries are added at integration only; the pre-commit hook's advisory list tolerates the lag.

---

## Resolved in the architecture review

1. **Is `RectifiedCard` the right hand-off unit to `ICardIdentifier`?** **Yes.** One rectification serves both the hash and the thumbnail, and the round-trip gate depends on there being exactly one transform path. Letting the identifier rectify its own way would create a second one.
2. **Should `ICardDetector` take `maxCards` or `ScanSettings`?** **`int maxCards`.** The narrow seam wins, and thresholds now live in the pipeline, so the detector has no reason to see settings.
3. **Does `Cohort` need to own tile disposal?** **No — removed by construction.** `RectifiedCard` is a plain managed buffer and `Cohort` is not `IDisposable`, so there is no lifetime rule for the UI to break. The end-to-end test that reads a thumbnail after commit stays as a regression guard.

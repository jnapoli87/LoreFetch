# Contracts — DRAFT FOR REVIEW

These are the seams that let three streams run in parallel. **Frozen after the foundation pass.** If a stream needs a change here, it stops and asks — it does not edit this surface unilaterally, because every unilateral change is a three-way merge conflict.

Everything lives in `LoreFetch.Core/Abstractions`. **No OpenCvSharp types appear in any contract** — that's deliberate, so the UI stream compiles and runs without touching CV, and so the identification stream can swap its internals freely.

---

## Design choice worth challenging: `byte[]` frames, not `Mat`, not `IntPtr`

Frames cross the contract as **pooled managed buffers**, not OpenCV `Mat` and not raw pointers.

- **Not `Mat`:** it would drag OpenCvSharp into the UI project and couple the UI stream to the CV stream's dependency.
- **Not `IntPtr`/`Span`:** zero-copy is tempting, but the capture path is a background thread handing frames to a UI thread, and pointer lifetime across that boundary is the single most likely source of a heisenbug. A pooled `byte[]` makes the lifetime explicit and `Dispose` return it.
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
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }              // may exceed Width*bpp
    public PixelLayout Layout { get; }
    public DateTimeOffset CapturedAt { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public void Dispose();
}

public readonly record struct FrameGeometry(int Width, int Height, int RotationDegrees);

/// Produces frames: a camera, or a folder of images.
public interface IFrameSource : IAsyncDisposable
{
    /// Human-readable, for logs and the UI status line.
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

**Rotation lives in the source**, not in consumers — `WebcamFrameSource` applies the 90° rotation so everything downstream sees an already-upright frame and nobody has to remember.

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
public sealed class RectifiedCard : IDisposable
{
    public const int CanonicalWidth  = 488;    // matches Scryfall "normal"
    public const int CanonicalHeight = 680;

    public int Stride { get; }
    public PixelLayout Layout { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public CardQuad SourceQuad { get; }
    public void Dispose();
}

public interface IRectifier
{
    RectifiedCard Rectify(CameraFrame frame, CardQuad quad);
}
```

**Why 488×680 canonical:** it matches Scryfall's `normal` size, so reference and query sides start from identical geometry. The hash only needs 96 px wide, so warping to 488×680 first is slightly wasteful — but the same buffer is the UI's tile thumbnail, so one canonical size serves both and there's no second warp.

---

## Identification

```csharp
public readonly record struct CardCandidate(
    string OracleId,
    string OracleName,
    int Distance,          // Hamming, 0..1024
    double Confidence);    // 0..1, derived from Distance against calibrated thresholds

public interface ICardIdentifier
{
    /// Identifies the implementation in logs and the accuracy table.
    /// e.g. "CardSpotterHash/v1", "Stub"
    string Name { get; }

    /// Ranked best-first. Empty when nothing is within the ok threshold.
    IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates);
}
```

**Why a ranked list rather than one answer:** the right-click menu can offer the runners-up, and the accuracy harness needs rank-N to compute the distance margin that calibrates the thresholds. A single-answer contract would block both.

---

## Cohorts

```csharp
public enum TileState { Included, Excluded, Unresolved, ManuallySet }

public sealed class CohortTile
{
    public RectifiedCard Image { get; }
    public IReadOnlyList<CardCandidate> Candidates { get; }
    public CardCandidate? Chosen { get; set; }
    public TileState State { get; set; }
    public bool IsLowConfidence { get; }     // drives highlight only, never gating
}

public sealed class Cohort : IDisposable
{
    public Guid Id { get; }
    public DateTimeOffset CapturedAt { get; }
    public int ExpectedCount { get; }
    public CaptureReason Reason { get; }
    public IReadOnlyList<CohortTile> Tiles { get; }
}

public enum CaptureReason { Manual, AutoSettle }
```

Default state is `Included` — opt-out, per the interaction model. `Unresolved` means no candidate cleared the ok threshold, so there is nothing to include until it's manually set.

---

## Capture trigger

```csharp
/// Pure logic over a sequence of detection snapshots. No camera, no images,
/// no clock of its own — the caller passes `now`, so it is fully unit-testable.
public interface IAutoCaptureTrigger
{
    /// True exactly once when the count-gated settle condition is met.
    bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now);

    /// MUST be called after ANY capture, manual included, so the re-arm rule
    /// applies and a static tableau cannot re-fire.
    void NotifyCaptured();

    void Reset();
}
```

**Manual capture bypasses this entirely** — space ignores `expectedCount` by design — but it still has to call `NotifyCaptured()`, or auto-mode fires immediately afterward on the same static scene. That coupling is the whole reason `NotifyCaptured` is on the interface rather than internal.

---

## Collection and export

**Our native format is the source of truth; every adapter projects *down* from it.** It therefore carries everything we have at commit time, not merely what v1's adapters consume — otherwise the SOT becomes the lossy bottleneck and no future exporter can ever emit more than three columns.

```csharp
public enum RowSource { Hash, Manual }

public readonly record struct CollectionRow(
    string OracleId,            // Scryfall oracle_id — stable key, free from the index
    string OracleName,          // denormalised for human readability
    int Quantity,
    string Condition,
    DateTimeOffset LastScannedAt,
    int? BestMatchDistance,     // null when Source is Manual
    RowSource Source);

public interface ICollectionStore
{
    /// Commits every tile whose State is Included or ManuallySet.
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

**Storage is CSV, not a database.** v1 holds three columns and nothing relational; the old plan's EF Core + SQLite was inherited, never re-justified. Write via temp-file + atomic rename (atomic on NTFS and APFS). Dedup identity is **oracle name + condition** → increment `Quantity`. UTF-8 **with BOM**, or Excel mangles non-ASCII card names.

**`ExportCsvAsync` deliberately is not on the store.** The native format is just one `ICollectionExporter` among several, so the most-used path shares its code and tests with the adapters instead of being a special case. The UI enumerates `IEnumerable<ICollectionExporter>` and holds **no per-format knowledge** — it must not know the word "Moxfield".

`IsVerified` exists because an export adapter that has never been imported into its target tool is a guess. Surfacing that in the picker is honest; hiding it costs user trust on first failure.

**Why the extra SOT columns earn their place:**

| Field | Why it's worth a column |
|---|---|
| `OracleId` | A stable Scryfall key instead of a name string — survives card renames and is what a future exporter needs to resolve printings and prices without re-scanning. Free from the hash index. |
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
    public int GoodDistance { get; set; }              // calibrated by stream B
    public int OkDistance { get; set; }                // calibrated by stream B
    public int CameraRotationDegrees { get; set; } = 90;
    public string? PreferredDeviceId { get; set; }
}
```

`GoodDistance` / `OkDistance` ship as placeholders and are replaced by stream B's measured values. Streams A and C must not hardcode them anywhere.

---

## The three fakes — what actually unblocks stream A

Written in the foundation pass, in `Core` so every stream can use them:

| Fake | Behaviour |
|---|---|
| `FolderFrameSource : IFrameSource` | Cycles images from a directory on a timer. The demo path too, not just a test double. |
| `StubCardDetector : ICardDetector` | Returns N fixed quads laid out for 1/3/9, so overlay rendering is exercisable. |
| `StubCardIdentifier : ICardIdentifier` | Canned candidates with **configurable distances**, so the UI can reach all four `TileState` values on demand. |

With these, **stream A never needs anything real from B or C** — not at the start, not at the end.

---

## Stream boundaries

| Stream | Owns (exclusive write access) | Consumes | Must not touch |
|---|---|---|---|
| **A — UI** | `LoreFetch.App/**` | Abstractions + the three fakes | `Core/Identification`, `Core/Imaging`, `Capture` |
| **B — Identification** | `Core/Identification/**`, `Core/Imaging/**`, `LoreFetch.Lab/**` | Abstractions + the fixture corpus | `App`, `Capture` |
| **C — Capture** | `LoreFetch.Capture/**` | Abstractions | `App`, `Core` |

**Shared and frozen:** `Core/Abstractions/**`, every `.csproj`, and `LoreFetch.slnx`. The foundation pass creates all projects with **all** package references already in place, so no stream ever edits a project file — that's the main merge-conflict source removed by construction.

---

## Open questions on this draft

1. **Is `RectifiedCard` the right hand-off unit to `ICardIdentifier`?** It couples identification to a fixed 488×680. The alternative is passing the raw quad plus frame and letting the identifier rectify however it likes — more freedom for stream B, but then the UI needs its own rectifier for thumbnails and the two can drift. Current choice favours one rectification, shared.
2. **Should `ICardDetector` take `maxCards` or `ScanSettings`?** Passing the whole settings object is more future-proof; passing one int keeps the seam narrow. Currently narrow.
3. **Does `Cohort` need to own tile disposal?** It currently does (`IDisposable`), which means the UI must not hold a tile's `RectifiedCard` past the cohort's lifetime — a real footgun if tiles get bound directly into a long-lived list. Alternative: copy thumbnails to a managed bitmap on cohort creation and let the rectified buffers go immediately. **This is the one I'd most want a second opinion on**, because it's exactly the kind of lifetime bug that only shows up under demo conditions.

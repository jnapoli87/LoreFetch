# Contracts

These are the seams that let four streams run in parallel. **Frozen after the foundation pass.** If a stream needs a change here, it stops and asks — it does not edit this surface unilaterally, because every unilateral change is a four-way merge conflict.

The contract surface is two directories: **`LoreFetch.Core/Abstractions`** (types and interfaces) and **`LoreFetch.Core/Scanning`** (the scan pipeline that composes them). **No OpenCvSharp types appear in any contract** — that's deliberate, so the UI stream never writes CV code, and so the identification stream can swap its internals freely. (`Core` itself does reference OpenCvSharp, because `Core/Imaging` and `FolderFrameSource` need it; the rule is about the seam, not the dependency graph.)

Vocabulary follows [`../CONTEXT.md`](../CONTEXT.md).

> **Status:** architecture review applied 2026-09-21; **four stream reviews reconciled 2026-09-21** — all 20 proposed contract changes ruled on and the accepted ones applied here once. Every decision, with its rationale, is recorded in [`RECONCILIATION.md`](RECONCILIATION.md). This surface is now ready for Stream 0 to build and freeze.

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

/// Geometry of the frame **as delivered downstream**, i.e. post-rotation.
/// `Width`/`Height` always equal the `CameraFrame.Width`/`Height` a consumer
/// sees, so a quad in frame coordinates maps directly onto them.
/// `RotationDegrees` records the rotation the source ALREADY APPLIED — it is
/// informational. Never re-apply it; doing so transposes every quad.
public readonly record struct FrameGeometry(int Width, int Height, int RotationDegrees);

/// Produces frames: a camera, or a folder of images.
public interface IFrameSource : IAsyncDisposable
{
    /// Human-readable, for logs and the UI status line — what was actually
    /// negotiated, not what was requested.
    /// e.g. "Logitech C920 1920x1080 MJPG @30fps" or "folder: fixtures/10in"
    string Description { get; }
    FrameGeometry Geometry { get; }

    /// Newest-frame-only semantics: implementations MUST drop the STALE frame
    /// and keep the newest, so a slow consumer sees latency, not a backlog.
    /// A dropped frame MUST still be disposed — a Channel with capacity 1 and
    /// DropOldest does NOT dispose the item it evicts, so use the
    /// `itemDropped` overload or the pooled buffer leaks on every drop.
    /// Throws FrameSourceException when the device fails or disappears.
    IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct);
}

/// A device failure: unplugged, in use by another application, permission
/// denied, or no frame within the configured timeout. Distinguishable from
/// cancellation (OperationCanceledException) and from ordinary end-of-stream,
/// so callers never have to match on message text.
public sealed class FrameSourceException : Exception
{
    public FrameSourceException(string message, Exception? inner = null);
}

/// Opens a frame source. Construction is async because the negotiated format
/// is only known after the device is opened — which is what `Description` and
/// `Geometry` must report. Without this, both properties would be wrong until
/// the first frame arrives, and a "no such device" failure would surface from
/// inside the enumerator instead of at startup.
public interface IFrameSourceFactory
{
    /// Throws FrameSourceException, listing the enumerated devices, when the
    /// requested device is absent or no usable format can be negotiated.
    Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct);
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
    string OracleId,       // Scryfall oracle_id — the identity key
    string OracleName,
    int Distance,          // Hamming, 0..1024 — lower is closer
    string? ArtworkId);    // Scryfall printing id of the matched ART; null for stubs

public interface ICardIdentifier
{
    /// Identifies the implementation in logs and the accuracy table.
    /// e.g. "CardSpotterHash/v1", "Stub"
    string Name { get; }

    /// The maxCandidates nearest **distinct oracle cards**, ranked by
    /// ascending Distance — best distance per OracleId, never the same card
    /// twice under two of its artworks. A heavily reprinted card otherwise
    /// fills the whole list with its own arts, and the distance margin that
    /// calibrates the thresholds would then measure art-vs-art of one card
    /// instead of a genuine confusion.
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

**Why `ArtworkId`:** `unique_artwork` holds ~55k arts across ~38k oracle ids, so `OracleId` alone cannot say *which reference entry* matched. The round-trip gate must assert that a render retrieves **its own art**; asserting only `OracleId` lets the gate pass while matching a different printing of the same card — precisely the silent failure the gate exists to catch. It is free: the printing id is already in the bulk record. Nullable so the fakes can leave it unset.

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
    /// The tile is constructed with the pipeline's two thresholds so it can
    /// re-apply the pipeline's own numbers here — it never sources them.
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

**`now` must be monotonic.** `DateTimeOffset` is wall-clock and can step backwards on an NTP correction or DST change — a backwards step stalls a 500 ms settle indefinitely, a forward jump satisfies it instantly. The pipeline therefore derives `now` as `startWallClock + stopwatch.Elapsed` rather than calling `DateTimeOffset.UtcNow`. No signature change; the trigger stays a pure function of what it is handed, which is what makes it unit-testable.

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
    /// Raised on the pipeline's background thread, like FrameProcessed — the
    /// UI must marshal to its own thread before touching any control.
    event Action<Cohort>? AutoCaptured;

    /// The frame source failed — device unplugged, taken by another app, or
    /// silent past the watchdog. Raised on the pipeline's background thread
    /// immediately before RunAsync faults, so the UI can render the failure
    /// instead of showing a preview that has quietly stopped updating.
    event Action<FrameSourceException>? SourceFailed;

    /// The frame source's Description, captured once at construction, so the
    /// status line never needs to touch an IFrameSource.
    string SourceDescription { get; }

    /// Rectifies and identifies the frame the LATEST snapshot came from —
    /// never a newer frame against older quads. Ignores ExpectedCount.
    /// Returns null when the latest snapshot has zero quads.
    ///
    /// Safe to call from ANY thread, concurrently with the pipeline's own
    /// loop: it takes ownership of the retained frame under a lock, then does
    /// the rectify-and-identify work outside it. Async because up to nine
    /// identify passes is far too much to run inline on the UI thread — the
    /// UI awaits this from its key handler and stays responsive.
    Task<Cohort?> CaptureAsync(CancellationToken ct);

    /// Consumes the frame source until cancelled.
    Task RunAsync(CancellationToken ct);
}

/// Constructs the pipeline. Owned and written by Stream 0, so the wiring
/// recipe is frozen in one place and the App only calls it.
public static class ScanPipelineFactory
{
    public static IScanPipeline Create(
        IFrameSource source, ICardDetector detector, IRectifier rectifier,
        ICardIdentifier identifier, IAutoCaptureTrigger trigger,
        ScanSettings settings, ILoggerFactory loggers);
}
```

**Frame ownership:** the pipeline holds exactly one frame — the one its latest snapshot was detected on — and disposes it when the next one replaces it. That is what lets `CaptureAsync` rectify the exact pixels the overlay showed, and it keeps "one owner, one `Dispose`" true across the thread boundary.

**The retained frame is guarded by a lock**, because `CaptureAsync` is called from the UI thread while the pipeline thread is replacing and disposing that exact frame. `CaptureAsync` takes the lock, *takes ownership* of the retained frame and snapshot together (leaving the pipeline with none until the next arrives), releases the lock, and only then rectifies, identifies and disposes. Without that, Space can rectify a buffer already returned to the pool — the same class of heisenbug the `byte[]`-over-`IntPtr` decision was made to avoid, one layer up.

**Composition:** the App is the composition root, but it does not know the wiring — it calls `ScanPipelineFactory.Create` and `IFrameSourceFactory.CreateAsync`, both frozen in Stream 0's code, and calls `RunAsync` once at startup. **Stream 0 also loads stream B's thresholds file into `ScanSettings`**, so stream A never implements a file format that stream B defines; stream A only renders the failure when the file is missing.

**Detection rate:** every frame the source yields, on the pipeline's thread. The preview throttle (~15 fps) is the UI's concern, applied inside its handler.

**Thresholds:** read from `ScanSettings` at cohort construction, per the table under *Cohorts*. **Thresholds originate only in `ScanSettings` and are applied only by the pipeline and by the `CohortTile` instances it constructs** — the pipeline hands each tile both values so `Clear()` can re-apply them. Nothing else in the codebase sources or compares a distance threshold; in particular nothing in `App`, `Capture`, `Identification` or `Collection` may hardcode one.

---

## Collection and export

**Our native format is the source of truth; every adapter projects *down* from it.** It therefore carries everything we have at commit time, not merely what v1's adapters consume — otherwise the SOT becomes the lossy bottleneck.

```csharp
public enum RowSource { Hash, Manual }

public readonly record struct CollectionRow(
    string OracleId,            // Scryfall oracle_id — the identity key
    string OracleName,          // denormalised for human readability; never a key
    int Quantity,
    string? Condition,          // null = not assessed; v1 never assesses.
                                // null is the ONLY representation of
                                // "unassessed": null serialises to a blank
                                // field, and a blank field parses back to
                                // null — never "". Condition is half the
                                // dedup key, so "" vs null would silently
                                // split one card into two rows.
    DateTimeOffset LastScannedAt,
    int? BestMatchDistance,     // null when Source is Manual
    RowSource Source);

public interface ICollectionStore
{
    /// Commits every tile whose State is Included or ManuallySet.
    /// Tile → row: Chosen gives OracleId/OracleName; ManuallySet → Source.Manual
    /// with BestMatchDistance null; Included → Source.Hash with ChosenDistance.
    /// Returns the number of CARDS committed — the sum of the quantity
    /// increments, not the number of rows touched. Committing nine basic
    /// lands returns 9, which is the number a UI shows the user; the rows
    /// affected would be 1 and would read as a bug.
    /// Duplicate tiles within one cohort are folded before writing.
    /// Throws CollectionStoreException when the file cannot be replaced —
    /// the caller keeps its cohort and can retry.
    Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct);

    Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct);
}

/// The collection file could not be read or replaced — most often because
/// Excel holds it open on Windows. Recoverable by construction: the caller
/// keeps the pending cohort intact and retries after the user closes the file.
public sealed class CollectionStoreException : Exception
{
    public CollectionStoreException(string message, Exception? inner = null);
}

public sealed record ExportFormat(
    string Id,                 // v1: "native" | "moxfield"  (see below)
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

**Storage is CSV, not a database** — nothing relational is happening; the old plan's EF Core + SQLite was inherited, never re-justified. The implementation lives in `Core/Collection/**`, owned by stream D.

**The write sequence is exact, and two details are load-bearing:**

1. **The temp file must be created in the same directory as `collection.csv`** — not `Path.GetTempPath()`. .NET always passes `MOVEFILE_COPY_ALLOWED`, so a cross-volume `File.Move` silently degrades to copy-then-delete, and Unix takes the same non-atomic fallback on `EXDEV`. A temp file in `%TEMP%` quietly removes the entire atomicity guarantee.
2. **`Flush(true)` before disposing the temp stream, then rename.** `using`/`Dispose` flushes to the OS, not to disk. `File.Move(temp, target, overwrite: true)` is the primitive; `File.Replace` is worse here because it cannot create the target on a first write.

Atomic rename buys exactly one thing: **a reader never sees a truncated file.** It is documented atomic on APFS (POSIX/Apple `rename(2)`) and *undocumented either way* on NTFS — Microsoft never uses the word. Durability past power loss is not promised by any of them, which is what the `.bak` of the previous write covers.

**BOM is a per-format decision, not a global one.** The **native** format is UTF-8 **with BOM**, because it is the file users open in Excel and Excel mangles non-ASCII card names without it. Adapters choose for themselves: a BOM that fixes Excel can break an importer's header match. The Moxfield adapter writes **no BOM**, and D4's real import verifies that.

**Duplicate keys on read are merged, not rejected.** Two rows with the same `OracleId` + `Condition` are folded — sum `Quantity`, keep the latest `LastScannedAt` — and the merge is logged. This is a deliberate exception to the fail-loudly rule below, which governs *columns*: hand-editing in Excel is an advertised feature, so a pasted or re-sorted row must not lock a user out of their own collection.

**Oracle names are written verbatim, never sanitised.** `+2 Mace` is a real in-scope card and the only oracle name beginning with a character Excel treats as a formula. Excel will try to evaluate that cell; that is documented in the README as a display artefact. Prefixing a `'` or a tab would corrupt the source of truth for every machine reader in order to fix one program's rendering.

**Dedup identity is `OracleId` + `Condition`** → increment `Quantity`. A blank condition is a value like any other, so two unassessed copies of a card are one row with quantity 2. Keying on the name would break the first time Scryfall renames a card.

**Condition is blank in v1.** Nothing in the scan loop can assess it, and a column that always says "NM" would look authoritative and be wrong. Written as an empty CSV field — never the literal `null`. Adapters emit it blank and let the target tool apply its own default.

**`ExportCsvAsync` deliberately is not on the store.** The native format is just one `ICollectionExporter` among several, so the most-used path shares its code and tests with the adapters instead of being a special case. The UI enumerates `IEnumerable<ICollectionExporter>` and holds **no per-format knowledge** — it must not know the word "Moxfield".

`IsVerified` exists because an export adapter that has never been imported into its target tool is a guess. Surfacing that in the picker is honest; hiding it costs user trust on first failure.

**v1 ships exactly two formats: the native SOT and one third-party adapter, Moxfield.** Moxfield is the one researched tool that provably accepts name-only rows — only `Name` is required and column order is explicitly irrelevant. The others were checked and rejected on evidence, and the README says so rather than leaving users to discover it:

| Tool | Why not in v1 |
|---|---|
| **ManaBox** | Its own docs set a hard minimum of card name **plus** set name/code, or a Scryfall ID — so it cannot accept the name-only rows this project produces. Its `Scryfall ID` column is a *printing* id, not `oracle_id`, so writing ours there would look precise and resolve wrongly. |
| **Archidekt** | Blocks name-only uploads, and has no fixed import header by design. |
| **Deckbox** | Does accept name-only rows, but its column spec is community folklore rather than first-party documentation, and there is a credible report of a BOM breaking its header match. The strongest candidate if a second adapter is ever added. |
| **Dragon Shield** | No first-party import documentation exists at all. |

One verified adapter beats four guessed ones, and `IsVerified` would have to be `false` on every one of them.

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

    /// Auto-capture is OFF on a first run: it is the surprising mode, and
    /// without a flag here the UI has no way to expose the toggle at all.
    public bool AutoCaptureEnabled { get; set; } = false;

    /// The settle condition's epsilon: movement beyond this many pixels
    /// between snapshots resets the settle timer. 4 px is ~0.03" at the
    /// settled ~9.75" height (~139 px/inch) — above contour jitter, far
    /// below hand movement.
    public int MovementTolerancePixels { get; set; } = 4;

    /// Device-failure detection. Device-in-use, permission-denied and unplug
    /// are NOT exceptions from the capture backend — all three present as
    /// frames that simply never arrive, so these two timeouts are the only
    /// mechanism that turns silence into a FrameSourceException.
    /// The first-frame budget is generous because Media Foundation has been
    /// measured at 5.7 s to first frame at 1080p.
    public int FirstFrameTimeoutMs { get; set; } = 10_000;
    public int FrameWatchdogMs { get; set; } = 2_000;

    /// 0, 90, 180 or 270 only — the setter throws ArgumentOutOfRangeException
    /// on anything else, because nothing else is implementable.
    public int CameraRotationDegrees { get; set; } = 90;

    /// The capture backend's own opaque device identity, prefixed with the
    /// backend that produced it, e.g. "dshow:\\?\usb#vid_046d...".
    /// The prefix matters: Windows enumeration concatenates three backends,
    /// so one C920 yields up to three descriptors, and an unprefixed id
    /// silently stops matching if the preference order ever changes.
    /// Null means "first usable device".
    public string? PreferredDeviceId { get; set; }
}
```

**Backend preference is fixed, not discovered: DirectShow first, Media Foundation as fallback, Video for Windows ignored.** DirectShow is what the latency evidence covers (1.44 s to first frame vs 5.71 s); Video for Windows is a legacy path that misreports modern modes.

`GoodDistance` / `OkDistance` ship as placeholders; stream B replaces them with measured values in a **thresholds file committed next to the hash index**, loaded into `ScanSettings` at startup. The scan pipeline is the only consumer. Nothing in A, C or D may hardcode a distance.

---

## Logging

**`Microsoft.Extensions.Logging.Abstractions` (MIT) is the logging seam.** Referenced by `Core`, `Capture` and `App`; `ILoggerFactory` is passed into `ScanPipelineFactory.Create` and `IFrameSourceFactory.CreateAsync`.

There was no logger anywhere on the contract surface, yet three of stream C's *done-when* criteria are phrased as "the log shows…" — the negotiated camera format, the device characteristic list, and per-frame decode time. Without a seam the only option is `Console.WriteLine`, and diagnostic output described as "worth keeping permanently" has nowhere to go.

Chosen over a hand-rolled interface because it is the .NET standard, costs one tiny reference, and needs no adapter for whatever sink is wired later. It is a package reference, so it **must** be pinned before the fork.

---

## The seven fakes — what actually unblocks stream A

Written in the foundation pass, in `Core` so every stream can use them. Owned by Stream 0; streams don't edit them.

| Fake | Behaviour |
|---|---|
| `FolderFrameSource : IFrameSource` | Cycles images from a directory on a timer (decoded via OpenCvSharp). The demo path too, not just a test double. |
| `StubCardDetector : ICardDetector` | Returns N fixed quads laid out for 1/3/9, so overlay rendering is exercisable. |
| `StubRectifier : IRectifier` | Managed-code crop + nearest-neighbour resize of the quad's bounding box to 488×680. Wrong for hashing, fine for thumbnails and wiring. |
| `StubCardIdentifier : ICardIdentifier` | Canned candidates with **configurable distances**, so the UI can reach all four `TileState` values on demand. |
| `StubOracleCatalog : IOracleCatalog` | **Configurable entry count, defaulting to ~33,000** synthetic names, because `AutoCompleteBox`'s defaults are actively hostile at that size and a few hundred entries cannot reproduce the only performance problem the type-ahead has. Always includes the hostile handful: names with commas, embedded double quotes, a **leading** double quote, accents, and `+2 Mace`. |
| `StubCollectionStore : ICollectionStore` | In-memory rows with the real dedup and commit semantics. Can be told to throw `CollectionStoreException` on the next commit, so the Excel-lock path is renderable. |
| `StubCollectionExporter : ICollectionExporter` | Configurable `ExportFormat`, so stream A can register one verified and one **unverified** instance and exercise the `IsVerified` badge. Writes a trivial file. |

With these, **stream A never needs anything real from B, C or D** — not at the start, not at the end. The last two were added in reconciliation: stream A's collection view, its empty state and its export picker are all built against `ICollectionStore` and `ICollectionExporter`, which belong to stream D, so without fakes that promise was simply false.

---

## Stream boundaries

| Stream | Owns (exclusive write access) | Consumes | Must not touch |
|---|---|---|---|
| **A — UI** | `LoreFetch.App/**`, `Core/Trigger/**`, `Tests/StreamA/**` | Abstractions, `Core/Scanning` (incl. `ScanPipelineFactory`), the seven fakes | `Core/Identification`, `Core/Imaging`, `Core/Collection`, `Core/Export`, `Capture` |
| **B — Identification** | `Core/Identification/**`, `Core/Imaging/**`, `LoreFetch.Lab/**`, `Tests/StreamB/**` | Abstractions + the fixture corpus | `App`, `Capture`, `Core/Trigger`, `Core/Collection`, `Core/Export` |
| **C — Capture** | `LoreFetch.Capture/**`, `Tests/StreamC/**` | Abstractions (incl. `IFrameSourceFactory`, `FrameSourceException`), `ScanSettings` | `App`, everything else in `Core` |
| **D — Collection & export** | `Core/Collection/**`, `Core/Export/**`, `Tests/StreamD/**` | Abstractions — specifically `CollectionRow`, `ICollectionStore`, `ICollectionExporter`, `ExportFormat`, `Cohort`, `CohortTile`, **`OracleEntry`**, **`TileState`**, **`RowSource`**, `CollectionStoreException` | `App`, `Capture`, `Core/Identification`, `Core/Imaging`, `Core/Trigger` |

**Shared and frozen (hook-enforced):** `Core/Abstractions/**`, `Core/Scanning/**`, every `.csproj`, and `LoreFetch.slnx`. The foundation pass creates all projects with **all** package references already in place, so no stream ever edits a project file — that's the main merge-conflict source removed by construction.

**Owned by Stream 0, not edited by streams:** the fakes, `Tests/Integration/**` (the end-to-end suite; real implementations are injected at integration, not before).

**Shared by section:** `README.md` — Stream 0 writes one headed section per stream, and each stream edits only its own. `THIRD-PARTY-NOTICES` — entries are added at integration only; the pre-commit hook's advisory list tolerates the lag.

---

## Resolved in the architecture review

1. **Is `RectifiedCard` the right hand-off unit to `ICardIdentifier`?** **Yes.** One rectification serves both the hash and the thumbnail, and the round-trip gate depends on there being exactly one transform path. Letting the identifier rectify its own way would create a second one.
2. **Should `ICardDetector` take `maxCards` or `ScanSettings`?** **`int maxCards`.** The narrow seam wins, and thresholds now live in the pipeline, so the detector has no reason to see settings.
3. **Does `Cohort` need to own tile disposal?** **No — removed by construction.** `RectifiedCard` is a plain managed buffer and `Cohort` is not `IDisposable`, so there is no lifetime rule for the UI to break. The end-to-end test that reads a thumbnail after commit stays as a regression guard.

---

## Resolved in the stream reviews

Four parallel stream reviews raised **20 proposed contract changes and 20 open questions**; all 40 were ruled on in the reconciliation pass of 2026-09-21 and the accepted changes are applied above. The decision record — every ruling, its rationale, and what was rejected — is [`RECONCILIATION.md`](RECONCILIATION.md).

Three things the reviews changed that are *not* visible in this file, because they live in [`../CLAUDE.md`](../CLAUDE.md):

1. The reference and query transforms are **deliberately asymmetric**, not identical, and the round-trip gate asserts a recorded stable distance floor rather than ≈ 0.
2. The "measured ceiling is 60 fps @ 1080p" preview figure had no primary source and has been removed.
3. The macOS rationale was stale — FlashCap has had an AVFoundation backend since 1.11.0. The shipping decision is unchanged and better supported than before.

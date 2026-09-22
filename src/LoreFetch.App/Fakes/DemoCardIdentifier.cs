using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;

namespace LoreFetch.App.Fakes;

/// <summary>
/// Wraps <see cref="StubCardIdentifier"/> (Core/Fakes, frozen — wrapped
/// rather than edited) so the demo composition can show every
/// hash-reachable <c>CohortTile</c> state — confident <c>Included</c>,
/// low-confidence <c>Included</c>, and <c>Unresolved</c> — within ONE
/// cohort, by cycling the distance it hands back once per call.
/// </summary>
/// <remarks>
/// <para>
/// Every distance here is derived ARITHMETICALLY from
/// <see cref="ScanSettings.GoodDistance"/>/<see cref="ScanSettings.OkDistance"/>
/// at call time — this class holds no distance literal of its own, per plan
/// item I4 ("no distance literal outside Core/Scanning, CohortTile and
/// thresholds files"). <see cref="DemoThresholds"/> is the one sanctioned
/// App-side exception, and even it only supplies the good/ok pair, not the
/// three per-state distances computed here.
/// </para>
/// <para>
/// <b>Concurrency.</b> <c>ScanPipeline.TryCaptureFromRetained</c> takes
/// ownership of the retained frame under a lock, but runs the actual
/// rectify-then-identify loop OUTSIDE that lock — so two overlapping
/// captures (an auto-settle fire on the pipeline's loop thread racing a
/// manual <c>CaptureAsync</c> on a thread-pool thread) CAN each be mid-loop
/// at once, and both call <see cref="ICardIdentifier.Identify"/> on this
/// same shared instance. Calls WITHIN one cohort are always sequential (a
/// plain <c>foreach</c> in <c>TryCaptureFromRetained</c>), but calls ACROSS
/// two overlapping cohorts are not. <see cref="StubCardIdentifier"/> is
/// itself not thread-safe for concurrent callers (<c>NextDistances</c> is a
/// plain mutable field <c>Identify</c> reads internally, and its call
/// counter increments without synchronization), so this wrapper takes its
/// own lock around the whole "compute distance, set NextDistances, delegate"
/// sequence, making each logical call atomic with respect to the others.
/// That guarantees correctness (thread B's distance can never leak into
/// thread A's candidate) unconditionally; it only relaxes the "tile k of
/// THIS cohort is state k mod 3" cycling guarantee under the rare case of
/// two interleaved concurrent captures, which is a demo-quality cosmetic
/// concern, not a correctness one — the mod-3 cycle still produces a mix of
/// all three states either way.
/// </para>
/// <para>
/// <b>Per-capture distinct oracle ids: skipped, not cheap.</b>
/// <see cref="StubCardIdentifier"/> names each returned candidate
/// <c>"stub-oracle-{i}"</c> where <c>i</c> is that candidate's position
/// WITHIN its own call's already-sorted, already-ascending result list —
/// never anything derived from the call index. Since this wrapper always
/// wants its chosen distance to be the (only) candidate, that candidate is
/// always at position 0, so every tile this wrapper drives is
/// <c>OracleId = "stub-oracle-0"</c> regardless of which capture produced
/// it. Making candidates from different captures carry different oracle ids
/// would require either editing <see cref="StubCardIdentifier"/> (Core/Fakes
/// is frozen) or padding every call with extra lower-ranked filler
/// candidates purely to shift the wanted one off position 0 — which would
/// then no longer be the tile's proposed match. Neither is cheap, so this is
/// deliberately not attempted; the collection view will show scanned demo
/// cards collapsing into one row by design, not by oversight.
/// </para>
/// </remarks>
public sealed class DemoCardIdentifier : ICardIdentifier
{
    private readonly StubCardIdentifier _inner;
    private readonly ScanSettings _settings;
    private readonly object _gate = new();

    private int _callIndex = -1;

    public DemoCardIdentifier(StubCardIdentifier inner, ScanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(settings);
        _inner = inner;
        _settings = settings;
    }

    public string Name => _inner.Name;

    public IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates)
    {
        lock (_gate)
        {
            var good = _settings.GoodDistance;
            var ok = _settings.OkDistance;

            _callIndex++;
            var distance = (_callIndex % 3) switch
            {
                0 => good / 2,          // confident: well inside "good" -> Included, not low-confidence
                1 => (good + ok) / 2,   // low-confidence: strictly between good and ok -> Included, IsLowConfidence
                _ => ok + (ok - good),  // unresolved: as far past "ok" as "ok" is past "good" -> Unresolved
            };

            _inner.NextDistances = [distance];
            return _inner.Identify(card, maxCandidates);
        }
    }
}

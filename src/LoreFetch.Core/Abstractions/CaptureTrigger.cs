namespace LoreFetch.Core.Abstractions;

/// Pure logic over a sequence of detection snapshots. No camera, no images,
/// no clock of its own — the caller passes `now`, so it is fully unit-testable.
///
/// This is contract only. The implementation lives in `Core/Trigger/**` and
/// is owned by stream A; the scan pipeline is its only caller.
public interface IAutoCaptureTrigger
{
    /// True exactly once when the count-gated settle condition is met.
    bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now);

    /// Called after ANY capture, manual included, so the re-arm rule
    /// applies and a static tableau cannot re-fire.
    void NotifyCaptured();

    void Reset();
}

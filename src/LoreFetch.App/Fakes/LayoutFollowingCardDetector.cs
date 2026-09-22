using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;

namespace LoreFetch.App.Fakes;

/// <summary>
/// Wraps <see cref="StubCardDetector"/> (Core/Fakes, frozen — wrapped rather
/// than edited) so every <see cref="Detect"/> call reflects the CURRENT
/// <see cref="ScanSettings.ExpectedCount"/>, instead of whatever
/// <see cref="StubCardDetector.CardCount"/> composition happened to set at
/// startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppComposition"/> previously constructed the stub once with
/// <c>new StubCardDetector(settings.ExpectedCount)</c> and never touched
/// <see cref="StubCardDetector.CardCount"/> again — so flipping the 1/3/9
/// selector at runtime changed <see cref="ScanSettings.ExpectedCount"/> (the
/// trigger's own count-gate reads it fine) but never changed what the NEXT
/// detected frame actually contained, since nothing re-read the setting into
/// the detector. This wrapper closes that gap: it re-reads
/// <see cref="ScanSettings.ExpectedCount"/> on every call and pushes it into
/// the wrapped stub's mutable <see cref="StubCardDetector.CardCount"/>
/// immediately before delegating.
/// </para>
/// <para>
/// Thread safety: <see cref="LoreFetch.Core.Scanning.ScanPipeline"/> calls
/// <see cref="ICardDetector.Detect"/> from exactly one place —
/// <c>ProcessFrame</c>, on its own single background loop thread.
/// <c>CaptureAsync</c> (callable concurrently, from any thread) never calls
/// the detector at all — it rectifies and identifies against the quads a
/// prior <c>ProcessFrame</c> already retained
/// (<c>TryCaptureFromRetained</c>). So <see cref="Detect"/> is never called
/// concurrently with itself, and the read-then-write here needs no lock.
/// </para>
/// </remarks>
public sealed class LayoutFollowingCardDetector : ICardDetector
{
    private readonly StubCardDetector _inner;
    private readonly ScanSettings _settings;

    public LayoutFollowingCardDetector(StubCardDetector inner, ScanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(settings);
        _inner = inner;
        _settings = settings;
    }

    public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards)
    {
        _inner.CardCount = _settings.ExpectedCount;
        return _inner.Detect(frame, maxCards);
    }
}

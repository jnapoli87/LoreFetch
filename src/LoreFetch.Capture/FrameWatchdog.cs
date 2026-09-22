using System.Runtime.CompilerServices;
using LoreFetch.Core.Abstractions;

namespace LoreFetch.Capture;

/// Turns silence into a `FrameSourceException`. Per docs/stream-c-capture.md
/// C5: FlashCap has no way to detect a device already in use — issue #15 is
/// open, labelled "help wanted" and "suspended" — so an unplugged camera, a
/// device already in use by another application, and a denied camera
/// permission all present identically: as frames that simply never arrive.
/// There is nothing to `catch`, because none of the three is an exception.
///
/// This wraps any frame sequence with two independent budgets: a generous
/// one-time `firstFrameTimeout` before the very first item (Media
/// Foundation alone has been measured at 5.7 s to first frame at 1080p),
/// and a tighter recurring `frameTimeout` between every item after that.
/// Both are read from `ScanSettings` (`FirstFrameTimeoutMs` /
/// `FrameWatchdogMs`), never hardcoded here.
internal sealed class FrameWatchdog
{
    private readonly TimeSpan _firstFrameTimeout;
    private readonly TimeSpan _frameTimeout;
    private readonly TimeProvider _timeProvider;

    /// `timeProvider` defaults to `TimeProvider.System`. Tests inject
    /// millisecond-scale real timeouts rather than a fake time source —
    /// this project has no reference to Microsoft.Extensions.TimeProvider.
    /// Testing (and the frozen `.csproj` surface means one can't be added
    /// for this package), and a hand-rolled fake capable of driving
    /// `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` would need
    /// to reimplement that overload's own timer-firing semantics to be
    /// trustworthy. Short bounded real waits are simpler and just as
    /// deterministic in outcome: a 50 ms timeout against a stream that
    /// never advances always elapses, and a healthy stream paced well
    /// inside its own timeout always outruns it.
    internal FrameWatchdog(TimeSpan firstFrameTimeout, TimeSpan frameTimeout, TimeProvider? timeProvider = null)
    {
        _firstFrameTimeout = firstFrameTimeout;
        _frameTimeout = frameTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// Re-yields every item from `source`, racing each arrival against the
    /// applicable timeout. Throws `FrameSourceException` — never
    /// `TimeoutException` — naming the three candidate causes, because
    /// nothing at this layer can tell them apart (stream-c-capture.md C5:
    /// "Unplugged camera, device already in use by another app, permission
    /// denied.").
    ///
    /// The timeout resets on *every* item, not just the first: a healthy
    /// stream running for an hour at a steady pace must never trip this
    /// just because its total runtime exceeds `frameTimeout` — only a gap
    /// between two consecutive items may.
    internal async IAsyncEnumerable<T> Watch<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct)
    {
        var isFirst = true;
        var enumerator = source.GetAsyncEnumerator(ct);

        // Deliberately not `await using`: an async-iterator-backed
        // `IAsyncEnumerator` throws `NotSupportedException` from
        // `DisposeAsync` if it's called while a `MoveNextAsync` on the same
        // instance is still outstanding — and on the timeout path below,
        // that's exactly the situation, because we abandon the race's loser
        // rather than waiting for it. Disposing eagerly there would replace
        // the `FrameSourceException` we're trying to throw with that
        // `NotSupportedException` instead. `pendingMoveNext` tracks whether
        // a `MoveNextAsync` is still in flight when we leave the loop, so
        // the `finally` below can choose the disposal path that's actually
        // legal.
        Task<bool>? pendingMoveNext = null;
        try
        {
            while (true)
            {
                var timeout = isFirst ? _firstFrameTimeout : _frameTimeout;
                var moveNext = pendingMoveNext ?? enumerator.MoveNextAsync().AsTask();
                pendingMoveNext = moveNext;
                var delay = Task.Delay(timeout, _timeProvider, ct);

                var winner = await Task.WhenAny(moveNext, delay).ConfigureAwait(false);
                if (winner == delay)
                {
                    // A genuine timeout completes `delay` normally; the
                    // caller's own cancellation completes it as Canceled —
                    // awaiting it here re-throws in the cancellation case
                    // instead of misreporting an ordinary shutdown as a
                    // device failure. `pendingMoveNext` stays set, so the
                    // `finally` below knows not to dispose synchronously.
                    await delay.ConfigureAwait(false);

                    var reason = isFirst
                        ? $"No frame arrived within {timeout.TotalMilliseconds:F0} ms of opening the capture device."
                        : $"No frame arrived for {timeout.TotalMilliseconds:F0} ms after frames were flowing.";
                    throw new FrameSourceException(
                        reason + " FlashCap cannot tell these apart: the device may be unplugged, already in use by " +
                        "another application, or camera access may be denied by a permission or privacy setting.");
                }

                // moveNext just won the race, so it's already complete —
                // safe to dispose the enumerator from here on if we leave
                // the loop.
                pendingMoveNext = null;

                if (!await moveNext.ConfigureAwait(false))
                {
                    yield break;
                }

                isFirst = false;
                yield return enumerator.Current;
            }
        }
        finally
        {
            if (pendingMoveNext is null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                // The abandoned `MoveNextAsync` will only ever complete
                // once `ct` is cancelled (the caller's reaction to the
                // `FrameSourceException` we just threw) or the underlying
                // source itself faults — never on its own, since the whole
                // point of this branch is that nothing arrived. Detach:
                // wait for it off to the side and dispose once it settles,
                // rather than blocking this throw on a wait that may
                // outlive the caller's interest in this stream entirely.
                DisposeAfterPendingMoveNextCompletes(enumerator, pendingMoveNext);
            }
        }
    }

    /// Fire-and-forget cleanup for the one case where the enumerator can't
    /// be disposed inline: a `MoveNextAsync` from the timed-out iteration
    /// is still running. Exceptions from either await are expected (the
    /// pending call typically completes as `OperationCanceledException`,
    /// and a `Faulted`/already-failed enumerator can throw again from
    /// `DisposeAsync`) and deliberately swallowed — this path exists only
    /// to release the device handle, not to report anything, and the
    /// caller already has the `FrameSourceException` that matters.
    private static async void DisposeAfterPendingMoveNextCompletes<T>(IAsyncEnumerator<T> enumerator, Task<bool> pendingMoveNext)
    {
        try
        {
            await pendingMoveNext.ConfigureAwait(false);
        }
        catch
        {
            // Expected: cancellation, or the source's own fault surfacing late.
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort release; nothing left to report it to.
        }
    }
}

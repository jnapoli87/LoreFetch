using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;

namespace LoreFetch.App;

/// One frame's worth of metadata, published alongside its bytes by
/// `FrameHandoff`. `Quads` is the snapshot the frame was detected against —
/// see `DetectionSnapshot` (CONTRACTS.md).
internal readonly record struct FrameHandoffMetadata(
    int Width,
    int Height,
    int Stride,
    PixelLayout Layout,
    IReadOnlyList<CardQuad> Quads,
    long Sequence);

/// Fixes the A2 data race: `OnFrameProcessed` (background thread) used to
/// copy into a single `_stagingBuffer` under a lock, and `OnRenderFrame`
/// (UI thread) took a REFERENCE to that same array under the lock, then
/// converted it after releasing the lock — so the producer could overwrite
/// the buffer mid-conversion and render a torn frame (top of one frame,
/// bottom of the next).
///
/// This is a three-slot rotation instead. The producer always writes into
/// its own "back" slot; publishing a frame is just a pointer swap done
/// under `_lock`, never a byte copy. The three invariants that make a torn
/// read impossible:
///
///   1. `_back`, `_ready` and `_taken` are always pairwise distinct
///      whenever more than one is set (three slots, at most two occupied by
///      "ready" + "taken" at any moment, so a free third always exists for
///      "back").
///   2. Only the producer thread ever touches the `_back` slot's bytes, and
///      only between one `Publish` call and the next — so writing into it
///      needs no lock.
///   3. The producer never picks a slot the consumer currently holds
///      (`_taken`) as its next `_back` — see `PickFreeSlot`.
///
/// `Publish` copies into the back slot BEFORE taking the lock (the byte
/// copy, not the pointer swap, is the expensive part, and nothing else
/// touches that slot while it does); the lock only guards the O(1) index
/// bookkeeping. `TryTake`/`Return` are the consumer's side of the same
/// bookkeeping. No per-frame allocation: each of the three backing arrays
/// is reallocated only when a frame needs a bigger one than it already has.
///
/// Coalescing falls out for free: if the producer publishes twice before
/// the consumer calls `TryTake`, the second `Publish` overwrites `_ready`
/// (the first, unconsumed frame is simply skipped) — there is never more
/// than one frame waiting, and `Sequence` only ever increases across the
/// frames a consumer actually observes.
internal sealed class FrameHandoff
{
    private readonly object _lock = new();
    private readonly byte[]?[] _slots = new byte[3][];
    private readonly FrameHandoffMetadata[] _meta = new FrameHandoffMetadata[3];

    private int _back;
    private int _ready = -1;
    private int _taken = -1;

    /// Producer side. Copies `data` into this instance's own back buffer
    /// (resizing it only if it is too small — never per frame at a steady
    /// size) and then, under `_lock`, publishes it as the newest ready
    /// frame. Safe to call from exactly one producer thread; `FrameHandoff`
    /// does not support multiple concurrent producers.
    public void Publish(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        int stride,
        PixelLayout layout,
        IReadOnlyList<CardQuad> quads,
        long sequence)
    {
        var slot = _back;

        var buffer = _slots[slot];
        if (buffer is null || buffer.Length < data.Length)
        {
            buffer = new byte[data.Length];
            _slots[slot] = buffer;
        }

        data.CopyTo(buffer);
        _meta[slot] = new FrameHandoffMetadata(width, height, stride, layout, quads, sequence);

        lock (_lock)
        {
            _back = PickFreeSlot(slot, _taken);
            _ready = slot;
        }
    }

    /// Consumer side. Takes the newest published frame, if any, and marks
    /// its slot "taken" so the producer will never write into it — the
    /// consumer must call `Return()` once it is done reading `buffer` so
    /// that slot becomes available for the producer to reuse. Returns
    /// `false` (no allocation, `buffer` default) when nothing new has been
    /// published since the last `TryTake`.
    public bool TryTake(out ReadOnlyMemory<byte> buffer, out FrameHandoffMetadata metadata)
    {
        lock (_lock)
        {
            if (_ready == -1)
            {
                buffer = default;
                metadata = default;
                return false;
            }

            var slot = _ready;
            _ready = -1;
            _taken = slot;
            buffer = _slots[slot];
            metadata = _meta[slot];
            return true;
        }
    }

    /// Consumer side. Releases the slot most recently returned by
    /// `TryTake`, allowing the producer to pick it as a future back buffer.
    /// A no-op if nothing is currently taken.
    public void Return()
    {
        lock (_lock)
        {
            _taken = -1;
        }
    }

    /// Picks the one slot index (of three) that is neither `a` nor `b`.
    /// Called only under `_lock`. With three slots and at most two indices
    /// excluded, exactly one candidate always remains.
    private static int PickFreeSlot(int a, int b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (i != a && i != b)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unreachable: three slots, at most two excluded.");
    }
}

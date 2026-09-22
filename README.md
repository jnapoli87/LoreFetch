# LoreFetch

Turn any webcam into a Magic: The Gathering collection scanner. Fully offline, no subscription, no phone.

LoreFetch turns a webcam on a desk mount into a Magic: The Gathering collection scanner. Lay out one card or nine, tap space, and it identifies them by image — then exports your inventory to CSV. Everything runs locally: it ships its own card fingerprint index, so after install it never needs the network. Free and GPLv3, because the paid apps shouldn't be the only option.

> [!NOTE]
> **Status: planning complete, implementation not started.** There is no working software here yet — only design documents. The plan is public from the start because the interesting part is the reasoning, not the code.

## How it will work

Cards are identified by **perceptual hash**, not OCR. Nothing reads the card name — at a realistic overhead camera height the name is about five pixels tall, which rules text-reading out entirely. Instead each card is rectified, blurred, reduced to a 32×32 thumbnail and turned into a 1024-bit fingerprint, then matched by Hamming distance against a prebuilt index.

That approach isn't novel here: it's a port of [CardSpotter](https://github.com/relgin/cardspotter) (BSD-3-Clause), which is the engine behind Wizards of the Coast's own SpellTable. Using a technique already proven in production at this exact camera geometry was the single biggest risk reduction available.

## Planned scope for v1

- One, three, or nine cards per capture, detected automatically
- Keyboard-driven: space to capture, enter to accept, escape to discard
- A confirmation grid where you click to *exclude* rather than to approve, so a clean batch commits with no clicks
- Collection inventory with CSV as the source of truth
- Export adapters for other collection tools

### Known limitations, by design

- **Printings are not distinguished.** Reprints share artwork, so a perceptual hash physically cannot tell a card's set apart. v1 identifies the card, not which printing you own — which also means no price data.
- **Foils are unreliable.** Glare defeats image hashing without polarised or diffuse lighting.
- **Basic lands** are identified, but every basic land art resolves to the same name.

## Documentation

| Document | Contents |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | Every settled decision, and why each rejected alternative stays rejected |
| [`docs/PLAN.md`](docs/PLAN.md) | Build sequencing: a serial foundation pass, then four parallel streams |
| [`docs/CONTRACTS.md`](docs/CONTRACTS.md) | The interface seam that lets those streams run independently |
| [`docs/TESTING.md`](docs/TESTING.md) | Test strategy across five levels |
| [`docs/stream-a-ui.md`](docs/stream-a-ui.md) | Avalonia UI |
| [`docs/stream-b-identification.md`](docs/stream-b-identification.md) | Hashing, indexing, card detection, accuracy measurement |
| [`docs/stream-c-capture.md`](docs/stream-c-capture.md) | Webcam capture |
| [`docs/stream-d-export.md`](docs/stream-d-export.md) | Export formats |

## Working on it

```sh
scripts/lorefetch.sh setup     # FIRST, in any fresh clone — see below
scripts/lorefetch.sh doctor    # environment + guard check; fails if not set up
scripts/lorefetch.sh build
scripts/lorefetch.sh test
```

> **Run `setup` before your first commit in a new clone.** `hooks/pre-commit` is tracked, so the
> file arrives with the clone — but `core.hooksPath` is *local config*, and config does not clone.
> Until you wire it, the hook does not run, and `user.email` falls back to your global identity.
> `setup` sets the repo-local identity, the hooks path and the SSH key pin, and touches nothing
> global. `doctor` exits non-zero while the guards are not live, so it is safe to trust in a script.

> **If you have a clone from before 2026-09-21**, its history no longer matches: the repo's history
> was rewritten that day and every commit hash changed. Use
> `git fetch && git reset --hard origin/main` — **not** `git pull`, which would merge the old
> history back in. Prefer that over re-cloning, because of the point above.

## Stream A — UI

Filled in by Stream A as the Avalonia app, auto-capture trigger and capture/cohort UI land.

## Stream B — Identification

Filled in by Stream B as the hash port, index build and detection/accuracy work land.

## Stream C — Capture

`LoreFetch.Capture` turns the webcam into a stream of `CameraFrame`s via FlashCap. `WebcamFrameSourceFactory` is the entry point; callers receive an `IFrameSource`. The source keeps exactly one frame with `DropOldest` semantics — a slow consumer sees latency, never a backlog — and allocates from `ArrayPool<byte>` so sustained 30 fps produces no garbage. First-frame and mid-stream watchdogs (thresholds in `ScanSettings.FirstFrameTimeoutMs` and `FrameWatchdogMs`) surface device loss: unplug, in-use, and permission-denied all present identically as frames that never arrive.

**The C920 is USB 2.0.** Uncompressed 1080p can only advertise 5 fps on that bus; MJPG at 1080p advertises 30. `WebcamFrameSourceFactory` selects the MJPG 1920×1080 30 fps characteristic from `EnumerateDescriptors()` — what the device actually declares, not a `set()` call that returns `false` and silently falls back. If that format is absent, it throws with the full enumerated list. `IFrameSource.Description` reports the negotiated format, not the requested one.

FlashCap hands the callback raw JPEG bytes for an MJPEG stream — it does not decode. Decode is this stream's job, via `Cv2.ImDecode`, producing BGR24 frames. Those are rotated in-source by `ScanSettings.CameraRotationDegrees` (default 90°, via `Cv2.Rotate`) before reaching the detection pipeline. Per-frame decode time is logged — 30 JPEG decodes per second at 1080p is the first place to look if the preview feels slow.

> [!IMPORTANT]
> **`win-x64` only.** FlashCap gained an AVFoundation backend in 1.11.0, so `LoreFetch.Capture` builds and nominally runs on macOS — but [issue #182](https://github.com/kekyo/FlashCap/issues/182) reports a native crash on capture start and a BGRA/RGB channel mismatch. A native crash takes down the test host, so the `macos-latest` CI leg compiles but never opens a device. Mac users: builds from source; camera capture is unsupported.

## Stream D — Collection & export

Filled in by Stream D as the collection store, native format and export adapters land.

## Licence

[GPLv3](LICENSE). Deliberately copyleft: fork it, sell it, do as you like — but ship your source too. The point is that a free option stays free.

Card imagery is never committed to this repository. Only derived fingerprints and measurements are, which carry no artwork.

---

*Portions of card data courtesy of [Scryfall](https://scryfall.com). LoreFetch is unofficial Fan Content permitted under the [Wizards of the Coast Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved or endorsed by Wizards. Magic: The Gathering and its logos are trademarks of Wizards of the Coast LLC in the United States and other countries. © Wizards of the Coast LLC.*

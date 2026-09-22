# LoreFetch

Turn any webcam into a Magic: The Gathering collection scanner. Fully offline, no subscription, no phone.

Cataloging Magic cards shouldn't mean a monthly subscription or holding your phone over one card at a time. LoreFetch turns a webcam into a scanner for players and independent game stores: lay down up to nine cards, and it identifies them offline, for free, and exports your collection to Moxfield.

> [!NOTE]
> **Status: in progress, v0.1.0 not yet released.** Implementation is under way in four parallel streams; [`docs/orchestration-plan.md`](docs/orchestration-plan.md) records exactly what has landed. The plan has been public from the start because the interesting part is the reasoning, not just the code.

## What it solves

| Problem | What LoreFetch does |
|---|---|
| **Scanning one card at a time.** Phone apps want each card held under the camera, one by one. | Lay down one, three or nine cards. It finds them, and in auto mode it captures on its own once they stop moving. |
| **Subscriptions and the cloud.** The capable scanners are paid, and most need a connection. | Free, GPLv3, and fully offline. It ships its own card fingerprint index, so it never needs the network after install. |
| **Special hardware.** | A webcam looking straight down at the table: the same kind of overhead setup people already use for SpellTable. The reference rig is a hand-built PVC gantry, with the webcam duct-taped to the crossbar. |
| **Wrong matches slipping in.** | Low-confidence matches are highlighted. Click a card to exclude it, or right-click to set it by hand, before anything is saved. |
| **Getting the collection somewhere useful.** | The collection is a plain CSV you own, and it exports straight to [Moxfield](https://moxfield.com). |

## How it will work

Cards are identified by **perceptual hash**, not OCR. Nothing reads the card name — at a realistic overhead camera height the name is about five pixels tall, which rules text-reading out entirely. Instead each card is rectified, reduced to a 32×32 thumbnail and turned into a 1024-bit fingerprint, then matched by Hamming distance against a prebuilt index.

That approach isn't novel here: it's a port of [CardSpotter](https://github.com/relgin/cardspotter) (BSD-3-Clause), which is the engine behind Wizards of the Coast's own SpellTable. Using a technique already proven in production at this exact camera geometry was the single biggest risk reduction available.

## Planned scope for v0.1.0

- Modern-frame English cards, single-faced, non-foil
- One, three, or nine cards per capture, detected automatically, with an auto-capture mode that fires once the cards stop moving
- Keyboard-driven: space to capture, enter to accept, escape to discard
- A confirmation grid where you click to *exclude* rather than to approve, so a clean batch commits with no clicks — and right-click to set a card by hand
- Collection inventory with CSV as the source of truth, and export to [Moxfield](https://moxfield.com)
- Fully offline, shipped as a single Windows executable. The code is cross-platform and CI runs on Windows and macOS; Windows is simply the one release target.

### Known limitations, by design

- **Printings are not distinguished.** Reprints share artwork, so a perceptual hash physically cannot tell a card's set apart. v1 identifies the card, not which printing you own — which also means no price data.
- **Foils are unreliable.** Glare defeats image hashing without polarised or diffuse lighting.
- **Basic lands** are identified, but every basic land art resolves to the same name.
- **New sets need a new index.** LoreFetch recognises cards that were on Scryfall when its index was built, and the release notes give that date. To add newer sets, rebuild the index with the Lab tool (`bulk` → `images` → `build-index`; the first run downloads about 5 GB) or download an updated index from the releases page.

### After v0.1.0

Stretch goals, roughly in order:

- **Worth-sleeving flag:** marks cards worth pulling out of bulk, whatever their condition. Aimed at card shops, where the person at the scanner may know Pokémon but not Magic.
- **Update card data in the app:** add new sets without waiting for a release.
- **Foils**
- **Set and printing detection** from the collector number
- **Double-faced cards**
- **A supported macOS release**
- **The 3D-printed mount**

Details and reasoning are in [`docs/PLAN.md`](docs/PLAN.md#stretch-goals--after-v010).

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

## Cameras

Any webcam that delivers 1080p should work. These are the ones actually run, rather than the ones expected to work — if you use another, open an issue with the camera, resolution and OS and it gets added.

| Camera | Tested at | Notes |
|---|---|---|
| Logitech C920 | 1920×1080, 30 fps, Windows 11 (2026-09) | USB 2.0, so 1080p only reaches 30 fps in MJPG; uncompressed caps at 5. LoreFetch requests MJPG and then reads the setting back to confirm it took, because OpenCV reports success either way. |

The lamp matters more than the camera: put it low and off to one side, never beside the lens. Glare is the main way identification fails.

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

Hardware-verified on the Windows PC with a Logitech C920 (2026-09-22):

| Check | Result |
|---|---|
| Format negotiation | Negotiated `HD Pro Webcam C920 1920x1080 MJPG @30fps (DirectShow)`; the VfW "Default" device was enumerated and correctly skipped; Media Foundation offered 335 characteristics, DirectShow 35 |
| Delivered rate | 28.6 fps over a 10 s window; first frame ~720–790 ms after `StartAsync` |
| JPEG decode | 11–15 ms average per 150-frame window, max 42 ms (one outlier) |
| Sustained memory | 3-minute run, 5,035 frames: private bytes oscillated 180–190 MB, post-warm-up growth 9.5 MB (well under the 100 MB bound) |
| Slow consumer | 500 ms/frame for 60 s: +5.7 MB private-bytes growth, capture→consume latency mean 33 ms, max 77 ms |
| Unplug | `FrameSourceException` raised 2,002 ms after the last frame (`FrameWatchdogMs` = 2000); `DisposeAsync` completed in 23 ms; replugging and reopening worked, first frame in 719 ms |

## Stream D — Collection & export

Filled in by Stream D as the collection store, native format and export adapters land.

## Licence

[GPLv3](LICENSE). Deliberately copyleft: fork it, sell it, do as you like — but ship your source too. The point is that a free option stays free.

Card imagery is never committed to this repository. Only derived fingerprints and measurements are, which carry no artwork.

---

*Portions of card data courtesy of [Scryfall](https://scryfall.com). LoreFetch is unofficial Fan Content permitted under the [Wizards of the Coast Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved or endorsed by Wizards. Magic: The Gathering and its logos are trademarks of Wizards of the Coast LLC in the United States and other countries. © Wizards of the Coast LLC.*

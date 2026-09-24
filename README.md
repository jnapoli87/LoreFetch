# LoreFetch

Turn any webcam into a Magic: The Gathering collection scanner. Fully offline, no subscription, no phone.

Lay down up to nine cards; LoreFetch finds them, identifies them, and adds them to a CSV you own. Free and GPLv3, and it never touches the network after install.

> [!NOTE]
> **v0.1.0 is released** — see [Download and run](#download-and-run).

## Demo

Video of one full pass — open the app, scan, export, import into Moxfield — goes here.

<!-- Video slots. Paste the YouTube id into both halves and delete the line above; GitHub
     renders the thumbnail as a play link:

     [![Webcam scan to Moxfield export](https://img.youtube.com/vi/VIDEO_ID/hqdefault.jpg)](https://youtu.be/VIDEO_ID)

     One entry per video. Keep the link text describing the function being shown, not the
     video's own title, so the list stays readable as it grows. -->

## Download and run

[**LoreFetch v0.1.0, Windows 10/11 x64**](https://github.com/jnapoli87/LoreFetch/releases/tag/v0.1.0) — one self-contained executable, ~86 MB zipped. No .NET install, no prerequisites.

1. Extract the zip anywhere.
2. **Keep `data/index/` beside `LoreFetch.exe`.** That's the card fingerprint index; the app resolves it relative to the executable and won't start without it.
3. Run it. The exe is unsigned, so SmartScreen warns on first run — **More info → Run anyway**.
4. Aim a webcam straight down from about 15″. Put the lamp low and off to one side, never beside the lens.

| Environment variable | Effect |
|---|---|
| `LOREFETCH_COLLECTION` | Where the collection CSV is written |
| `LOREFETCH_FRAMES_DIR` | Read a folder of images instead of the camera |
| `LOREFETCH_MODE=fakes` | Stand-in pipeline for UI testing — never reports a real match |

## Using it

One window: live preview with card outlines on the left, the capture grid on the right, your collection docked below.

| Key / action | Effect |
|---|---|
| **Space** | Capture whatever is detected right now. Replaces any pending grid. |
| **Enter** | Commit every tile that isn't X'd. |
| **Escape** | Discard. Nothing is written. |
| **Left-click a tile** | Toggle its **X**. No X means included. |
| **Right-click a tile** | Set the card by hand, or clear a manual pick. |

The happy path is space, enter, space, enter — no mouse. Capture only fills the grid; Enter is the only thing that writes.

**Tile borders:** none = confident · amber = low confidence, worth a look · red = unresolved, right-click to set · grey with ✕ = excluded · blue with **M** = set by hand.

**Auto mode** captures on its own once the expected count (1, 3 or 9) holds still for 500 ms. It fires once per scene, then waits for the count to change — so swapping one card for another won't re-fire. Press Space for that.

The **Dist** column is how many of the 1024 fingerprint bits differ from the closest card in the index. Lower is better. Sort by it to audit your own worst matches.

## How it works

Perceptual hash, not OCR — at overhead height the card name is about five pixels tall, so nothing reads text.

Each card is found by contour (63:88 aspect ratio, minimum area), perspective-corrected, reduced to a 32×32 thumbnail, and turned into a 1024-bit fingerprint matched by Hamming distance against **47,418 artworks** indexed from Scryfall. Detection would rather find nothing than guess: a rejected contour is logged with its reason instead of being hashed.

The hash is a port of [CardSpotter](https://github.com/relgin/cardspotter) (BSD-3-Clause). Finding the cards is ours — CardSpotter identifies a card you click on.

Details: [`docs/design/identification.md`](docs/design/identification.md).

## Accuracy

| Measurement | Result |
|---|---|
| correct@1, normal cards | **70.4%** (38/54) |
| **wrong@1** | **0** |
| Detection | 91% (49/54 slots) |
| Distance bands | confident ≤ 208 · low confidence to 240 · unresolved above |

Measured on six Logitech C920 frames: tight 3×3 grid, light mat, 15″, non-land cards. `wrong@1 = 0` is the number that matters — a miss shows up as an unresolved tile, not as a wrong card written into your collection.

**That table predates dual-hypothesis identification and was never re-measured, so treat it as a floor.** Hand validation of the release build over ~50–60 cards at 21″ on white: normal cards were uniformly fine, foils identified reliably under diffuse off-axis light, sleeved cards worked but wanted the camera closer. No correct@1 is claimed for that pass — it was a hand check, not a measured run.

Method and every recorded run: [`docs/accuracy.md`](docs/accuracy.md). The numbers aren't reproducible from a clone, because the fixture frames are Wizards artwork and can't be committed; CI measures against synthetic frames instead.

## Limitations

- **No set or printing.** Reprints share artwork, so the hash can't tell them apart. Oracle name only — and therefore no prices.
- **Foils and sleeves depend on your lighting.** Neither is a supported case.
- **Leave a finger-width gap.** Cards touching edge-to-edge merge into one contour and are never split apart.
- **Basic lands** all resolve to the same name, whatever the art.
- **Dark mats hurt detection.** A black-bordered card on a black mat has no edge to find.
- **New sets need a new index.** Rebuild it with the Lab tool (`bulk` → `images` → `build-index`; ~5 GB on the first run), or download an updated index from the releases page.
- **Windows x64 only.** The code is portable and CI runs on macOS, but macOS camera capture is broken upstream ([FlashCap #182](https://github.com/kekyo/FlashCap/issues/182)) — build from source, unsupported.

## Export

The collection is a plain CSV you own — UTF-8 with BOM, so Excel opens accented card names intact. It exports as itself, or as **Moxfield**, verified by a real import on 2026-09-22.

> [!IMPORTANT]
> **Upload it under Collection, not as a decklist.** Use the CSV upload at [moxfield.com/collection](https://moxfield.com/collection). The decklist box on Moxfield's home page will not ingest the file correctly.

ManaBox, Archidekt, Deckbox and Dragon Shield are not in v1 — each either needs a set or printing id we can't supply, or has no first-party import spec to target. Reasons in [`docs/design/collection.md`](docs/design/collection.md).

## Cameras

Any webcam that delivers 1080p should work. This is what has actually been run:

| Camera | Tested at | Notes |
|---|---|---|
| Logitech C920 | 1920×1080 MJPG, 30 fps, Windows 11 | USB 2.0, so uncompressed 1080p caps at 5 fps. The format is negotiated explicitly and logged, so a silent fallback is visible rather than mysteriously slow. Delivered 28.6 fps; first frame ~750 ms. |

Using something else? Open an issue with the camera, resolution and OS and it gets added.

**The lamp matters more than the camera.** Glare is the main way identification fails.

## Next

Roughly in order: a worth-sleeving flag for bulk sorting · in-app card data updates · foils · set and printing detection from the collector number · double-faced cards · a supported macOS release · the 3D-printed mount.

Reasoning in [`docs/history/PLAN.md`](docs/history/PLAN.md#stretch-goals--after-v010).

## Building

```sh
scripts/lorefetch.sh setup     # FIRST, in any fresh clone
scripts/lorefetch.sh doctor    # fails while the hook isn't wired
scripts/lorefetch.sh build
scripts/lorefetch.sh test
```

`setup` points `core.hooksPath` at the tracked `hooks/` folder and touches nothing global. Run it before your first commit: `core.hooksPath` is local config and doesn't clone, so until then `hooks/pre-commit` silently never fires, and you'd find out about stray card images or a trimmed notices file from CI instead. Set your own git identity; a GitHub noreply address keeps your email out of public history.

Clones from before 2026-09-21 no longer match — the history was rewritten that day. Fetch, then hard-reset to `origin/main`; a merge would drag the old history back.

## Documentation

| Document | Contents |
|---|---|
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | How to contribute: issue → branch → PR, and what CI checks |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Every settled decision, and why each rejected alternative stays rejected |
| [`docs/accuracy.md`](docs/accuracy.md) | Accuracy: corpora, method, every recorded run |
| [`docs/history/orchestration-plan.md`](docs/history/orchestration-plan.md) | The build log — every ruling in order, including open bugs |
| [`docs/history/PLAN.md`](docs/history/PLAN.md) | Build sequencing |
| [`docs/CONTRACTS.md`](docs/CONTRACTS.md) | The interface seam between components |
| [`docs/TESTING.md`](docs/TESTING.md) | Test strategy across five levels |
| [`docs/design/app.md`](docs/design/app.md) | Avalonia UI |
| [`docs/design/identification.md`](docs/design/identification.md) | Hashing, indexing, detection, accuracy measurement |
| [`docs/design/capture.md`](docs/design/capture.md) | Webcam capture |
| [`docs/design/collection.md`](docs/design/collection.md) | Export formats |

## Licence

[GPLv3](LICENSE). Deliberately copyleft: fork it, sell it, do as you like — but ship your source too. The point is that a free option stays free.

Card imagery is never committed to this repository. Only derived fingerprints and measurements, which carry no artwork.

---

*Portions of card data courtesy of [Scryfall](https://scryfall.com). LoreFetch is unofficial Fan Content permitted under the [Wizards of the Coast Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved or endorsed by Wizards. Magic: The Gathering and its logos are trademarks of Wizards of the Coast LLC in the United States and other countries. © Wizards of the Coast LLC.*

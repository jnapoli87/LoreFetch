# LoreFetch

Turn any webcam into a Magic: The Gathering collection scanner. Fully offline, no subscription, no phone.

LoreFetch turns a webcam on a desk mount into a Magic: The Gathering collection scanner. Lay out one card or nine, tap space, and it identifies them by image — then exports your inventory to CSV. Everything runs locally: it ships its own card fingerprint index, so after install it never needs the network. Free and GPLv3, because the paid apps shouldn't be the only option.

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

Filled in by Stream C as the webcam capture pipeline lands.

## Stream D — Collection & export

Filled in by Stream D as the collection store, native format and export adapters land.

## Licence

[GPLv3](LICENSE). Deliberately copyleft: fork it, sell it, do as you like — but ship your source too. The point is that a free option stays free.

Card imagery is never committed to this repository. Only derived fingerprints and measurements are, which carry no artwork.

---

*Portions of card data courtesy of [Scryfall](https://scryfall.com). LoreFetch is unofficial Fan Content permitted under the [Wizards of the Coast Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved or endorsed by Wizards. Magic: The Gathering and its logos are trademarks of Wizards of the Coast LLC in the United States and other countries. © Wizards of the Coast LLC.*

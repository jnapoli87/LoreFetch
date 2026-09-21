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

## Licence

[GPLv3](LICENSE). Deliberately copyleft: fork it, sell it, do as you like — but ship your source too. The point is that a free option stays free.

Card imagery is never committed to this repository. Only derived fingerprints and measurements are, which carry no artwork.

---

*Portions of card data courtesy of [Scryfall](https://scryfall.com). LoreFetch is unofficial Fan Content permitted under the [Wizards of the Coast Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved or endorsed by Wizards. Magic: The Gathering and its logos are trademarks of Wizards of the Coast LLC in the United States and other countries. © Wizards of the Coast LLC.*

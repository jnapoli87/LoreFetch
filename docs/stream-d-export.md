# Stream D — Collection & export

**Short, fully isolated, and the stream that owns the user's data.** It stores the collection and projects it into other tools' shapes. No camera, no UI, no hash, no image processing.

Owns (exclusive write access): `LoreFetch.Core/Collection/**`, `LoreFetch.Core/Export/**`, `Tests/StreamD/**`, the stream D section of `README.md`
Consumes: `Core/Abstractions` — `Cohort`, `CohortTile`, `CollectionRow`, `ICollectionStore`, `ICollectionExporter`, `ExportFormat` (frozen — see [`CONTRACTS.md`](CONTRACTS.md))
Implements: `ICollectionStore`, and every `ICollectionExporter`
Must not touch: `LoreFetch.App`, `Core/Identification`, `Core/Imaging`, `Core/Trigger`, `Core/Scanning`, `LoreFetch.Capture`, the fakes, any `.csproj`, `LoreFetch.slnx`

Can start the moment Stream 0 lands, and can finish long before the others. The store is file-in/file-out and every adapter is a pure function over a row list, so this is the most testable stream in the project.

---

## The limitation to design around, not paper over

v1 identifies the **oracle card only — no set, no collector number.** The hash physically cannot distinguish printings that share art, so this isn't a gap to fill later in v1; it's inherent.

Most target formats key on exactly what we don't have:

| Tool | Identity columns it wants |
|---|---|
| Moxfield | `Name`, `Edition`, `Collector Number` |
| ManaBox | `Name`, `Set code`, `Collector number`, `Scryfall ID` |
| Archidekt | `Name`, `Edition Code`, `Collector Number`, `Scryfall ID` |
| Deckbox | `Name`, `Edition`, `Card Number` |

**Decision: emit the name and quantity, leave printing columns blank, and let the target tool resolve.** Do *not* fabricate a plausible set code.

The tempting alternative — the index knows an oracle id, so pick the most recent or cheapest printing — is worse. It manufactures **false precision** in someone's collection data and, downstream, in their valuation. A blank column is honest and the importing tool will substitute its own default; a guessed column looks authoritative and is wrong. If offered at all, it's an explicit opt-in setting with a visible warning, and it is a stretch item, not v1.

This limitation goes in the README, not just here.

**Condition is the same shape of problem.** v1 never assesses a card's physical condition, so `CollectionRow.Condition` is always blank. Emit it as an empty field and let the target tool apply its own default — don't write "NM" to satisfy a parser.

---

## Tasks, in order

### D0 — The collection store (`Core/Collection`)
Implement `ICollectionStore` over the native CSV:
- **Commit** every tile whose `State` is `Included` or `ManuallySet`. Tile → row: `Chosen` gives `OracleId`/`OracleName`; `ManuallySet` → `Source = Manual`, `BestMatchDistance` null; `Included` → `Source = Hash`, `BestMatchDistance = ChosenDistance`.
- **Dedup on `OracleId` + `Condition`** → increment `Quantity`, update `LastScannedAt`. A blank condition is a value like any other.
- **Write via temp-file + atomic rename**, keeping a `.bak` of the previous file. UTF-8 with BOM.
- **Read through the native format's own parser**, which fails loudly on unknown or missing columns rather than mis-parsing.

### D1 — The native format, through the same seam
Our own CSV store format **is the source of truth**, and every adapter projects *down* from it. It carries the full `CollectionRow` — `OracleId`, `OracleName`, `Quantity`, `Condition`, `LastScannedAt`, `BestMatchDistance`, `Source` — deliberately richer than any v1 adapter consumes, so the SOT never becomes the lossy bottleneck.

Implement it as an `ICollectionExporter` rather than a special case, so the most-used path shares code and tests with the adapters. UTF-8 **with BOM** or Excel mangles non-ASCII names. The header row's exact column set is the format version; a reader seeing unknown or missing columns must fail loudly rather than mis-parse.

Note the asymmetry this creates: the native export is lossless, and **every third-party adapter is lossy by definition** — they drop `OracleId`, `Source` and `BestMatchDistance` because those tools have nowhere to put them. That's fine and expected; it's also the reason the native format is what a user should keep.

### D2 — Two adapters, verified
Pick **two** targets and get them genuinely working: **Moxfield** and **ManaBox** are the highest-value pair (largest user bases, both accept name-keyed CSV import).

For each: exact header row, exact column order, quoting and escaping rules for names containing commas, apostrophes and accents — `Lim-Dûl's Vault`, `Borrowing 100,000 Arrows`, `Kongming, "Sleeping Dragon"` are the test cases that break naive CSV writers. Cite the source for each spec in a comment; these are **community-documented formats, not versioned APIs**, so they drift.

### D3 — Round-trip and escaping tests
Pure-function tests, no I/O fixtures needed beyond in-memory streams:
- names with `,` `"` `'` and non-ASCII survive a write→parse round trip
- an empty collection produces a valid header-only file, not an empty file
- quantity aggregation is preserved
- blank printing and condition columns are emitted as empty fields, not the literal `null`
- the store: commit increments rather than duplicates; an `Excluded` tile writes nothing; a `ManuallySet` tile writes `Source = Manual` with no distance; a crash between temp-write and rename leaves the previous file intact

### D4 — Manual import verification ⚠️ the part that actually matters
**Import at least one generated file into the real tool and confirm it lands correctly.** An adapter that has never been imported is a guess with a test suite around it.

This needs an account on the target and is a manual step — it cannot be automated or CI'd. Record the result (tool, date, row count, what it resolved printings **and blank conditions** to) in the README, so users know what's verified and what isn't.

### D5 — Additional formats, if time allows
Archidekt, Deckbox, Dragon Shield. Each is ~30 lines once the seam exists.

**Two verified adapters beat six unverified ones.** Claiming six and having none actually imported is worse than claiming two, because the first failed import is the last time a user trusts the tool.

---

## Done when

- `ICollectionStore` commits cohorts with `OracleId` + condition dedup, atomic writes and a `.bak`.
- The native CSV format is implemented as an `ICollectionExporter`, and the store uses it.
- **Two** adapters implemented with exact headers and correct escaping.
- **At least one** verified by a real import into the live tool, with the result recorded in the README.
- Escaping tests cover comma, quote, apostrophe and non-ASCII names.
- Printing columns are blank rather than fabricated, and the README states the limitation.
- The UI's format picker enumerates whatever exporters exist, with no per-format code in `LoreFetch.App`.

## Fallbacks

- **A target format can't import name-only rows:** document it as unsupported rather than fabricating set codes to satisfy the parser. State it in the README.
- **No account for manual verification:** ship the adapter marked *unverified* in both the README and the UI picker. Honest labelling costs nothing; a silently-broken export costs trust.
- **Time runs out:** the native CSV export alone satisfies the v1 deliverable. Everything here is additive.

---

## What a reviewer should scrutinise here

1. **Is the native format implemented through `ICollectionExporter`**, or special-cased in the store? Special-casing means the most-used path has the least-shared test coverage.
2. **CSV escaping.** Is there a real quoting implementation, or string concatenation with commas? Test against `Kongming, "Sleeping Dragon"` specifically — it contains both a comma and embedded quotes.
3. **UTF-8 BOM present?** Without it Excel mangles accented card names, and it will be reported as a data-corruption bug.
4. **Are printing columns blank, or fabricated?** Any code path that guesses a set code should be removed or gated behind an explicit, warned setting.
5. **Is each format spec's source cited?** These drift, and an uncited magic header row is unmaintainable.
6. **Has any adapter actually been imported into its target tool** — and does the README distinguish verified from unverified?
7. **Does the UI contain per-format knowledge?** It should enumerate exporters and know nothing about Moxfield or ManaBox specifically.
8. **Empty-collection behaviour** — header-only file, or a zero-byte file that the target rejects with a confusing error?

## Risks owned by this stream

1. **Unverified adapters.** The main risk, and the reason D4 exists. A format that has never been imported is fiction.
2. **Format drift.** Community-documented shapes change without notice. Mitigated by keeping adapters tiny and citing sources, so a fix is minutes.
3. **The printing gap is inherent, not temporary.** Users importing into a price-tracking tool will get default printings and therefore wrong valuations. This is a documentation problem, and under-documenting it is how the project gets a reputation for bad data.
4. **CSV escaping is deceptively easy to get wrong**, and MTG card names are unusually hostile to naive writers — commas, quotes, apostrophes, em-dashes, accented characters and Unicode all appear in real card names.
5. **The store is the one place user data can be silently corrupted.** A bad dedup key or a non-atomic write loses someone's collection. Everything else in the app can fail loudly; this can't.

---

## Plan review: research targets

For the pre-build stream review (see [`stream-review-directions.md`](stream-review-directions.md)). Check each against primary sources — each tool's own import documentation, not blog posts — and record what you found, citing the source.

1. **Moxfield and ManaBox import specs.** Exact headers, column order, required vs optional columns, quoting rules. Does each accept **name-only** rows (no set, no collector number)?
2. **Blank condition.** What does each tool do on import when the condition column is empty — default to NM, reject the row, or reject the file?
3. **Atomic rename on Windows.** Is `File.Move(temp, target, overwrite: true)` (or `File.Replace`) actually atomic on NTFS? This doc and `CONTRACTS.md` assert it; confirm or correct.
4. **UTF-8 BOM.** Does each target tool's importer tolerate a BOM? A BOM that fixes Excel but breaks an importer is a per-adapter decision, not a global one.
5. **The seam.** Can the store do everything in D0 with only `Cohort`, `CohortTile` and `CollectionRow` as the contract defines them?

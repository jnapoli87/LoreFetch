# Collection & export — design record

**Short, fully isolated, and the stream that owns the user's data.** It stores the collection and projects it into other tools' shapes. No camera, no UI, no hash, no image processing.

> [!NOTE]
> **Design record from v0.1.** Written as the hackathon's Stream D spec. Code comments cite its section IDs (D0, D1, …), so they stay stable. The parallel-build rules (exclusive ownership, "must not touch") were dropped after v0.1; the original is at tag `v0.1.0`.
>
> Code: `src/LoreFetch.Core/Collection`, `src/LoreFetch.Core/Export`. Tests: `Tests/Collection`.

Consumes: `Core/Abstractions` — `Cohort`, `CohortTile`, `CollectionRow`, `ICollectionStore`, `ICollectionExporter`, `ExportFormat`, **`OracleEntry`**, **`TileState`**, **`RowSource`**, **`CollectionStoreException`** (see [`CONTRACTS.md`](../CONTRACTS.md))
Implements: `ICollectionStore`, and every `ICollectionExporter`
> [!NOTE]
> **Reconciled 2026-09-21.** Every proposal and open question below has been ruled on; the contract surface in [`CONTRACTS.md`](../CONTRACTS.md) is now final and the rulings are recorded in [`RECONCILIATION.md`](../RECONCILIATION.md). The *Plan review findings* section is kept as the review record — **read the disposition notes before acting on any recommendation there.** What changed for this stream:
>
> - **All 4 proposed contract changes accepted.** The three missing types are on the *Consumes* line above.
> - **v1 ships the native SOT plus ONE adapter: Moxfield.** The user's call — one export beyond the SOT is enough. Deckbox is *not* in v1; document ManaBox, Archidekt, Deckbox and Dragon Shield in the README as unsupported-by-design.
> - **`Condition`: `null` is the only representation of "unassessed".** It serialises to a blank field and a blank field parses back to `null`, never `""`.
> - **`CommitCohortAsync` returns cards committed** (sum of quantity increments), not rows touched. Fold within-cohort duplicates first.
> - **Throw `CollectionStoreException`** when the file cannot be replaced; the caller keeps its cohort and retries. Never discard it, never fall back to a non-atomic in-place write.
> - **Duplicate `OracleId` + `Condition` rows merge on read** — sum `Quantity`, keep the latest `LastScannedAt`, log it. Justified by **import robustness**, not by hand-editing: the user explicitly rejected hand-editing CSV as a requirement.
> - **Temp file in the target's own directory**, `Flush(true)`, then `File.Move(overwrite: true)`. `%TEMP%` silently degrades the rename to copy+delete.
> - **`CsvHelper` is pinned** in Stream 0 as insurance. Native keeps the BOM; Moxfield writes none.


Can start the moment Stream 0 lands, and can finish long before the others. The store is file-in/file-out and every adapter is a pure function over a row list, so this is the most testable stream in the project.

---

## The limitation to design around, not paper over

v1 identifies the **oracle card only — no set, no collector number.** The hash physically cannot distinguish printings that share art, so this isn't a gap to fill later in v1; it's inherent.

Most target formats key on exactly what we don't have:

| Tool | Identity columns it wants | Accepts name-only? |
|---|---|---|
| Moxfield | `Count`, `Name`, `Edition`, `Collector Number`, `Foil` — but **only `Name` is required** | **Yes** — documented |
| ManaBox | `Card name` **plus** `Set code`/`Set name`, *or* a `Scryfall ID` | **No** — documented minimum |
| Archidekt | No fixed header at all — columns are user-mapped at import | **No** — upload is blocked as ambiguous |
| Deckbox | `Count`, `Name`, `Edition`, `Card Number`; header mandatory, order matters | **Yes** — edition left unset |

Corrected against each tool's own sources — see the findings section. Three changes from what this table said before: Moxfield's quantity column is **`Count`**, not `Quantity`, and only `Name` is strictly required ([Moxfield help](https://moxfield.com/help/help-articles/importing-collection)); **Archidekt has no fixed import header** and deliberately removed its own preset, because its exporter is user-configurable, so a literal header row buys nothing ([Archidekt staff, own forum](https://archidekt.com/forum/thread/15700538)); and Deckbox's quantity column is **`Count`** with a mandatory header whose column order matters ([Deckbox importer error text](https://deckbox.org/forum/viewtopic.php?id=30026)).

**Decision: emit the name and quantity, leave printing columns blank, and let the target tool resolve.** Do *not* fabricate a plausible set code.

⚠️ **This decision is now known to be incompatible with two of the four tools above, and that changes the adapter shortlist.** ManaBox documents a hard minimum of *card name plus set name or set code*, or a `Scryfall ID`, precisely because "the app needs to know some way of distinguishing different versions of the same card" ([ManaBox import/export guide](https://www.manabox.app/guides/collection/import-export/)); Archidekt blocks the upload outright with *"Not enough identifiable information was provided for card lookups"* ([Archidekt importer release post](https://archidekt.com/news/5891613)). Neither can be targeted without fabricating the printing this stream has correctly decided never to fabricate. So the stream's own fallback — *"a target format can't import name-only rows: document it as unsupported"* — **fires for ManaBox and Archidekt**, and it fires by design, not by accident.

Also load-bearing: **ManaBox's `Scryfall ID` column is a *printing* id, not `oracle_id`.** Verified by resolving a sample value from a real ManaBox export against the Scryfall API — it matched a card's `id`, whose `oracle_id` is a different GUID. Our native `OracleId` therefore must never be written into that column; it would look like a precise printing reference and resolve to the wrong thing, or to nothing.

The tempting alternative — the index knows an oracle id, so pick the most recent or cheapest printing — is worse. It manufactures **false precision** in someone's collection data and, downstream, in their valuation. A blank column is honest and the importing tool will substitute its own default; a guessed column looks authoritative and is wrong. If offered at all, it's an explicit opt-in setting with a visible warning, and it is a stretch item, not v1.

This limitation goes in the README, not just here.

**Condition is the same shape of problem.** v1 never assesses a card's physical condition, so `CollectionRow.Condition` is always blank. Emit it as an empty field and let the target tool apply its own default — don't write "NM" to satisfy a parser.

**The decision is sound and safe to ship, but be honest about what it achieves.** Blank is accepted by both recommended targets: Moxfield's `Condition` is optional (only `Name` is required), and Deckbox "will just be left blank" if you omit it. What blank does *not* do is prevent the false precision — Moxfield's import request always carries a `defaultCondition` of `nearMint`, so a blank almost certainly lands as Near Mint on their side. Our file stays honest; the *importing tool* invents the precision, and we cannot stop it. That is still much better than writing "NM" ourselves, because the fabrication then lives in the target tool's defaults rather than in the user's source of truth. It also means D4's README record must state what each tool resolved blank conditions to — the doc already asks for this, and now there is a specific expected answer to check against. (Moxfield's `defaultCondition` behaviour is **inferred from its shipped client code, not documented** — treat it as the thing D4 confirms.)

---

## Tasks, in order

### D0 — The collection store (`Core/Collection`)
Implement `ICollectionStore` over the native CSV:
- **Commit** every tile whose `State` is `Included` or `ManuallySet`. Tile → row: `Chosen` gives `OracleId`/`OracleName`; `ManuallySet` → `Source = Manual`, `BestMatchDistance` null; `Included` → `Source = Hash`, `BestMatchDistance = ChosenDistance`.
- **Dedup on `OracleId` + `Condition`** → increment `Quantity`, update `LastScannedAt`. A blank condition is a value like any other.
- **Write via temp-file + atomic rename**, keeping a `.bak` of the previous file. UTF-8 with BOM.
- **Read through the native format's own parser**, which fails loudly on unknown or missing columns rather than mis-parsing.

Nine things D0 has to pin down that the list above leaves open — most of them ways user data goes silently wrong, which is this stream's stated risk #5:

0. **Two dedup bugs here are invisible through the commit path, and must be tested through a written FILE.** Measured on the Stream 0 stub 2026-09-21: `CommitCohortAsync` can never produce a non-null `Condition`, because `CohortTile` carries none and v1 never assesses it — so a bug conflating `null` with `""` in the dedup key passed **79 unrelated tests and was caught by none of them**. The same applies to the new `ArtworkId` fold: a commit-only suite cannot distinguish agree-or-null from last-write-wins. Drive both from a file the reader parses, and cover `null` vs `""` vs a real value explicitly.

1. **Normalise blank condition on both sides of the round trip.** `CollectionRow.Condition` is `string?` where null means *not assessed*, but a CSV blank field naturally parses back as `""`. If the committer produces `null` and the parser produces `""`, the dedup key `OracleId + Condition` stops matching and the same card silently becomes two rows with quantity 1. Pick one canonical in-memory representation — recommend **`null`**, with the parser mapping `""` → `null` — and assert the round trip in D3. This is the highest-probability data-corruption bug in the stream and it is invisible until someone counts their cards.
2. **Aggregate duplicates *within* a cohort, not just against the file.** A 3×3 capture of basic lands is the documented smoke-test path, so nine tiles sharing one `OracleId` is a first-run case, not an edge case. Commit must fold them into one `+9`, not nine sequential `+1`s against a re-read file.
3. **Define `CommitCohortAsync`'s return value.** The contract says "rows inserted or incremented"; with (2), committing nine Forests returns `1`, which is useless as the "committed N cards" number a UI would want to show. Decide which it is and write it down — this is a number that will end up in front of a user.
4. **Put the temp file in the target's own directory** — `Path.GetDirectoryName(target)`, never `Path.GetTempPath()` or `Path.GetTempFileName()`. This is the single highest-value correction in this review, because **the whole atomicity claim is contingent on it**. .NET's `File.Move` always passes `MOVEFILE_COPY_ALLOWED`, and `MoveFileEx` documents that "if the file is to be moved to a different volume, the function simulates the move by using the CopyFile and DeleteFile functions" ([MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw), [runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Windows/Kernel32/Interop.MoveFileEx.cs)). On Unix the same split exists as an `EXDEV` fallback to copy+delete ([runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs)). Collection on `D:` and temp on `C:` is an ordinary setup, so getting this wrong silently converts the atomic write into the exact copy-then-delete this design exists to avoid — with no error to notice.
5. **Call `FileStream.Flush(true)` before disposing the temp stream, then rename.** `Dispose` flushes to the OS, not to disk — only the `Flush(true)` overload routes through `FlushToDisk` ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush), [runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/OSFileStreamStrategy.cs)). Atomicity and durability are different properties: the rename buys "a reader sees the old file or the new one, never a truncated one", and buys nothing at all about surviving power loss. Free win on macOS — .NET's `FSync` tries `fcntl(F_FULLFSYNC)` first, which is what Apple's own `fsync(2)` page says you need.
6. **Use `File.Move(temp, target, overwrite: true)`, not `File.Replace`.** `File.Replace` looks like the better fit because it produces the `.bak` in one call, but it is worse here on three documented counts: it **cannot create the target on a first-ever write** (it throws `FileNotFoundException`, and first run has no `collection.csv`), its own docs describe it as a combination of several steps with observable intermediate failure states rather than a transaction, and its Unix implementation carries an explicit "these checks are not atomic" comment in the runtime source ([File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace), [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)). Produce the `.bak` with a separate copy of the outgoing file before the rename.
7. **`ListAsync` on a missing file returns empty, not an exception.** First run has no `collection.csv`. Unspecified today.
8. **Reject, loudly, an `Included` or `ManuallySet` tile whose `Chosen` is null.** The contract says `Chosen` is null only when `Unresolved`, so this is a broken invariant rather than a data case — throw instead of writing a row with an empty `OracleId`.
9. **Decide what happens when `collection.csv` is locked.** "Users can hand-fix a bad row in Excel" is an explicit selling point of the CSV decision, and Excel holds the file open on Windows — so "commit fails because the user is looking at their collection" is a designed-in case, not bad luck. The rename will throw. Surface it as a recoverable error that keeps the cohort intact so the user can close Excel and press Enter again; do not lose the cohort and do not fall back to a non-atomic write.

### D1 — The native format, through the same seam
Our own CSV store format **is the source of truth**, and every adapter projects *down* from it. It carries the full `CollectionRow` — `OracleId`, `OracleName`, `Quantity`, `Condition`, `LastScannedAt`, `BestMatchDistance`, `Source` — deliberately richer than any v1 adapter consumes, so the SOT never becomes the lossy bottleneck.

Implement it as an `ICollectionExporter` rather than a special case, so the most-used path shares code and tests with the adapters. UTF-8 **with BOM** or Excel mangles non-ASCII names. The header row's exact column set is the format version; a reader seeing unknown or missing columns must fail loudly rather than mis-parse.

> **Contract change, ruled 2026-09-21 before the fork: `CollectionRow` carries `ArtworkId`.** Appended last, `string?`, the Scryfall printing id of the art the hash matched. Set only on `Source.Hash` rows. **Null on `Manual` rows, and null whenever merged rows disagree** — agree-or-null, never last-write-wins, so that a non-null value is trustworthy. Full reasoning in `CONTRACTS.md` §Collection and export and in `RECONCILIATION.md`.
>
> Two consequences for this stream:
> - **The header set changed**, so D1's exact-header check and the format-version expectation must include it.
> - **Moxfield's output does not change in v1.** The adapter ignores the column. Emitting `Edition` and `Collector Number` from a resolved printing is a later adapter change, gated on B4a's single-printing measurement and a real verified import.

**Four mechanics this depends on, each verified:**

1. **`ExportAsync` must not dispose the caller's stream.** The contract hands the exporter a `Stream destination` it does not own, but `StreamWriter.Dispose` disposes the underlying stream unless you opt out — "unless you set the `leaveOpen` parameter to `true`, the `StreamWriter` object calls `Dispose()` on the provided `Stream`" ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamwriter.-ctor)). Use the `leaveOpen: true` overload in every adapter. Nothing in the contract says this, and it is the kind of bug that only shows up when the caller tries to write a second thing.
2. **The BOM only lands if the stream is at position 0.** `StreamWriter` writes the encoding preamble on first flush and skips it when `CanSeek && Position > 0` ([runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/StreamWriter.cs)) — so a fresh temp file is correct and an append would silently drop the BOM. Also note the `StreamWriter` constructors that take **no** encoding use UTF-8 *without* a BOM, which is the opposite of what we want: pass the encoding explicitly ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamwriter.-ctor)). `Encoding.UTF8` already emits the BOM; `new UTF8Encoding(true, true)` also throws on invalid sequences instead of substituting `?`.
3. **Read with BOM detection on, or the BOM becomes data.** `StreamReader` strips a leading preamble when the encoding you pass has one, or when `detectEncodingFromByteOrderMarks` is left at its default `true`. Get both wrong and the `EF BB BF` decodes as **U+FEFF at the start of the first field**, so `"﻿OracleId" != "OracleId"` and the format-version check fails — or worse, is "fixed" by someone trimming the header. Worth an explicit regression test given that this file is the source of truth.
4. **No CSV library gives you the "unknown columns" half of the guard.** Worth knowing before picking one: CsvHelper by default throws on a *missing* expected column (`HeaderValidated`) and on a missing field in a row (`MissingFieldFound`), but an **extra, unknown header column is silently ignored and there is no config flag that changes it** — you must compare `HeaderRecord` against the expected set yourself ([IReaderConfiguration](https://raw.githubusercontent.com/JoshClose/CsvHelper/master/src/CsvHelper/Configuration/IReaderConfiguration.cs), [ConfigurationFunctions](https://raw.githubusercontent.com/JoshClose/CsvHelper/master/src/CsvHelper/Configuration/ConfigurationFunctions.cs)). Its `DetectColumnCountChanges` also defaults to `false`. So the format-version guard is hand-written code whatever we depend on.

⚠️ **Pre-fork blocker: decide the CSV dependency before the streams fork.** Every `.csproj` is frozen once worktrees exist and the hook refuses the edit from inside one, so a stream that discovers it wants a package **stalls**. Stream D's plan currently names no CSV library at all. The good news is that nothing forces one: **`Microsoft.VisualBasic.FileIO.TextFieldParser` needs no `PackageReference`**, because `Microsoft.VisualBasic.Core` ships inside the `Microsoft.NETCore.App` shared framework ([NetCoreAppLibrary.props](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/NetCoreAppLibrary.props)), it targets net8.0/net9.0, it is usable from C#, it handles quoted fields with embedded commas and doubled quotes, and it throws `MalformedLineException` with the offending line number ([docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualbasic.fileio.textfieldparser), [source](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.VisualBasic.Core/src/Microsoft/VisualBasic/FileIO/TextFieldParser.vb)). One RFC deviation to guard: its `BeginQuotesRegex` accepts whitespace before an opening quote, which RFC 4180 says is field content. There is **no first-party `System.*` CSV API** in .NET 9 ([open request](https://github.com/dotnet/runtime/discussions/37711)). Per CLAUDE.md's "over-reference rather than under-reference", the safe move is for Stream 0 to add the `CsvHelper` reference anyway — an unused `PackageReference` costs nothing, and a missing one costs the stream.

**Writing is the easy half and should be hand-rolled regardless.** RFC 4180 is four rules: quote any field containing a comma, a double quote or CRLF; escape an embedded `"` by doubling it; spaces are field content and must not be trimmed; and — the one that matters here — **a field containing a `"` must be quoted even when it has no comma**, because item 5 forbids a bare `"` inside an unquoted field and the ABNF's `TEXTDATA` excludes `DQUOTE` outright ([RFC 4180 §2](https://www.rfc-editor.org/rfc/rfc4180.txt)). That is the authoritative version of the `"Rumors of My Death . . ."` case in D3.

Note the asymmetry this creates: the native export is lossless, and **every third-party adapter is lossy by definition** — they drop `OracleId`, `Source` and `BestMatchDistance` because those tools have nowhere to put them. That's fine and expected; it's also the reason the native format is what a user should keep.

### D2 — Two adapters, verified
~~Pick **two** targets and get them genuinely working: **Moxfield** and **ManaBox** are the highest-value pair (largest user bases, both accept name-keyed CSV import).~~

**Corrected: ManaBox does not accept name-keyed import, so the pair must change.** Recommended pair was **Moxfield + Deckbox** — the only two of the five tools researched with attested acceptance of name-only rows.

**→ Resolved: Moxfield only.** The user's call: one export format beyond the native SOT is enough for v1. That makes this the "ship Moxfield only, verified, plus the native format" option this review itself called defensible — two formats, both verified, instead of one verified and one resting on community folklore. Deckbox remains the strongest candidate if a second is ever added.

- **Moxfield — confirmed viable.** Header is `Count`, `Name`, `Edition`, `Condition`, `Language`, `Foil`, `Collector Number`, `Alter`, `Playtest Card`, `Purchase Price`; spelling and **case** must match exactly, no leading or trailing space in a header, and **column order is explicitly irrelevant**. Only `Name` is strictly required. `Edition` is a Scryfall **set code**, not a set name. Conditions are `M`/`NM`/`LP`/`MP`/`HP`/`D`|`DM`. ([Moxfield, *Importing a Collection*](https://moxfield.com/help/help-articles/importing-collection))
- **Deckbox — viable, with a caveat.** Quantity is `Count`, the header row is mandatory, and column order matters: `Count,Tradelist Count,Name,Edition,Card Number,Condition,Language,Foil,Signed,Artist Proof,Altered Art,Misprint,Promo,Textless,My Price`. `Count` and `Name` are the documented minimum, from the importer's own error text. Omitting `Edition` imports every row with no edition set — exactly the behaviour this stream wants. ([Deckbox importer error text](https://deckbox.org/forum/viewtopic.php?id=30026), [column set](https://deckbox.org/forum/viewtopic.php?id=30992), [conditions](https://deckbox.org/help/card_conditions)) **Caveat: the column list is community folklore** — Deckbox's only first-party import page covers encoding and nothing else, so cite it as community-documented and expect drift.
- **ManaBox — document as unsupported in v1.** Its minimum is card name plus set name or set code, or a `Scryfall ID`. We have none of those. ([ManaBox guide](https://www.manabox.app/guides/collection/import-export/))
- **Archidekt — document as unsupported in v1.** Blocks ambiguous uploads at configuration time. ([Archidekt](https://archidekt.com/news/5891613))

For each: exact header row, exact column order, quoting and escaping rules for names containing commas, apostrophes and accents — `Lim-Dûl's Vault`, `Borrowing 100,000 Arrows`, `Kongming, "Sleeping Dragon"` are the test cases that break naive CSV writers. Cite the source for each spec in a comment; these are **community-documented formats, not versioned APIs**, so they drift.

All three names are exact real oracle names, verified against Scryfall on 2026-09-21 — including the `û` in `Lim-Dûl's Vault`, which the doc had right. Scale, from Scryfall regex searches: **4,014** oracle names contain a comma, **134** contain a non-ASCII character, **12** contain an embedded double quote. Escaping is not an edge case in this catalog.

⚠️ **The three named vectors miss the worst class.** Five oracle names *begin* with a double quote — `"Ach! Hans, Run!"`, `"Brims" Barone, Midway Mobster`, `"Lifetime" Pass Holder`, `"Name Sticker" Goblin`, `"Rumors of My Death . . ."` — and **`"Rumors of My Death . . ."` contains no comma at all** ([`name:/^"/`](https://api.scryfall.com/cards/search?q=name%3A%2F%5E%22%2F)). A writer that quotes only when it sees a comma emits that name bare, and a conforming reader then parses the leading `"` as the start of a quoted field and mis-reads the row. `Kongming, "Sleeping Dragon"` does *not* catch this, because its comma triggers the naive writer's quoting anyway. **Add `"Rumors of My Death . . ."` as a required vector in D3.**

### D3 — Round-trip and escaping tests
Pure-function tests, no I/O fixtures needed beyond in-memory streams:
- names with `,` `"` `'` and non-ASCII survive a write→parse round trip
- an empty collection produces a valid header-only file, not an empty file
- quantity aggregation is preserved
- blank printing and condition columns are emitted as empty fields, not the literal `null`
- the store: commit increments rather than duplicates; an `Excluded` tile writes nothing; a `ManuallySet` tile writes `Source = Manual` with no distance; a crash between temp-write and rename leaves the previous file intact

Required test vectors, all verified as real oracle names against Scryfall on 2026-09-21:

| Name | The failure it catches |
|---|---|
| `Kongming, "Sleeping Dragon"` | comma **and** embedded quotes in one field |
| `"Rumors of My Death . . ."` | **leading quote with no comma** — the case a quote-only-if-comma writer emits bare and any conforming reader then mis-parses. The doc's original three vectors all miss this. |
| `Lim-Dûl's Vault` | non-ASCII plus an apostrophe |
| `Borrowing 100,000 Arrows` | comma inside a number, which also tempts a locale-aware number parser |
| `+2 Mace` | leading `+`. See the Excel note below. |

**Two corrections to the last bullet:**

- **"A crash between temp-write and rename" is not testable as written** — a unit test cannot crash the process, and the atomicity of the rename is an OS property we neither implement nor can assert. Test what is actually ours: that the temp file is created in the target's own directory, that the original file is byte-identical until the rename, and that a write which throws mid-serialisation leaves both the original and the `.bak` intact. State the OS rename guarantee as an assumption with its source, not as a test.
- **Add the blank-vs-empty-string dedup round trip** from D0 item 1 — commit a row with a null condition, read it back, commit the same card again, and assert **one** row with quantity 2 rather than two rows with quantity 1. That is the assertion that catches the silent-duplication bug; the existing "quantity aggregation is preserved" bullet does not, because it never crosses a parse boundary.

**Excel formula injection — know it, don't "fix" it.** `+2 Mace` is a real card (Adventures in the Forgotten Realms, so squarely inside v1's Modern-frame scope) and is the only oracle name that begins with an Excel formula character ([`name:/^[-+=@]/`](https://api.scryfall.com/cards/search?q=name%3A%2F%5E%5B-%2B%3D%40%5D%2F)). Excel will try to evaluate that cell. The usual mitigation — prefixing a `'` or tab — would corrupt the source of truth for every machine reader, so **do not sanitise**: the native format's job is to be correct, and the fix belongs in the README as a known Excel display quirk. The test that matters is that our own parser round-trips `+2 Mace` unchanged, and that no mitigation prefix ever reaches the file.

### D4 — Manual import verification ⚠️ the part that actually matters
**Import at least one generated file into the real tool and confirm it lands correctly.** An adapter that has never been imported is a guess with a test suite around it.

This needs an account on the target and is a manual step — it cannot be automated or CI'd. Record the result (tool, date, row count, what it resolved printings **and blank conditions** to) in the README, so users know what's verified and what isn't.

### D5 — Additional formats, if time allows
~~Archidekt, Deckbox, Dragon Shield. Each is ~30 lines once the seam exists.~~

**Corrected: this list does not survive contact with the three tools.** The "~30 lines once the seam exists" estimate is right about the *code* and wrong about the *targets* — two of the three cannot be targeted at all from name-only rows:

- **Deckbox** — viable, and promoted into D2 above as the recommended second adapter.
- **Archidekt** — refuses ambiguous uploads, so not viable without fabricating a printing. Also has **no fixed import header by design**: it removed its own preset because "our exporter is dynamic… a hard coded list of what we expect for columns ended up being wrong more often than it was right" ([Archidekt staff](https://archidekt.com/forum/thread/15700538)). An "Archidekt adapter" is close to a category error — the user maps columns at import time.
- **Dragon Shield** — **no first-party import documentation exists at all**; no help, FAQ or support route on the site, and community sources disagree about whether the app even has a CSV import. Its condition vocabulary (`Mint, Near Mint, Excellent, Good, Light Played, Played, Poor`) is only recoverable from the web app's own locale file. Do not ship this adapter without importing into the real app first, which is precisely what D4 demands and what makes it a poor stretch target.

So the realistic stretch list is **Deckbox and nothing else** until someone has accounts to test against. That is a better outcome than three adapters nobody imported.

**Two verified adapters beat six unverified ones.** Claiming six and having none actually imported is worse than claiming two, because the first failed import is the last time a user trusts the tool.

---

## Done when

- `ICollectionStore` commits cohorts with `OracleId` + condition dedup, atomic writes and a `.bak`.
- The native CSV format is implemented as an `ICollectionExporter`, and the store uses it.
- **Two** adapters implemented with exact headers and correct escaping.
- **At least one** verified by a real import into the live tool, with the result recorded in the README.
- Escaping tests cover comma, quote, apostrophe and non-ASCII names.
- Printing columns are blank rather than fabricated, and the README states the limitation.
- ~~The UI's format picker enumerates whatever exporters exist, with no per-format code in `LoreFetch.App`.~~ **Not stream D's criterion.** `LoreFetch.App` is on this stream's *must not touch* list, so D can neither implement nor test this — it is satisfied or broken entirely inside stream A. What D owes the seam is that `ExportFormat` carries everything a picker needs (`DisplayName`, `FileExtension`, `IsVerified`, `Notes`) and that every exporter is registered so an `IEnumerable<ICollectionExporter>` injection sees it. Keep the intent, move the verification to A or to integration; a done-when another stream owns is a done-when nobody checks.
- The blank-condition dedup round trip holds: commit → write → parse → commit the same card again yields one row with quantity 2.

## Fallbacks

- **A target format can't import name-only rows:** document it as unsupported rather than fabricating set codes to satisfy the parser. State it in the README. **This fallback has already fired** — research found ManaBox and Archidekt both require a printing, so they are unsupported in v1 by design rather than by omission. The fallback working as intended is the reason D2's target pair changed.
- **The BOM is a per-adapter decision, not a global one.** Our native format keeps UTF-8 **with** BOM, because Excel needs it and the native file is the one users open. Third-party adapters should be written **without** a BOM: Deckbox has a credible tested community report of a BOM breaking the header match, with the tell-tale signature of only the *first* column being unrecognised ([report](https://deckbox.org/forum/viewtopic.php?id=30026)), while its own first-party page asks for UTF-8 ([Deckbox help](https://deckbox.org/help/exports_and_imports)); Moxfield's tolerance is undocumented but its importer insists headers match exactly "including case" and warns about stray characters around them, so a BOM welded to `Count` is a plausible break; and ManaBox writes no BOM in its own exports. Default the adapters to BOM-less and let D4's real import settle it.
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

For the pre-build stream review (see [`docs/history/stream-review-directions.md`](../history/stream-review-directions.md)). Check each against primary sources — each tool's own import documentation, not blog posts — and record what you found, citing the source.

1. **Moxfield and ManaBox import specs.** Exact headers, column order, required vs optional columns, quoting rules. Does each accept **name-only** rows (no set, no collector number)?
2. **Blank condition.** What does each tool do on import when the condition column is empty — default to NM, reject the row, or reject the file?
3. **Atomic rename on Windows.** Is `File.Move(temp, target, overwrite: true)` (or `File.Replace`) actually atomic on NTFS? This doc and `CONTRACTS.md` assert it; confirm or correct.
4. **UTF-8 BOM.** Does each target tool's importer tolerate a BOM? A BOM that fixes Excel but breaks an importer is a per-adapter decision, not a global one.
5. **The seam.** Can the store do everything in D0 with only `Cohort`, `CohortTile` and `CollectionRow` as the contract defines them?

---

## Plan review findings — 2026-09-21

Research targets 1–4 were worked against each tool's own documentation, Microsoft Learn, the .NET runtime source, RFC 4180 and the Scryfall API. Target 5 was worked against `CONTRACTS.md` directly. Every claim below carries its source; where a primary source does not exist, it says so rather than guessing — several of these formats are community folklore and saying so is the finding.

### Verified

- **All three of D2's escaping test vectors are exact real oracle names**, including the `û` in `Lim-Dûl's Vault` — [Scryfall `/cards/named`](https://api.scryfall.com/cards/named?fuzzy=Kongming)
- **Scale of the escaping problem:** 4,014 oracle names contain a comma, 134 contain a non-ASCII character, 12 contain an embedded double quote — [Scryfall search](https://api.scryfall.com/cards/search?q=name%3A%2F%2C%2F)
- **Moxfield accepts name-only rows.** Only `Name` is strictly required; column order is explicitly irrelevant; header spelling and case must match exactly — [Moxfield, *Importing a Collection*](https://moxfield.com/help/help-articles/importing-collection)
- **Moxfield's condition vocabulary** is `M`/`NM`/`LP`/`MP`/`HP`/`D`|`DM`, and the column is optional — same source
- **Deckbox accepts name-only rows**, importing them with no edition set; `Count` + `Name` are the documented minimum, from the importer's own error text — [Deckbox forum](https://deckbox.org/forum/viewtopic.php?id=30026), [conditions](https://deckbox.org/help/card_conditions)
- **`rename(2)` replacing an existing file is atomic on macOS/APFS**, documented at namespace level: "guarantees that an instance of new will always exist, even if the system should crash in the middle of the operation" — shipped `man 2 rename`, and [POSIX](https://pubs.opengroup.org/onlinepubs/9699919799/functions/rename.html) for the concurrency half
- **.NET's `File.Move(overwrite: true)` maps to `MoveFileExW` on Windows and bare `rename()` on Unix** — [runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Windows/Kernel32/Interop.MoveFileEx.cs)
- **An atomic rename is not durability.** It guarantees a reader sees old-or-new, never truncated; surviving power loss needs `FileStream.Flush(true)`, which alone routes through `FlushToDisk` — [docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush). On macOS .NET's `FSync` tries `F_FULLFSYNC` first, which is what Apple's own `fsync(2)` page says is required.
- **`TextFieldParser` needs no `PackageReference`** — `Microsoft.VisualBasic.Core` ships in the `Microsoft.NETCore.App` shared framework, targets net8.0/net9.0, is usable from C#, and handles quoted fields and doubled quotes — [NetCoreAppLibrary.props](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/NetCoreAppLibrary.props), [docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualbasic.fileio.textfieldparser)
- **RFC 4180 requires quoting any field containing a `"`, comma or CRLF**, and escaping an embedded `"` by doubling it — [RFC 4180 §2](https://www.rfc-editor.org/rfc/rfc4180.txt)
- **`Encoding.UTF8` emits the BOM; `StreamWriter` with no encoding does not.** The preamble is written on first flush and skipped when the stream position is non-zero — [docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamwriter.-ctor), [runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/StreamWriter.cs)
- **The seam holds for D0.** Every field of `CollectionRow` is reachable from `Cohort` + `CohortTile` as frozen: `Chosen` gives `OracleId`/`OracleName`, `State` gives `Source`, `ChosenDistance` gives `BestMatchDistance`, `Cohort.CapturedAt` gives `LastScannedAt`, `Condition` is always null in v1 and `Quantity` is derived. No contract change is needed to build the store.

### Corrected

- **"Moxfield and ManaBox … both accept name-keyed CSV import"** → Moxfield does; **ManaBox does not.** ManaBox documents a hard minimum of card name *plus* set name or set code, or a `Scryfall ID`, because "the app needs to know some way of distinguishing different versions of the same card" — [ManaBox guide](https://www.manabox.app/guides/collection/import-export/). D2's target pair had to change; recommended replacement is Deckbox.
- **Moxfield's quantity column is `Count`, not `Quantity`** — [Moxfield help](https://moxfield.com/help/help-articles/importing-collection). Same for Deckbox.
- **Archidekt's identity columns** → it has **no fixed import header at all**; import is user-mapped columns and Archidekt deliberately deleted its own preset because "a hard coded list of what we expect for columns ended up being wrong more often than it was right" — [Archidekt staff, own forum](https://archidekt.com/forum/thread/15700538). It also blocks ambiguous uploads outright — [release post](https://archidekt.com/news/5891613).
- **D5's "Archidekt, Deckbox, Dragon Shield, each ~30 lines"** → only **Deckbox** is viable from name-only rows. Archidekt refuses them; **Dragon Shield has no first-party import documentation whatsoever** — no help or support route exists on its site, and community sources disagree on whether it has a CSV import at all.
- **D2's escaping vectors miss the worst case.** Five oracle names *begin* with a double quote, and `"Rumors of My Death . . ."` contains **no comma** — [Scryfall](https://api.scryfall.com/cards/search?q=name%3A%2F%5E%22%2F). A quote-only-on-comma writer emits it bare and every conforming reader then mis-parses the row. Added as a required vector.
- **"Atomic on NTFS"** (asserted in this doc, `CONTRACTS.md` and `CLAUDE.md`) → **undocumented, not false.** The `MoveFileEx` page never uses the word "atomic" and neither promises nor disclaims it; `ReplaceFile` is *also* not documented as atomic and is explicitly described as a multi-step operation with observable intermediate failure states — [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw), [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew). Widely relied upon, but the docs are silent; soften the claim rather than repeating it as documented fact.
- **The temp file's location was unspecified, and the whole atomicity claim depends on it.** .NET always passes `MOVEFILE_COPY_ALLOWED`, and `MoveFileEx` documents that a cross-volume move "simulates the move by using the CopyFile and DeleteFile functions"; Unix takes an equivalent `EXDEV` copy+delete fallback. The temp file must live in the target's own directory, never `Path.GetTempPath()`.
- **`File.Replace` is the wrong primitive here**, despite producing the `.bak` in one call: it cannot create the target on a first write, and its Unix implementation carries an explicit "these checks are not atomic" comment in the runtime source. (Also corrected a stale belief of my own: `File.Replace` is **not** `PlatformNotSupportedException` on modern Unix — it is fully implemented.)
- **Blank condition round-trips as `""`, not `null`** — unstated, and the highest-probability silent data-corruption bug in the stream, since `""` will not dedup against `null`. Now specified in D0 with a required test in D3.
- **Within-cohort duplicate aggregation was unspecified** — nine tiles of one basic land is the documented smoke-test path, not an edge case.
- **`ExportAsync` disposing the caller's stream** — unstated; requires the `leaveOpen: true` overload in every adapter.
- **"A crash between temp-write and rename leaves the previous file intact" is not testable as written** — a unit test cannot crash the process, and the rename's atomicity is an OS property we do not implement. Reframed around what is actually ours.
- **"The UI's format picker enumerates whatever exporters exist" is not stream D's done-when** — `LoreFetch.App` is on D's *must not touch* list, so D can neither implement nor verify it.
- **No CSV library satisfies "fail loudly on unknown columns."** CsvHelper throws on *missing* expected columns by default but **silently ignores extra ones, with no config flag to change it**; `DetectColumnCountChanges` defaults to `false`. The format-version guard is hand-written code either way — [ConfigurationFunctions](https://raw.githubusercontent.com/JoshClose/CsvHelper/master/src/CsvHelper/Configuration/ConfigurationFunctions.cs).

### Proposed contract changes

- **`ICollectionExporter.ExportAsync` / `CONTRACTS.md` "Collection and export"**: the blanket statement "UTF-8 **with BOM**, or Excel mangles non-ASCII card names" should be scoped to the **native format only**, with the BOM made an explicit per-adapter choice. **Why:** the native file is the one users open in Excel, so it keeps the BOM; but Deckbox has a tested community report of a BOM breaking its header match, Moxfield's importer demands exact header matching and its BOM tolerance is undocumented, and ManaBox emits no BOM in its own exports. A BOM that fixes Excel and breaks an importer is a per-format decision. No signature change — `ExportAsync` already writes bytes into a caller-supplied stream, so each adapter can already choose; the contract's prose just needs to stop mandating one answer. **Effect on other streams:** unknown — for reconciliation.
- **`CollectionRow.Condition`**: document the canonical representation of "not assessed" as **`null`**, with a stated rule that a blank CSV field parses to `null` and `null` serialises to an empty field. **Why:** `Condition` is half the dedup key, and `""` vs `null` silently splits one card into two rows. The type already allows both; the contract does not say which one means "not assessed", and the store and the parser are the two places that must agree. **Effect on other streams:** unknown — for reconciliation.
- **Stream D's *Consumes* line** (in this doc and in `CONTRACTS.md`'s stream boundary table): add **`OracleEntry`**, **`TileState`** and **`RowSource`**. **Why:** D0 cannot be written without them — `CohortTile.Chosen` is an `OracleEntry?`, the commit filter switches on `TileState`, and `Source` is a `RowSource`. All three are already in `Core/Abstractions`, so this is an enumeration gap rather than a new dependency, but the *Consumes* line is what the freeze is checked against. **Effect on other streams:** unknown — for reconciliation.
- **`ICollectionStore.CommitCohortAsync`**: pin down the documented meaning of the return value — "rows inserted or incremented" versus cards committed. **Why:** once within-cohort duplicates are folded (D0 item 2), committing nine Forests returns `1`, which is not the number a UI would want to show a user. **Effect on other streams:** unknown — for reconciliation, since stream A is the caller.

### Open questions

- **Which second adapter replaces ManaBox?** ManaBox cannot accept our name-only rows, so D2's "highest-value pair" no longer exists as written. **Recommendation: Moxfield + Deckbox**, and document ManaBox and Archidekt in the README as unsupported-by-design with the one-line reason. Deckbox is the only other researched tool that provably accepts name-only rows and leaves the printing unset — the exact behaviour this stream wants. The cost is honest: Deckbox's column spec is community folklore (its only first-party import page covers encoding), and it has a credible BOM-breaks-the-header report, so it needs D4's real import more than Moxfield does. The alternative — ship **Moxfield only**, verified, plus the native format — is also defensible and matches "two verified adapters beat six unverified ones" taken one step further. I would not pick Archidekt or Dragon Shield under any time budget.
- **Should Stream 0 add a `CsvHelper` reference to the .csproj before the fork?** **Recommendation: yes**, even though I expect stream D not to need it. `.csproj` files are frozen and hook-enforced once worktrees exist, so a stream that discovers it wants a package stalls, and CLAUDE.md's own guidance is to over-reference because an unused `PackageReference` costs nothing. The technical answer is that no package is required — `TextFieldParser` is in the shared framework and an RFC 4180 writer is a few lines — and no library provides the unknown-column guard anyway. This is a cheap insurance question, not a design question. CsvHelper is dual MS-PL **or** Apache-2.0, so the Apache-2.0 option is GPLv3-compatible.
- **What should the store do when `collection.csv` is locked by Excel?** The CSV decision explicitly sells hand-editing in Excel, and Excel holds the file open on Windows, so the rename will throw during ordinary use. **Recommendation: surface it as a recoverable error that leaves the pending cohort intact**, so the user closes Excel and presses Enter again — never discard the cohort, and never fall back to a non-atomic in-place write to make the error go away. Needs a decision because it is the one failure the UI must render, and that makes it stream A's problem too.
- **What should the reader do about duplicate `OracleId` + `Condition` rows in the file?** The format-version rule covers unknown and missing *columns* but says nothing about duplicate keys, which hand-editing in Excel makes likely (a pasted row, a sorted-and-re-saved file). **Recommendation: merge duplicates on read — sum `Quantity`, keep the latest `LastScannedAt` — and log it, rather than failing loudly.** Failing loudly here locks a user out of their own collection after an innocent paste, which is a worse outcome than a logged merge; the file is the source of truth and the app should be able to heal it. This is a deliberate exception to the "fail loudly" rule, which is why it needs the user's call rather than mine.
- **Should the native format's Excel-facing quirks be documented or mitigated?** `+2 Mace` is a real in-scope card and the only oracle name starting with an Excel formula character; Excel will try to evaluate that cell. **Recommendation: document, never sanitise** — a `'` or tab prefix would corrupt the source of truth for every machine reader to fix a display artefact in one program. Flagging it because "it looks broken in Excel" is exactly the kind of report that tempts a later fix in the wrong layer.

# LoreFetch

A webcam collection scanner for Magic: The Gathering: cards laid on a desk are identified by image and recorded into a collection file the user owns.

## Language

### Cards

**Oracle card**:
A card as a rules object, independent of which printing it came from; what LoreFetch identifies.
_Avoid_: card (when the printing is ambiguous), card name

**OracleId**:
Scryfall's stable identifier for an oracle card; the identity key everywhere in LoreFetch.
_Avoid_: card ID, Scryfall ID (that identifies a printing)

**Oracle name**:
The human-readable name of an oracle card; display only, never an identity key.

**Printing**:
One specific release of an oracle card (set, collector number); v1 cannot distinguish printings that share art.
_Avoid_: edition, version

**Condition**:
A card's physical grade (NM, LP, MP, HP, DMG); unset means not assessed, and v1 never assesses it.
_Avoid_: quality, grade

**Oracle catalog**:
The full list of oracle cards LoreFetch can identify, as carried by the hash index.
_Avoid_: card database, name list

### Scanning

**Capture**:
Taking the cards currently detected and turning them into a cohort. Manual (Space) or auto (count-gated settle).
_Avoid_: scan, snapshot

**Cohort**:
The set of cards from one capture, awaiting the user's accept or discard.
_Avoid_: batch, scan, grid

**Tile**:
One card within a cohort: its image, ranked candidates, chosen card and state.
_Avoid_: slot, cell

**Candidate**:
One possible identification for a tile, ranked by match distance.
_Avoid_: match, guess, result

**Match distance**:
Hamming distance between a tile's fingerprint and a reference fingerprint; lower is closer.
_Avoid_: score, confidence

**Good threshold / Ok threshold**:
The two calibrated match distances: below good is a confident match, between good and ok is a low-confidence match that is highlighted, above ok is unresolved.
_Avoid_: confidence level

**Unresolved**:
A tile whose best candidate is beyond the ok threshold, so nothing is proposed.

**Excluded**:
A tile the user has X'd out of its cohort; it will not be committed.
_Avoid_: rejected, removed

**Manually set**:
A tile whose chosen card was picked by the user rather than proposed by the hash.
_Avoid_: overridden, corrected

**Clear**:
Reverting a manually set tile to the hash's own proposal (or to unresolved if there was none).
_Avoid_: reset, remove

**Commit**:
Accepting a cohort (Enter): every included and manually set tile is written to the collection.
_Avoid_: save, accept (as a noun)

**Discard**:
Dropping a cohort (Escape) with nothing written.
_Avoid_: cancel, clear

### Collection

**Collection**:
The user's inventory, held in the native format; the source of truth.
_Avoid_: database, library, inventory file

**Native format**:
LoreFetch's own collection CSV, carrying every field known at commit time; every export projects down from it.
_Avoid_: internal format, save format

**Export adapter**:
A projection of the collection into a third-party tool's import shape; lossy by definition.
_Avoid_: exporter plugin, converter

**Verified adapter**:
An export adapter whose output has actually been imported into its live target tool.

### Build process

**Domain**:
One area of the codebase with its own code and its own test project: App, Detection, Identification, Lab, Capture, Collection. See the domain map in `docs/CONTRACTS.md`.
_Avoid_: stream (except when talking about the v0.1 build), module, layer

**Stream** *(historical)*:
One of the four parallel workstreams that built v0.1 (A UI, B Identification, C Capture, D Collection & export). Retired after v0.1.0; use it only when citing the v0.1 build record.
_Avoid_: track, workstream, team

**Stream 0** *(historical)*:
The serial foundation pass that created every project, the contract surface and the fakes before the v0.1 streams forked.
_Avoid_: setup, bootstrap

**Contract surface**:
The shared code every domain builds against: `Core/Abstractions`, `Core/Scanning` and `Core/Fakes`. Changes here affect every domain at once, so they are made deliberately and called out in the PR.
_Avoid_: interfaces, API, shared code

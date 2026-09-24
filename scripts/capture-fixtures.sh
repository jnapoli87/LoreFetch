#!/bin/sh
#
# LoreFetch — capture one labelled H3 fixture-corpus cell.
#
# H3 needs heights 8/10/12/14/20" x layouts 1/3/9, across lands/normal/
# stretch cards, on light/mid/dark mats. Today the only capture path is
# running the Stream C hardware test by hand, finding the PNG it drops in
# %TEMP%\lorefetch-hw, then copying and renaming it manually. The real risk
# there is MISLABELLING, not typing effort: a frame filed under the wrong
# height, or paired with the wrong ground-truth row, silently corrupts B6's
# accuracy table. This script exists to make that failure loud instead.
#
# One invocation captures exactly one cell: it runs only the single
# frame-saving Hardware test (never the sustained-memory, slow-consumer or
# interactive-unplug tests), files the frame under a deterministic name in
# test-images/fixtures/<height>in/<layout>/, and appends one ground-truth
# row per card slot. If the capture fails or produces no frame, nothing is
# appended — a ground-truth row without its frame is corruption.
#
# Usage:
#   scripts/capture-fixtures.sh --height <in> --layout <n> --mat <mat> \
#     --rung <rung> --card <name> [--card <name> ...] [--orientation <o>] \
#     [--catalog <path>] [--force] [--dry-run]
#   scripts/capture-fixtures.sh --verify <ground-truth.csv> [--catalog <path>]
#
# Required (capture mode):
#   --height <in>   a number of inches, 6..30 (decimals OK, e.g. 9.75 or
#                    13.5) — see compute_fit below for the real gate
#   --layout <n>    one of: 1 3 9 — also the number of --card values required
#   --mat <mat>     one of: light mid dark
#   --rung <rung>   one of: land normal stretch
#   --card <name>   the oracle name for one slot. Repeat in slot order —
#                   the FIRST --card is slot 1, the second is slot 2, etc.
#                   Must appear exactly <layout> times. Written to
#                   ground-truth.csv verbatim and RFC-4180-quoted — commas,
#                   quotes and accents are all fine (only a literal
#                   newline/CR is refused, since it cannot be represented).
#                   Checked against the Scryfall oracle catalog when one is
#                   available (see --catalog); case-insensitive, but the
#                   CSV always gets the catalog's canonical spelling.
#
# Options:
#   --orientation <o>  one of: portrait rotated. Only layout 9 (a 3x3 grid)
#                      has two real arrangements — PORTRAIT keeps the
#                      grid's 7.7" side on the frame's binding axis (the
#                      stricter case) and ROTATED puts its 10.7" side there
#                      instead (this script's arrangement before this flag
#                      existed). Layouts 1 and 3 accept the flag but have
#                      only one footprint each, so it changes nothing for
#                      them. Defaults to portrait — the stricter case, and
#                      the project's chosen arrangement — because a default
#                      that under-reports risk is the wrong default. The
#                      OTHER orientation's margin is always printed too, as
#                      information, for layout 9.
#   --catalog <path>  the oracle-name manifest to validate --card values
#                      (or --verify's csv) against. Default:
#                      scryfall-bulk/filtered-artworks.jsonl in the repo.
#                      Missing manifest, or no `jq` to parse it, WARNS and
#                      proceeds unvalidated rather than refusing to shoot.
#   --verify <csv>     re-check an existing ground-truth.csv's oracle_name
#                      column against the catalog, report unknown names by
#                      line number, and exit — captures nothing.
#   --force            overwrite an existing frame for this exact cell, and
#                       replace (rather than duplicate) its ground-truth rows
#   --dry-run           validate everything and print what would be filed,
#                       touching neither the camera nor the filesystem
#
# Examples:
#   scripts/capture-fixtures.sh --height 10 --layout 1 --mat light \
#     --rung land --card Forest
#
#   scripts/capture-fixtures.sh --height 14 --layout 3 --mat dark \
#     --rung normal --card "Lightning Bolt" --card Counterspell \
#     --card "Birds of Paradise" --dry-run
#
#   scripts/capture-fixtures.sh --verify test-images/ground-truth.csv
#
# This runs the real capture only on the Windows PC (the C920 is attached
# there); --help, --dry-run and --verify work anywhere.
#
# Height/layout are cross-checked against each other, not just against
# --height's own numeric range: the C920's frame at height h covers only
# (1920/ppi) x (1080/ppi) inches (ppi = 1360/h), so a 3x3 grid's 7.7"x10.7"
# footprint does not fit at every height in that range — AND, for layout 9,
# which of the grid's two sides is on the binding axis depends on
# --orientation, so the same height can both fit and fail depending on it
# (12" fits rotated, fails portrait). A combination whose margin is
# negative is refused outright, and one under 0.5" is filed with a warning
# — the frame looks fine either way, which is exactly what makes a bad fit
# a silent-corruption risk rather than an obvious one.
#
# NOT implemented (follow-up once stream/b merges): a cross-check of the
# REQUESTED height against the frame's own content, by deriving the true
# height from a detected card's pixel width (height = 1360 * 2.5 /
# card_pixel_width). That needs Lab's `detect` command, which lives on
# stream/b and has not merged to main — this script only checks the
# requested height against geometry, never against what the camera saw.

set -eu

# --------------------------------------------------------------------------
# Locate the repo from the script's own path, so it works from any cwd.
# --------------------------------------------------------------------------
script_dir=$(cd "$(dirname "$0")" && pwd -P)
REPO=$(cd "$script_dir/.." && pwd -P)

die()  { printf 'capture-fixtures: %s\n' "$1" >&2; exit 1; }
say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

LAYOUTS="1 3 9"
MATS="light mid dark"
RUNGS="land normal stretch"
ORIENTATIONS="portrait rotated"

# --height has no fixed allowlist. compute_fit() below is the real guard —
# it is strictly better validation than any fixed set of "sensible" heights,
# because it refuses combinations that physically cannot work (negative
# margin) and warns under 0.5" of margin, rather than requiring a list that
# has to grow every time the bench needs a new height. Only a sane NUMERIC
# range is checked up front (MIN_HEIGHT/MAX_HEIGHT below); everything past
# that is compute_fit()'s job.
#
# 9.75" is the clearest illustration of why the range alone was never the
# real guard: it is where a 3x3 grid FIRST fits at all (0.04" of margin —
# one millimetre), a geometric floor rather than a usable operating height.
# It is now ACCEPTED by the range check, and that's correct — compute_fit()
# surfaces the WARN and tells the operator the real reason, which is a
# better outcome than a blanket refusal. The project's recorded operating
# height is 12" (1.83" of margin) — see compute_fit below.

# Sane numeric bounds for --height, inches. Below ~6" not even the smallest
# footprint (1 card, 2.5"x3.5" — CLAUDE.md's Geometry table) fits
# comfortably. Above ~30" a card is under ~113px wide (1360/30 * 2.5 =
# 113.3) — well past anything useful for hashing or for a human to frame by
# hand. Anything inside this range is only a candidate; compute_fit() still
# decides per layout.
MIN_HEIGHT=6
MAX_HEIGHT=30

CAPTURE_TESTS_CSPROJ="$REPO/Tests/Capture/LoreFetch.Tests.Capture.csproj"
# Narrow enough to select exactly ONE test: the single frame-saving Hardware
# test, never SustainedRunKeepsMemoryFlat (~3 min), SlowConsumerSeesLatency-
# NotGrowth (~60s) or UnplugCameraTests (interactive). Verified with
# `dotnet test ... --filter "<this>" --list-tests`, which needs no camera.
FILTER="FullyQualifiedName~HardwareCameraTests.Negotiates1080pMjpgAndDeliversLiveFrames"

HEIGHT=""; LAYOUT=""; MAT=""; RUNG=""; FORCE=0; DRY_RUN=0
# Defaults to "portrait": it is both the STRICTER of the two arrangements
# (see layout_footprint below) and the project's chosen one, and a default
# that under-reports risk is the wrong default.
ORIENTATION="portrait"
CARDS=""
VERIFY_CSV=""
CATALOG_FILE="$REPO/scryfall-bulk/filtered-artworks.jsonl"

# Isolated single-character NL/CR, built via command substitution (which
# strips only a TRAILING newline, never an embedded one) and trimmed on
# both sides. Used both to reject a literal newline/CR in a --card value
# (below) and, later, by csv_quote_field's RFC 4180 quoting.
NL=$(printf 'x\nx'); NL=${NL#x}; NL=${NL%x}
CR=$(printf 'x\rx'); CR=${CR#x}; CR=${CR%x}

add_card() {
  # A literal newline/CR must be rejected HERE, before it ever joins
  # CARDS — CARDS itself uses a bare newline as the slot separator (see
  # the split below), so a card name containing one would silently split
  # into two slots. It would also make one ground-truth row span more
  # than one physical line, which breaks --force's line-based grep
  # dedup. Comma and double-quote are fine now — see csv_quote_field.
  case "$1" in
    *"$NL"*|*"$CR"*)
      die "--card '$1' contains a literal newline or carriage return, which this script cannot represent: ground-truth.csv is line-oriented (--force's row replacement greps by whole line) and --card values are joined internally on newline" ;;
  esac
  if [ -z "$CARDS" ]; then CARDS=$1; else CARDS="$CARDS
$1"; fi
}

in_set() {  # $1 = value, $2 = space-separated allowed set
  needle=$1; hay=$2
  for x in $hay; do [ "$x" = "$needle" ] && return 0; done
  return 1
}

# Numeric range check for --height. The case match rejects anything that
# isn't digits-and-at-most-one-dot (letters, signs, a bare ".", a leading
# or trailing dot, two dots) using a bracket-class glob rather than reaching
# for a regex tool that might not be on PATH; awk then does the actual
# float comparison against MIN_HEIGHT/MAX_HEIGHT; because /bin/sh only has
# integer arithmetic and this must accept 9.75 and 13.5.
in_height_range() {  # $1 = value
  v=$1
  case "$v" in
    ''|*[!0-9.]*|*.*.*|.|*.|.*) return 1 ;;
  esac
  awk -v v="$v" -v lo="$MIN_HEIGHT" -v hi="$MAX_HEIGHT" \
    'BEGIN { exit !(v + 0 >= lo && v + 0 <= hi) }'
}

slugify() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//'
}

# Windows-native path for a value handed to a .NET process under Git Bash —
# the inverse of converting a Windows path to a Git Bash one.
native_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# Resolves layout + orientation to a short x long footprint, inches, from
# CLAUDE.md's Geometry table (1 card 2.5x3.5, 3-in-a-line 2.5x10.7, 3x3
# grid 7.7x10.7 or its swap). Only layout 9 (a 3x3 grid) has two real
# arrangements, because it is the only footprint whose two sides differ:
# PORTRAIT puts the grid's shorter 7.7" side on the frame's short (binding)
# axis — the stricter case — while ROTATED puts its longer 10.7" side
# there instead; ROTATED reproduces exactly the fs=7.7/fl=10.7 pair this
# script always used before --orientation existed. Layouts 1 and 3 accept
# an orientation argument for a uniform CLI, but do NOT get a second
# footprint — a single card and a straight line of 3 have only one real
# arrangement, so inventing a second pair for them would be validation
# theatre. Layout 3's footprint is deliberately left as 2.5x10.7 either
# way, per the orchestrator's ruling.
layout_footprint() {  # $1 = layout, $2 = orientation ("portrait"|"rotated")
  case "$1" in
    1) printf '2.5 3.5\n' ;;
    3) printf '2.5 10.7\n' ;;
    9)
      case "$2" in
        rotated)  printf '7.7 10.7\n' ;;
        portrait) printf '10.7 7.7\n' ;;
      esac
      ;;
  esac
}

# Does this height/footprint combination physically fit the C920's frame?
# ppi = 1360/h (CLAUDE.md's "Geometry" table); the frame then covers
# 1920/ppi x 1080/ppi inches, long axis x short axis. fs/fl (short x long)
# come from layout_footprint above — this function no longer derives them
# from a layout number itself, so the same formula and thresholds serve
# both the chosen orientation and, for layout 9, the other one printed as
# information. The margin is the TIGHTER of the two axes — a grid can lose
# on either one — and awk does the arithmetic because 1360/h is fractional
# and this is /bin/sh, where integer arithmetic would round it away
# entirely (e.g. 1360/8 truncating to a different answer than 170).
#
# Prints "<STATUS> <margin> <axis> <frame_short> <frame_long> <foot_short> <foot_long>"
# — STATUS is FAIL (margin < 0), WARN (0 <= margin < 0.5) or OK; <axis> is
# "short" or "long", whichever axis the margin came from.
compute_fit() {  # $1 = height (inches), $2 = footprint short (fs), $3 = footprint long (fl)
  h=$1; fs=$2; fl=$3
  awk -v h="$h" -v fs="$fs" -v fl="$fl" 'BEGIN {
    frame_short = 1080 * h / 1360
    frame_long  = 1920 * h / 1360
    margin_short = frame_short - fs
    margin_long  = frame_long  - fl
    if (margin_short < margin_long) { margin = margin_short; axis = "short" }
    else                            { margin = margin_long;  axis = "long" }
    status = (margin < 0) ? "FAIL" : (margin < 0.5) ? "WARN" : "OK"
    printf "%s %.2f %s %.2f %.2f %.2f %.2f\n", status, margin, axis, frame_short, frame_long, fs, fl
  }'
}

# --------------------------------------------------------------------------
# RFC 4180 field quoting — mirrors src/LoreFetch.Core/Collection/
# NativeCsvCodec.cs's QuoteField exactly, so this script's ground-truth.csv
# and the native collection format agree on one quoting rule rather than
# growing a second CSV dialect: quote a field that contains a comma, a
# double quote, or a CR/LF, doubling any embedded quote; never trim, never
# sanitise the content itself (an oracle name is written verbatim). Every
# field is run through this, not just oracle_name — cheap, and it means no
# field is ever silently unquoted because "that column can't have a comma".
# --------------------------------------------------------------------------
csv_quote_field() {
  field=$1
  case "$field" in
    *,*|*'"'*|*"$NL"*|*"$CR"*)
      printf '"%s"' "$(printf '%s' "$field" | sed 's/"/""/g')"
      ;;
    *)
      printf '%s' "$field"
      ;;
  esac
}

# --------------------------------------------------------------------------
# Oracle-name validation against the Scryfall manifest.
#
# ground-truth.csv's oracle_name is B6's answer key: a typo means the hash
# identified the card correctly but B6 scores it wrong@1 anyway, and a
# typo'd row is a LOW-distance wrong answer (the match itself was right),
# which is exactly what B6 uses to calibrate okDistance — so an unnoticed
# typo tightens the shipped threshold. Validating here, at capture time,
# catches it before it ever reaches B6.
#
# The manifest (scryfall-bulk/filtered-artworks.jsonl, field OracleName) is
# gitignored and machine-local, so this degrades on purpose: no manifest,
# or no jq to parse it, means a clear warning and an UNVALIDATED capture —
# never a refusal to shoot. --catalog overrides the path.
# --------------------------------------------------------------------------
CATALOG_AVAILABLE=0
CATALOG_NAMES_TMP=""

load_catalog() {
  if [ ! -f "$CATALOG_FILE" ]; then
    note "WARNING: oracle catalog not found at $CATALOG_FILE — card names will NOT be validated against Scryfall; proceeding unvalidated"
    return 0
  fi
  if ! command -v jq >/dev/null 2>&1; then
    note "WARNING: jq not found on PATH — cannot parse the oracle catalog at $CATALOG_FILE; card names will NOT be validated against Scryfall; proceeding unvalidated"
    return 0
  fi

  CATALOG_NAMES_TMP=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures-catalog.XXXXXX")
  # jq decodes the file's \uXXXX escapes (diacritics, curly quotes) into
  # real UTF-8 on the way out — a plain grep/sed extraction would leave
  # "Andúril" instead of "Andúril" and every accented name would
  # silently fail to match.
  if ! jq -r '.OracleName' "$CATALOG_FILE" > "$CATALOG_NAMES_TMP" 2>/dev/null; then
    note "WARNING: failed to parse $CATALOG_FILE as JSONL — card names will NOT be validated against Scryfall; proceeding unvalidated"
    rm -f "$CATALOG_NAMES_TMP"
    CATALOG_NAMES_TMP=""
    return 0
  fi

  CATALOG_AVAILABLE=1
}

# The awk program shared by resolve_cards and verify_ground_truth's name
# check: one pass over the catalog (first file) builds an exact-match set
# and a lowercase→canonical map, so the catalog is loaded ONCE per run
# regardless of how many cards or rows follow, then a second pass resolves
# each query line against it. \037 (unit separator) joins multiple
# candidates on one output line — vanishingly unlikely to appear in a card
# name, unlike comma or pipe.
CATALOG_MATCH_AWK='
  BEGIN { US = sprintf("%c", 31) }
  FNR == NR {
    name = $0
    if (length(name) == 0) { next }
    catalog[name] = 1
    lc = tolower(name)
    if (!(lc in lcmap)) { lcmap[lc] = name; lccount[lc] = 1 }
    else if (lcmap[lc] != name) { lcmap[lc] = lcmap[lc] US name; lccount[lc]++ }
    next
  }
  {
    q = $0
    if (q in catalog) { print "OK\t" q; next }
    lc = tolower(q)
    if (lc in lcmap) {
      if (lccount[lc] == 1) { print "OK\t" lcmap[lc]; next }
      else { print "AMBIG\t" lcmap[lc]; next }
    }
    # Close-match suggestions: cheap, not Levenshtein — every catalog name
    # that contains one of the query'"'"'s own words (>=3 chars) as a
    # case-insensitive substring, ranked by how many words it contains.
    nwords = split(lc, rawwords, /[^a-z0-9]+/)
    delete qw; nu = 0
    for (k = 1; k <= nwords; k++) { if (length(rawwords[k]) >= 3) { nu++; qw[nu] = rawwords[k] } }
    if (nu == 0) { for (k = 1; k <= nwords; k++) { nu++; qw[nu] = rawwords[k] } }
    delete cscore; maxscore = 0
    for (c in catalog) {
      lcc = tolower(c); score = 0
      for (k = 1; k <= nu; k++) { if (index(lcc, qw[k]) > 0) { score++ } }
      if (score > 0) { cscore[c] = score; if (score > maxscore) { maxscore = score } }
    }
    out = ""; count = 0
    for (s = maxscore; s >= 1 && count < 12; s--) {
      for (c in cscore) { if (cscore[c] == s && count < 12) { out = (out == "" ? c : out US c); count++ } }
    }
    print "MISS\t" out
  }
'

# Resolves each of "$@" (raw --card values, in slot order) against the
# catalog and prints the resolved list, one per line, in the same order —
# it never edits "$@" itself (a shell function cannot: bash/dash give every
# function call its own positional-parameter scope, restored on return), so
# the caller re-splits stdout the same way CARDS itself is split. Any
# unknown/ambiguous name dies with every offending slot and its close
# matches — inside this command substitution, "set -eu" still propagates
# die's exit 1 out to the whole script (die writes to stderr, which is not
# captured, so the message still reaches the terminal).
resolve_cards() {
  if [ "$CATALOG_AVAILABLE" -ne 1 ]; then
    printf '%s\n' "$@"  # unvalidated: pass through exactly as typed
    return 0
  fi

  queries_tmp=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures-queries.XXXXXX")
  for c in "$@"; do printf '%s\n' "$c" >> "$queries_tmp"; done

  results_tmp=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures-resolved.XXXXXX")
  awk "$CATALOG_MATCH_AWK" "$CATALOG_NAMES_TMP" "$queries_tmp" > "$results_tmp"
  rm -f "$queries_tmp"

  resolved=""
  errors=""
  err_count=0
  i=0
  for c in "$@"; do
    i=$((i + 1))
    line=$(sed -n "${i}p" "$results_tmp")
    status=$(printf '%s' "$line" | cut -f1)
    payload=$(printf '%s' "$line" | cut -f2-)
    case "$status" in
      OK)
        canon=$payload
        ;;
      AMBIG)
        cands=$(printf '%s' "$payload" | tr '\037' ',' | sed 's/,/, /g')
        errors="$errors
  slot $i ('$c'): matches the catalog only case-insensitively, and more than one canonical spelling differs only by case — type the exact case. Candidates: $cands"
        err_count=$((err_count + 1))
        canon=$c
        ;;
      *)
        if [ -n "$payload" ]; then
          cands=$(printf '%s' "$payload" | tr '\037' ',' | sed 's/,/, /g')
        else
          cands="(no close matches found)"
        fi
        errors="$errors
  slot $i ('$c'): not found in the oracle catalog. Close matches: $cands"
        err_count=$((err_count + 1))
        canon=$c
        ;;
    esac
    if [ -z "$resolved" ]; then resolved=$canon; else resolved="$resolved
$canon"; fi
  done
  rm -f "$results_tmp"

  if [ "$err_count" -gt 0 ]; then
    die "unknown or ambiguous --card name(s) against $CATALOG_FILE:$errors"
  fi

  printf '%s\n' "$resolved"
}

# --verify <csv>: re-checks an existing ground-truth.csv's oracle_name
# column against the catalog and reports unknown names with their line
# numbers, WITHOUT capturing anything. Parses each row with the same RFC
# 4180 quoting rule csv_quote_field writes (quoted fields, doubled quotes)
# rather than a naive comma split, because this script's own quoting can
# legally put a comma inside a quoted oracle_name. Safe to read line-by-line
# because a literal newline inside a field can never reach this file — see
# add_card's rejection.
verify_ground_truth() {
  csv=$1
  [ -f "$csv" ] || die "--verify: file not found: $csv"

  load_catalog
  if [ "$CATALOG_AVAILABLE" -ne 1 ]; then
    die "--verify needs the oracle catalog to check names against, but it is unavailable (see the warning above) — nothing to verify"
  fi

  say "Verifying oracle_name column of $csv against $CATALOG_FILE"

  report_tmp=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures-verify.XXXXXX")
  awk '
    function parsecsv(line, fields,    i, field, inq, n, ch, L) {
      n = 0; field = ""; inq = 0; L = length(line)
      for (i = 1; i <= L; i++) {
        ch = substr(line, i, 1)
        if (inq) {
          if (ch == "\"") {
            if (i < L && substr(line, i + 1, 1) == "\"") { field = field "\""; i++ }
            else { inq = 0 }
          } else { field = field ch }
        } else {
          if (ch == "\"" && field == "") { inq = 1 }
          else if (ch == ",") { fields[++n] = field; field = "" }
          else { field = field ch }
        }
      }
      fields[++n] = field
      return n
    }
    FNR == NR {
      name = $0
      if (length(name) == 0) { next }
      catalog[name] = 1
      lc = tolower(name)
      if (!(lc in lcmap)) { lcmap[lc] = name }
      next
    }
    FNR == 1 { next }
    {
      n = parsecsv($0, f)
      if (n < 5) { printf "ERR\t%d\t(expected >=5 columns, found %d)\n", FNR, n; next }
      name = f[5]
      if (name in catalog) { next }
      lc = tolower(name)
      if (lc in lcmap) { printf "CASE\t%d\t%s\t%s\n", FNR, name, lcmap[lc]; next }
      printf "UNKNOWN\t%d\t%s\n", FNR, name
    }
  ' "$CATALOG_NAMES_TMP" "$csv" > "$report_tmp"

  unknown_count=0
  case_count=0
  err_count=0
  while IFS='	' read -r kind lineno a b; do
    [ -n "$kind" ] || continue
    case "$kind" in
      UNKNOWN) unknown_count=$((unknown_count + 1)); note "line $lineno: unknown oracle_name '$a'" ;;
      CASE)    case_count=$((case_count + 1));       note "line $lineno: '$a' matches the catalog only case-insensitively (canonical: '$b')" ;;
      ERR)     err_count=$((err_count + 1));          note "line $lineno: could not parse row $a" ;;
    esac
  done < "$report_tmp"
  rm -f "$report_tmp"

  say "Verify summary"
  note "$unknown_count unknown, $case_count non-canonical case, $err_count unparseable, against $CATALOG_FILE"

  if [ "$unknown_count" -gt 0 ] || [ "$err_count" -gt 0 ]; then
    exit 1
  fi
  exit 0
}

# --------------------------------------------------------------------------
# Arguments
# --------------------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    --height) shift; [ $# -gt 0 ] || die "--height needs a value"; HEIGHT=$1 ;;
    --layout) shift; [ $# -gt 0 ] || die "--layout needs a value"; LAYOUT=$1 ;;
    --mat)    shift; [ $# -gt 0 ] || die "--mat needs a value"; MAT=$1 ;;
    --rung)   shift; [ $# -gt 0 ] || die "--rung needs a value"; RUNG=$1 ;;
    --orientation) shift; [ $# -gt 0 ] || die "--orientation needs a value"; ORIENTATION=$1 ;;
    --card)    shift; [ $# -gt 0 ] || die "--card needs a value"; add_card "$1" ;;
    --catalog) shift; [ $# -gt 0 ] || die "--catalog needs a value"; CATALOG_FILE=$1 ;;
    --verify)  shift; [ $# -gt 0 ] || die "--verify needs a value"; VERIFY_CSV=$1 ;;
    --force)   FORCE=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '3,97p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)         die "unknown argument: $1  (try --help)" ;;
  esac
  shift
done

# --------------------------------------------------------------------------
# --verify is a separate mode: it never touches the camera or the height/
# layout/mat/rung/card arguments below, so it dispatches before any of
# those are required.
# --------------------------------------------------------------------------
if [ -n "$VERIFY_CSV" ]; then
  verify_ground_truth "$VERIFY_CSV"
fi

# --------------------------------------------------------------------------
# Validate every label. Fail loudly and name the offending argument —
# silent acceptance of a bad label is the exact failure this script exists
# to prevent.
# --------------------------------------------------------------------------
[ -n "$HEIGHT" ] || die "missing required --height (a number of inches in ${MIN_HEIGHT}..${MAX_HEIGHT}, e.g. 12 or 9.75)"
[ -n "$LAYOUT" ] || die "missing required --layout (one of: $LAYOUTS)"
[ -n "$MAT" ]    || die "missing required --mat (one of: $MATS)"
[ -n "$RUNG" ]   || die "missing required --rung (one of: $RUNGS)"
[ -n "$CARDS" ]  || die "missing required --card (at least one, in slot order)"

in_height_range "$HEIGHT" || die "invalid --height '$HEIGHT' (expected a number of inches in ${MIN_HEIGHT}..${MAX_HEIGHT}, e.g. 12 or 9.75)"
in_set "$LAYOUT" "$LAYOUTS" || die "invalid --layout '$LAYOUT' (expected one of: $LAYOUTS)"
in_set "$MAT" "$MATS"       || die "invalid --mat '$MAT' (expected one of: $MATS)"
in_set "$RUNG" "$RUNGS"     || die "invalid --rung '$RUNG' (expected one of: $RUNGS)"
in_set "$ORIENTATION" "$ORIENTATIONS" || die "invalid --orientation '$ORIENTATION' (expected one of: $ORIENTATIONS)"

# Height and layout are also validated AGAINST EACH OTHER: a layout can be
# geometrically too big for a height even though both are individually
# allowed (a 3x3 grid does not fit the C920's frame at every height in
# range) — and for layout 9, WHICH footprint applies depends on
# --orientation (see layout_footprint above), so the same height can fit
# in one orientation and fail in the other. A frame that is too small
# still looks like a normal photo — it just crops cards off the edge —
# which is exactly the silent-corruption failure this whole script exists
# to prevent, so a negative margin is refused outright rather than filed
# and discovered later in B6.
footprint=$(layout_footprint "$LAYOUT" "$ORIENTATION")
set -- $footprint
fit=$(compute_fit "$HEIGHT" "$1" "$2")
set -- $fit
FIT_STATUS=$1; FIT_MARGIN=$2; FIT_AXIS=$3; FIT_FRAME_SHORT=$4; FIT_FRAME_LONG=$5; FIT_FOOT_SHORT=$6; FIT_FOOT_LONG=$7

# For layout 9 only, also compute the OTHER orientation's fit and print it
# as information — never gating, always informational — so the operator
# sees at a glance whether flipping orientation would change the outcome
# (e.g. at 12" layout 9: portrait FAILs, rotated is OK). Layouts 1 and 3
# have one footprint regardless of orientation (see layout_footprint), so
# there is nothing distinct to report there.
if [ "$LAYOUT" = 9 ]; then
  OTHER_ORIENTATION=rotated
  [ "$ORIENTATION" = rotated ] && OTHER_ORIENTATION=portrait
  other_footprint=$(layout_footprint "$LAYOUT" "$OTHER_ORIENTATION")
  set -- $other_footprint
  other_fit=$(compute_fit "$HEIGHT" "$1" "$2")
  set -- $other_fit
  OTHER_STATUS=$1; OTHER_MARGIN=$2; OTHER_AXIS=$3
  note "orientation $ORIENTATION (chosen): margin ${FIT_MARGIN}\" on the $FIT_AXIS axis [$FIT_STATUS] (footprint ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\")"
  note "orientation $OTHER_ORIENTATION (other): margin ${OTHER_MARGIN}\" on the $OTHER_AXIS axis [$OTHER_STATUS] — informational only, does not change what this run does"
fi

if [ "$FIT_STATUS" = FAIL ]; then
  die "height $HEIGHT\" cannot fit layout $LAYOUT in $ORIENTATION orientation: needs ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\" (short x long), the frame at ${HEIGHT}\" only covers ${FIT_FRAME_SHORT}\"x${FIT_FRAME_LONG}\" — short by ${FIT_MARGIN}\" on the $FIT_AXIS axis"
fi
FIT_WARNING=""
if [ "$FIT_STATUS" = WARN ]; then
  FIT_WARNING="margin is only ${FIT_MARGIN}\" on the $FIT_AXIS axis — no tolerance for the mat shifting"
  note "WARNING: $FIT_WARNING"
fi

# Split CARDS on newline only, so spaces inside a card name (e.g. "Birds of
# Paradise") stay part of one slot. `set -f` blocks pathname expansion on
# the unquoted split, which field splitting on an unquoted parameter
# expansion would otherwise perform.
set -f
IFS='
'
set -- $CARDS
unset IFS
set +f
CARD_COUNT=$#

[ "$CARD_COUNT" -eq "$LAYOUT" ] || die "layout $LAYOUT needs exactly $LAYOUT --card value(s), one per slot in order — got $CARD_COUNT"

# A comma (or a quote, or non-ASCII) in a name is no longer rejected here —
# it is legal RFC 4180 content and csv_quote_field below writes it
# correctly. (A literal newline/CR was already rejected in add_card, before
# it could ever join CARDS.) Instead, validate the names themselves against
# the real oracle catalog and rewrite "$@" to its canonical spelling — see
# load_catalog/resolve_cards above.
load_catalog
RESOLVED_CARDS=$(resolve_cards "$@")
set -f
IFS='
'
set -- $RESOLVED_CARDS
unset IFS
set +f

# --------------------------------------------------------------------------
# Derive the destination path, filename and ground-truth rows. Computed
# up front so --dry-run and the real run print/write exactly the same thing.
# --------------------------------------------------------------------------
slug_all=""
for c in "$@"; do
  s=$(slugify "$c")
  if [ -z "$slug_all" ]; then slug_all=$s; else slug_all="${slug_all}_${s}"; fi
done

# .png, not .jpg: HardwareCameraTests saves the decoded BGR frame as a PNG,
# and that lossless dump is the faithful artifact — the camera's own MJPG
# artifacts are already baked into those pixels, so re-encoding to JPEG
# would stack a second lossy generation on top for no reason. Ruled by the
# orchestrator 2026-09-22; test-images/README.md's .jpg is being corrected
# separately (out of this script's scope).
# Orientation goes in the filename ONLY for layout 9, and only there: it
# is the only layout with two real footprints (see layout_footprint), so
# it is the only layout where a portrait and a rotated capture of the
# same height/mat/rung/cards are genuinely different frames. Without this
# tag they would collide on the same DEST_FILE/CSV_FILE_FIELD, so a second
# capture in the other orientation would either be silently refused (no
# --force) or silently overwrite/relabel the first (with --force) — the
# exact silent-corruption failure this script exists to prevent. Layouts
# 1 and 3 keep their pre-existing filenames unchanged, since orientation
# never changes their footprint or their frame. This is NOT added as a
# ground-truth.csv column — that header is fixed and B6 parses it — so it
# only ever shows up inside the existing "file" column's path.
ORIENT_TAG=""
[ "$LAYOUT" = 9 ] && ORIENT_TAG="-${ORIENTATION}"
FILENAME="${HEIGHT}in-L${LAYOUT}${ORIENT_TAG}-${MAT}-${RUNG}-${slug_all}.png"
DEST_DIR="$REPO/test-images/fixtures/${HEIGHT}in/${LAYOUT}"
DEST_FILE="$DEST_DIR/$FILENAME"
CSV_FILE_FIELD="fixtures/${HEIGHT}in/${LAYOUT}/$FILENAME"
GT_CSV="$REPO/test-images/ground-truth.csv"
GT_HEADER="file,height_in,layout,slot,oracle_name,rung,mat"

ROWS=""
slot=0
for c in "$@"; do
  slot=$((slot + 1))
  row="$(csv_quote_field "$CSV_FILE_FIELD"),$(csv_quote_field "$HEIGHT"),$(csv_quote_field "$LAYOUT"),$(csv_quote_field "$slot"),$(csv_quote_field "$c"),$(csv_quote_field "$RUNG"),$(csv_quote_field "$MAT")"
  if [ -z "$ROWS" ]; then ROWS=$row; else ROWS="$ROWS
$row"; fi
done

if [ "$DRY_RUN" -eq 1 ]; then
  say "Dry run — nothing touched"
  note "destination : $DEST_FILE"
  note "orientation : $ORIENTATION"
  note "fit margin  : ${FIT_MARGIN}\" on the $FIT_AXIS axis (frame ${FIT_FRAME_SHORT}\"x${FIT_FRAME_LONG}\" vs footprint ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\", short x long)"
  [ -n "$FIT_WARNING" ] && note "WARNING: $FIT_WARNING"
  note "would refuse to overwrite unless --force (existing-file check skipped in --dry-run)"
  note "ground-truth rows to append to $GT_CSV:"
  printf '%s\n' "$ROWS" | sed 's/^/      /'
  exit 0
fi

# --------------------------------------------------------------------------
# From here on this is a real capture: it drives the C920 through the
# Stream C hardware test, so it only runs on the Windows PC.
# --------------------------------------------------------------------------
uname_s=$(uname -s 2>/dev/null || echo unknown)
case "$uname_s" in
  MINGW*|MSYS*|CYGWIN*) : ;;
  *) die "this drives the real C920 via a Hardware-traited test and only runs on the Windows PC under Git Bash (uname -s: $uname_s). Use --dry-run to validate labels on this machine." ;;
esac

command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH"

if [ -e "$DEST_FILE" ] && [ "$FORCE" -ne 1 ]; then
  die "$DEST_FILE already exists — pass --force to overwrite it (and replace its ground-truth rows)"
fi

# Scratch dir OUTSIDE the repo — never let a captured frame reach git, even
# transiently. LOREFETCH_HW_OUT is HardwareTestSupport's own escape hatch
# for exactly this (Tests/Capture/Hardware/HardwareTestSupport.cs).
scratch_dir=$(mktemp -d "${TMPDIR:-/tmp}/lorefetch-fixture-capture.XXXXXX")
hw_out_native=$(native_path "$scratch_dir")
cleanup() { rm -rf "$scratch_dir" 2>/dev/null || true; }

run_step() {  # label, then the command
  label=$1; shift
  say "$label"
  note "$*"
  log=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures.XXXXXX")
  if "$@" > "$log" 2>&1; then
    tail -5 "$log"; rm -f "$log"; return 0
  else
    status=$?
    printf '\n--- failed (exit %s); last 60 lines ---\n' "$status"
    tail -60 "$log"; rm -f "$log"
    return "$status"
  fi
}

if ! run_step "Build (Release)" dotnet build "$CAPTURE_TESTS_CSPROJ" -c Release; then
  cleanup
  die "build failed; see output above — nothing was filed"
fi

if ! run_step "Capture (dotnet test, filter: $FILTER)" \
     env "LOREFETCH_HW_OUT=$hw_out_native" dotnet test "$CAPTURE_TESTS_CSPROJ" -c Release --no-build --filter "$FILTER"; then
  note "scratch directory kept for inspection: $scratch_dir"
  die "capture test failed; see output above — nothing was filed"
fi

CAPTURED="$scratch_dir/last-frame.png"
if [ ! -s "$CAPTURED" ]; then
  note "scratch directory kept for inspection: $scratch_dir"
  die "capture reported success but produced no frame at $CAPTURED — nothing was filed"
fi

mkdir -p "$DEST_DIR"
mv -f "$CAPTURED" "$DEST_FILE"
cleanup

# Only now — frame safely in place — touch ground-truth.csv. On --force,
# drop this file's own prior rows first so a re-capture replaces rather
# than duplicates them.
[ -f "$GT_CSV" ] || printf '%s\n' "$GT_HEADER" > "$GT_CSV"
if [ "$FORCE" -eq 1 ]; then
  tmp_csv=$(mktemp "${TMPDIR:-/tmp}/capture-fixtures.XXXXXX")
  grep -vF "$CSV_FILE_FIELD," "$GT_CSV" > "$tmp_csv" || true
  mv "$tmp_csv" "$GT_CSV"
fi
printf '%s\n' "$ROWS" >> "$GT_CSV"

say "Filed"
note "$CARD_COUNT card(s), height=${HEIGHT}in layout=$LAYOUT orientation=$ORIENTATION mat=$MAT rung=$RUNG"
note "fit margin  : ${FIT_MARGIN}\" on the $FIT_AXIS axis (frame ${FIT_FRAME_SHORT}\"x${FIT_FRAME_LONG}\" vs footprint ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\", short x long)"
note "frame -> ${DEST_FILE#"$REPO"/}"
note "ground-truth -> ${GT_CSV#"$REPO"/} (+$CARD_COUNT row(s))"

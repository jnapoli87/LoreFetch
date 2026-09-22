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
#     --rung <rung> --card <name> [--card <name> ...] [--force] [--dry-run]
#
# Required:
#   --height <in>   one of: 8 10 12 14 20
#   --layout <n>    one of: 1 3 9 — also the number of --card values required
#   --mat <mat>     one of: light mid dark
#   --rung <rung>   one of: land normal stretch
#   --card <name>   the oracle name for one slot. Repeat in slot order —
#                   the FIRST --card is slot 1, the second is slot 2, etc.
#                   Must appear exactly <layout> times.
#
# Options:
#   --force         overwrite an existing frame for this exact cell, and
#                   replace (rather than duplicate) its ground-truth rows
#   --dry-run       validate everything and print what would be filed,
#                   touching neither the camera nor the filesystem
#
# Examples:
#   scripts/capture-fixtures.sh --height 10 --layout 1 --mat light \
#     --rung land --card Forest
#
#   scripts/capture-fixtures.sh --height 14 --layout 3 --mat dark \
#     --rung normal --card "Lightning Bolt" --card Counterspell \
#     --card "Birds of Paradise" --dry-run
#
# This runs the real capture only on the Windows PC (the C920 is attached
# there); --help and --dry-run work anywhere.
#
# Height/layout are cross-checked against each other, not just against
# their own allowed sets: the C920's frame at height h covers only
# (1920/ppi) x (1080/ppi) inches (ppi = 1360/h), so a 3x3 grid's 7.7"x10.7"
# footprint does not fit at every height on HEIGHTS. A combination whose
# margin is negative is refused outright, and one under 0.5" is filed with
# a warning — the frame looks fine either way, which is exactly what makes
# a bad fit a silent-corruption risk rather than an obvious one.
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

HEIGHTS="8 10 12 14 20"
LAYOUTS="1 3 9"
MATS="light mid dark"
RUNGS="land normal stretch"

# 9.75" is deliberately absent from HEIGHTS: it is where a 3x3 grid FIRST
# fits (0.04" of margin — one millimetre), a geometric floor rather than a
# usable operating height. The project's recorded operating height is
# moving to 12" (1.83" of margin) — see compute_fit below.

STREAMC_CSPROJ="$REPO/Tests/StreamC/LoreFetch.Tests.StreamC.csproj"
# Narrow enough to select exactly ONE test: the single frame-saving Hardware
# test, never SustainedRunKeepsMemoryFlat (~3 min), SlowConsumerSeesLatency-
# NotGrowth (~60s) or UnplugCameraTests (interactive). Verified with
# `dotnet test ... --filter "<this>" --list-tests`, which needs no camera.
FILTER="FullyQualifiedName~HardwareCameraTests.Negotiates1080pMjpgAndDeliversLiveFrames"

HEIGHT=""; LAYOUT=""; MAT=""; RUNG=""; FORCE=0; DRY_RUN=0
CARDS=""

add_card() {
  if [ -z "$CARDS" ]; then CARDS=$1; else CARDS="$CARDS
$1"; fi
}

in_set() {  # $1 = value, $2 = space-separated allowed set
  needle=$1; hay=$2
  for x in $hay; do [ "$x" = "$needle" ] && return 0; done
  return 1
}

slugify() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//'
}

# Windows-native path for a value handed to a .NET process under Git Bash —
# same trick guard-write.sh uses the other direction (win_to_unix).
native_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# Does this height/layout combination physically fit the C920's frame?
# ppi = 1360/h (CLAUDE.md's "Geometry" table); the frame then covers
# 1920/ppi x 1080/ppi inches, long axis x short axis. Footprints below are
# short x long, from the same table (1 card 2.5x3.5, 3-in-a-line 2.5x10.7,
# 3x3 grid 7.7x10.7). The margin is the TIGHTER of the two axes — a grid
# can lose on either one — and awk does the arithmetic because 1360/h is
# fractional and this is /bin/sh, where integer arithmetic would round it
# away entirely (e.g. 1360/8 truncating to a different answer than 170).
#
# Prints "<STATUS> <margin> <axis> <frame_short> <frame_long> <foot_short> <foot_long>"
# — STATUS is FAIL (margin < 0), WARN (0 <= margin < 0.5) or OK; <axis> is
# "short" or "long", whichever axis the margin came from.
compute_fit() {  # $1 = height (inches), $2 = layout
  h=$1
  case "$2" in
    1) fs=2.5;  fl=3.5 ;;
    3) fs=2.5;  fl=10.7 ;;
    9) fs=7.7;  fl=10.7 ;;
  esac
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
# Arguments
# --------------------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    --height) shift; [ $# -gt 0 ] || die "--height needs a value"; HEIGHT=$1 ;;
    --layout) shift; [ $# -gt 0 ] || die "--layout needs a value"; LAYOUT=$1 ;;
    --mat)    shift; [ $# -gt 0 ] || die "--mat needs a value"; MAT=$1 ;;
    --rung)   shift; [ $# -gt 0 ] || die "--rung needs a value"; RUNG=$1 ;;
    --card)   shift; [ $# -gt 0 ] || die "--card needs a value"; add_card "$1" ;;
    --force)   FORCE=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '3,63p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)         die "unknown argument: $1  (try --help)" ;;
  esac
  shift
done

# --------------------------------------------------------------------------
# Validate every label. Fail loudly and name the offending argument —
# silent acceptance of a bad label is the exact failure this script exists
# to prevent.
# --------------------------------------------------------------------------
[ -n "$HEIGHT" ] || die "missing required --height (one of: $HEIGHTS)"
[ -n "$LAYOUT" ] || die "missing required --layout (one of: $LAYOUTS)"
[ -n "$MAT" ]    || die "missing required --mat (one of: $MATS)"
[ -n "$RUNG" ]   || die "missing required --rung (one of: $RUNGS)"
[ -n "$CARDS" ]  || die "missing required --card (at least one, in slot order)"

in_set "$HEIGHT" "$HEIGHTS" || die "invalid --height '$HEIGHT' (expected one of: $HEIGHTS)"
in_set "$LAYOUT" "$LAYOUTS" || die "invalid --layout '$LAYOUT' (expected one of: $LAYOUTS)"
in_set "$MAT" "$MATS"       || die "invalid --mat '$MAT' (expected one of: $MATS)"
in_set "$RUNG" "$RUNGS"     || die "invalid --rung '$RUNG' (expected one of: $RUNGS)"

# Height and layout are also validated AGAINST EACH OTHER: a layout can be
# geometrically too big for a height even though both are individually
# allowed (a 3x3 grid does not fit the C920's frame at every height on
# HEIGHTS). A frame that is too small still looks like a normal photo — it
# just crops cards off the edge — which is exactly the silent-corruption
# failure this whole script exists to prevent, so a negative margin is
# refused outright rather than filed and discovered later in B6.
fit=$(compute_fit "$HEIGHT" "$LAYOUT")
set -- $fit
FIT_STATUS=$1; FIT_MARGIN=$2; FIT_AXIS=$3; FIT_FRAME_SHORT=$4; FIT_FRAME_LONG=$5; FIT_FOOT_SHORT=$6; FIT_FOOT_LONG=$7

if [ "$FIT_STATUS" = FAIL ]; then
  die "height $HEIGHT\" cannot fit layout $LAYOUT: needs ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\" (short x long), the frame at ${HEIGHT}\" only covers ${FIT_FRAME_SHORT}\"x${FIT_FRAME_LONG}\" — short by ${FIT_MARGIN}\" on the $FIT_AXIS axis"
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

for c in "$@"; do
  case "$c" in
    *,*) die "--card '$c' contains a comma, which breaks ground-truth.csv's flat format" ;;
  esac
done

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
FILENAME="${HEIGHT}in-L${LAYOUT}-${MAT}-${RUNG}-${slug_all}.png"
DEST_DIR="$REPO/test-images/fixtures/${HEIGHT}in/${LAYOUT}"
DEST_FILE="$DEST_DIR/$FILENAME"
CSV_FILE_FIELD="fixtures/${HEIGHT}in/${LAYOUT}/$FILENAME"
GT_CSV="$REPO/test-images/ground-truth.csv"
GT_HEADER="file,height_in,layout,slot,oracle_name,rung,mat"

ROWS=""
slot=0
for c in "$@"; do
  slot=$((slot + 1))
  row="$CSV_FILE_FIELD,$HEIGHT,$LAYOUT,$slot,$c,$RUNG,$MAT"
  if [ -z "$ROWS" ]; then ROWS=$row; else ROWS="$ROWS
$row"; fi
done

if [ "$DRY_RUN" -eq 1 ]; then
  say "Dry run — nothing touched"
  note "destination : $DEST_FILE"
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
# for exactly this (Tests/StreamC/Hardware/HardwareTestSupport.cs).
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

if ! run_step "Build (Release)" dotnet build "$STREAMC_CSPROJ" -c Release; then
  cleanup
  die "build failed; see output above — nothing was filed"
fi

if ! run_step "Capture (dotnet test, filter: $FILTER)" \
     env "LOREFETCH_HW_OUT=$hw_out_native" dotnet test "$STREAMC_CSPROJ" -c Release --no-build --filter "$FILTER"; then
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
note "$CARD_COUNT card(s), height=${HEIGHT}in layout=$LAYOUT mat=$MAT rung=$RUNG"
note "fit margin  : ${FIT_MARGIN}\" on the $FIT_AXIS axis (frame ${FIT_FRAME_SHORT}\"x${FIT_FRAME_LONG}\" vs footprint ${FIT_FOOT_SHORT}\"x${FIT_FOOT_LONG}\", short x long)"
note "frame -> ${DEST_FILE#"$REPO"/}"
note "ground-truth -> ${GT_CSV#"$REPO"/} (+$CARD_COUNT row(s))"

#!/usr/bin/env bash
#
# Releases the plugin: merges dev into main, bumps the version, tags it and pushes.
#
#   ./Release~/release.sh                 # patch: 0.7.2 → 0.7.3
#   ./Release~/release.sh minor           # 0.7.2 → 0.8.0
#   ./Release~/release.sh 0.9.0           # an exact version
#   ./Release~/release.sh minor --dry-run # show what it would do, change nothing
#
# A REQUIRED UPDATE (older copies get the red banner):
#   ./Release~/release.sh --require --message "This version works with the new agent."
#   --require sets update.json's minimum to the new version; --minimum 0.7.0 sets it to another.
#   Without either, update.json is left as it is. --message alone changes only the message;
#   --clear-message empties it.
#
# What it does, in order — and stops before changing anything if a check fails:
#   1. Checks: no uncommitted changes, dev not behind origin/dev, main can fast-forward to dev,
#      the tag is free, and CHANGELOG.md has an "## [Unreleased]" section with notes in it.
#   2. main ← dev (fast-forward), then bumps package.json, Editor/Core/ZettaConfig.cs, the CHANGELOG
#      heading (Unreleased → the version and today's date) and the README's pinning example.
#   3. Commits "Release X.Y.Z" on main and tags vX.Y.Z.
#   4. Asks, then pushes main and the tag (--yes skips the question), and fast-forwards dev to main and
#      pushes it.
#   5. Opens a GitHub release: with the gh CLI if installed, otherwise prints a link with the notes filled in.
#
# Installed copies learn of the release from main's package.json (the update banner) — nothing else to do.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
REPO="ig-shadow-walker/zetta-Unity3DPlugin"
REMOTE="${RELEASE_REMOTE:-origin}"

say() { printf '%s\n' "$*"; }
fail() { printf '✖ %s\n' "$*" >&2; exit 1; }

bump="patch"
minimum=""
message=""
set_message=false
dry_run=false
yes=false
while [ $# -gt 0 ]; do
  case "$1" in
    patch|minor|major) bump="$1" ;;
    [0-9]*.[0-9]*.[0-9]*) bump="$1" ;;
    --require) minimum="NEW" ;;
    --minimum) shift; minimum="${1:-}"; [ -n "$minimum" ] || fail "--minimum needs a version" ;;
    --message) shift; message="${1:-}"; set_message=true ;;
    --clear-message) message=""; set_message=true ;;
    --dry-run) dry_run=true ;;
    --yes|-y) yes=true ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) fail "Unknown argument: $1 (see --help)" ;;
  esac
  shift
done

# ── 1. Checks ────────────────────────────────────────────────────────────────

[ -z "$(git status --porcelain)" ] || fail "There are uncommitted changes. Commit them to dev first."

say "Fetching ${REMOTE}…"
git fetch --quiet --tags "$REMOTE"

git rev-parse --verify --quiet dev >/dev/null || fail "No local dev branch."
# dev may be ahead of GitHub (it is pushed with the release), never behind it.
git merge-base --is-ancestor "$REMOTE/dev" dev ||
  fail "$REMOTE/dev has commits your dev doesn't. Pull dev first."
git merge-base --is-ancestor "$REMOTE/main" dev ||
  fail "$REMOTE/main has commits dev doesn't. Merge main into dev first."

current="$(python3 -c 'import json;print(json.load(open("package.json"))["version"])')"
next="$(python3 - "$current" "$bump" <<'PY'
import re, sys
current, bump = sys.argv[1], sys.argv[2]
major, minor, patch = map(int, current.split("."))
if re.fullmatch(r"\d+\.\d+\.\d+", bump):
    print(bump)
elif bump == "major":
    print(f"{major + 1}.0.0")
elif bump == "minor":
    print(f"{major}.{minor + 1}.0")
else:
    print(f"{major}.{minor}.{patch + 1}")
PY
)"
python3 - "$current" "$next" <<'PY' || fail "$next is not newer than $current."
import sys
a, b = (tuple(map(int, v.split("."))) for v in sys.argv[1:3])
sys.exit(0 if b > a else 1)
PY
tag="v$next"
[ "$minimum" = "NEW" ] && minimum="$next"
if [ -n "$minimum" ]; then
  [[ "$minimum" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "--minimum must be x.y.z, not $minimum."
fi

git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null && fail "Tag $tag already exists."
git ls-remote --exit-code --tags "$REMOTE" "refs/tags/$tag" >/dev/null 2>&1 && fail "Tag $tag already exists on $REMOTE."

notes="$(python3 <<'PY'
import re
text = open("CHANGELOG.md", encoding="utf-8").read()
m = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.S | re.M)
print(m.group(1).strip() if m else "")
PY
)"
[ -n "$notes" ] || fail "CHANGELOG.md has no notes under \"## [Unreleased]\". Write what changed there first."

commits="$(git log --oneline "$REMOTE/main..dev")"
[ -n "$commits" ] || fail "dev has nothing main doesn't — nothing to release."

say ""
say "Release $current → $next ($tag)"
say ""
say "Commits from dev:"
say "$commits" | sed 's/^/  /'
say ""
if [ -n "$minimum" ]; then say "Required update: copies older than $minimum must update."; fi
if $set_message; then say "Update message: ${message:-(none)}"; fi
if [ -z "$minimum" ] && ! $set_message; then say "update.json: unchanged ($(tr -d '\n ' < 'Release~/update.json'))"; fi
say ""

if $dry_run; then
  say "Dry run: nothing was changed."
  exit 0
fi

# ── 2. main ← dev, and the version bump ───────────────────────────────────────

start_branch="$(git rev-parse --abbrev-ref HEAD)"
git checkout --quiet main
git merge --quiet --ff-only "$REMOTE/main" 2>/dev/null || true
git merge --quiet --ff-only dev || fail "main can't fast-forward to dev."

python3 - "$current" "$next" "$minimum" "$set_message" "$message" <<'PY'
import datetime, json, re, sys
current, nxt, minimum, set_message, message = sys.argv[1:6]

def edit(path, old, new, count=1):
    text = open(path, encoding="utf-8").read()
    updated, n = re.subn(old, new, text, count=count, flags=re.M)
    if n == 0:
        sys.exit(f"Could not update {path} ({old}).")
    open(path, "w", encoding="utf-8").write(updated)

edit("package.json", r'"version":\s*"[^"]+"', f'"version": "{nxt}"')
edit("Editor/Core/ZettaConfig.cs", r'(public const string Version = )"[^"]+"', rf'\g<1>"{nxt}"')
edit("CHANGELOG.md", r"^## \[Unreleased\][^\n]*", f"## [{nxt}] - {datetime.date.today():%Y-%m-%d}")
edit("README.md", r"(zetta-Unity3DPlugin\.git#v)\d+\.\d+\.\d+", rf"\g<1>{nxt}")

if minimum or set_message == "true":
    path = "Release~/update.json"
    data = json.load(open(path, encoding="utf-8"))
    if minimum:
        data["minimum"] = minimum
    if set_message == "true":
        data["message"] = message or None
    open(path, "w", encoding="utf-8").write(json.dumps(data, indent=2) + "\n")
PY

# ── 3. Commit and tag ─────────────────────────────────────────────────────────

git add package.json Editor/Core/ZettaConfig.cs CHANGELOG.md README.md "Release~/update.json"
git commit --quiet -m "Release $next"
git tag -a "$tag" -m "Zetta 3D for Unity $next"
say "Committed \"Release $next\" on main and tagged $tag."

undo_hint() {
  say ""
  say "Nothing was pushed. To undo locally:"
  say "  git tag -d $tag && git checkout main && git reset --hard $REMOTE/main && git checkout $start_branch"
}

# ── 4. Push ───────────────────────────────────────────────────────────────────

if ! $yes; then
  printf 'Push main, %s and dev to %s? [y/N] ' "$tag" "$REMOTE"
  read -r answer
  if [[ ! "$answer" =~ ^[Yy] ]]; then undo_hint; exit 1; fi
fi

git push --quiet "$REMOTE" main "$tag"
git checkout --quiet dev
git merge --quiet --ff-only main
git push --quiet "$REMOTE" dev
[ "$start_branch" = "dev" ] || git checkout --quiet "$start_branch"
say "Pushed main, $tag and dev. main = dev = $(git rev-parse --short main)."

# ── 5. GitHub release ─────────────────────────────────────────────────────────

body="$notes

**Install or update:** Package Manager › + › Add package from git URL › \`https://github.com/$REPO.git\`"

if command -v gh >/dev/null 2>&1 && [ "$REMOTE" = "origin" ]; then
  gh release create "$tag" --repo "$REPO" --title "Zetta 3D for Unity $next" --notes "$body"
  say "GitHub release $tag published."
else
  url="$(python3 - "$tag" "Zetta 3D for Unity $next" "$body" "$REPO" <<'PY'
import sys, urllib.parse
tag, title, body, repo = sys.argv[1:5]
query = urllib.parse.urlencode({"tag": tag, "title": title, "body": body}, quote_via=urllib.parse.quote)
print(f"https://github.com/{repo}/releases/new?{query}")
PY
)"
  say ""
  say "Publish the GitHub release (title and notes are filled in):"
  say "$url"
fi
say ""
say "Installed copies will see $next in their update banner within a day."

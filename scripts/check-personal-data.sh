#!/usr/bin/env bash
# Scans text that is about to become public for personal data.
#
#   scripts/check-personal-data.sh              the staged changes (run before every commit)
#   scripts/check-personal-data.sh develop      everything on this branch that develop lacks,
#                                               commit messages included (run before every push)
#   scripts/check-personal-data.sh --file PATH  any text file, such as a PR description
#
# The terms it looks for are read from the live database at run time, so none of them are ever
# written here. Only added lines are scanned. Matches are printed to the terminal and nowhere else.
#
# Exit 0: nothing found. Exit 1: a real name was found. Exit 2: the check could not run, which is
# not a pass.
set -u

data="${LOCALAPPDATA:-}"
data="${data//\\//}/Countinghouse"
db="$data/countinghouse.db"

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "check-personal-data: sqlite3 is not on PATH, so nothing was checked." >&2
    exit 2
fi
if [ ! -f "$db" ]; then
    echo "check-personal-data: no database at $db, so nothing was checked." >&2
    exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "${1:-}" in
    --file)
        [ -f "${2:-}" ] || { echo "check-personal-data: no such file: ${2:-}" >&2; exit 2; }
        what="$2"
        cat "$2" > "$work/text"
        ;;
    "")
        what="the staged changes"
        git diff --cached | grep '^+' > "$work/text"
        ;;
    *)
        what="commits and messages not on $1"
        git rev-parse --verify --quiet "$1" >/dev/null \
            || { echo "check-personal-data: unknown ref: $1" >&2; exit 2; }
        { git diff "$1...HEAD" | grep '^+'; git log "$1..HEAD" --format='%s%n%b'; git branch --show-current; } \
            > "$work/text"
        ;;
esac

query() { sqlite3 -readonly "$db" "$1" 2>/dev/null | tr -d '\r' | awk 'length($0) >= 4' | sort -u; }

# Names that identify an account or an institution. Any appearance is a failure.
{
    query "select Name from Accounts;"
    query "select InstitutionLabel from SimpleFinConnections;"
    # Anything else that must stay private (employer, town, people), one term per line.
    [ -f "$data/private-terms.txt" ] && tr -d '\r' < "$data/private-terms.txt" | awk 'length($0) >= 4'
} > "$work/names"

# Payees and rule patterns. Many are ordinary words, so these are listed for a look, not failed.
{
    query "select distinct Payee from Transactions;"
    query "select Pattern from CategoryRules;"
} | awk 'length($0) >= 6' > "$work/payees"

if [ ! -s "$work/names" ]; then
    echo "check-personal-data: the database has no account names to check against." >&2
    exit 2
fi

status=0
echo "Checking $what ($(wc -l < "$work/text" | tr -d ' ') lines)"

hits="$(grep -i -o -F -f "$work/names" "$work/text" | sort -f -u)"
if [ -n "$hits" ]; then
    status=1
    echo
    echo "FAIL: real account or institution names:"
    echo "$hits" | sed 's/^/    /'
    grep -n -i -F -f "$work/names" "$work/text" | cut -c1-160 | sed 's/^/      /'
fi

if [ -s "$work/payees" ]; then
    hits="$(grep -i -o -w -F -f "$work/payees" "$work/text" | sort -f -u)"
    if [ -n "$hits" ]; then
        echo
        echo "Look at these: they match a payee or rule pattern in the register. Ordinary words are fine."
        echo "$hits" | sed 's/^/    /'
    fi
fi

hits="$(grep -n -E '\$[0-9]{2,}|\$[0-9]\.[0-9]{2}|[0-9]{1,3}(,[0-9]{3})+\.[0-9]{2}' "$work/text" | cut -c1-160)"
if [ -n "$hits" ]; then
    echo
    echo "Look at these: dollar amounts. Invented, round-ish figures are fine."
    echo "$hits" | sed 's/^/    /'
fi

[ "$status" -eq 0 ] && { echo; echo "No real account or institution names found."; }
exit "$status"

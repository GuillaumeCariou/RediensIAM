#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# RediensIAM — a dev deployment with known contents. DESTRUCTIVE.
#
#   ./deploy/dev-fixture.sh              # tear down, reinstall, seed, verify
#   ./deploy/dev-fixture.sh --seed-only  # seed an install that is already up
#   ./deploy/dev-fixture.sh --yes        # no prompt (CI)
#
# Why this exists
# ───────────────
# The end-to-end suite runs against a real deployment and a real deployment keeps its rows. Two
# consequences, both of which have bitten this repository:
#
#   1. A test that asserts "the list shows three organisations" passes once and then never again.
#      Every existing spec works around it by inventing run-unique names, which is correct but
#      cannot express "a suspended tenant renders differently" — that needs a suspended tenant to
#      exist before the test starts.
#   2. A run that fails half way leaves its objects behind, so the next run starts from a state no
#      one described. Debugging then begins with archaeology.
#
# So the fixture is not a convenience: it is what makes an assertion about *contents* possible at
# all. The teardown is `reset-dev.sh`, unchanged and with its own guards; this script adds the
# seeding and the ordering.
#
# What it creates is in seed-dev.mjs — one file, so the tests and the seed cannot disagree about
# what exists.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${HERE}/.." && pwd)"

SEED_ONLY=0
ASSUME_YES=0
for arg in "$@"; do
  case "$arg" in
    --seed-only) SEED_ONLY=1 ;;
    --yes|-y)    ASSUME_YES=1 ;;
    -h|--help)   sed -n '2,26p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown flag: $arg" >&2; exit 2 ;;
  esac
done

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

if [ "$SEED_ONLY" -eq 0 ]; then
  say "[1/3] Tear down"
  # reset-dev.sh refuses anything that is not the dev install — that guard is the reason this
  # script does not roll its own teardown.
  if [ "$ASSUME_YES" -eq 1 ]; then
    "${HERE}/reset-dev.sh" --yes
  else
    "${HERE}/reset-dev.sh"
  fi

  say "[2/3] Install"
  "${HERE}/setup.sh" --dev
else
  say "[1/1] Seeding an existing install"
fi

say "[3/3] Seed"
# Node, not bash: the seed speaks the management API over an OAuth2 flow with PKCE, and a
# cookie-jar-and-sed version of that is a second implementation of something the test harness
# already knows how to do.
node "${ROOT}/tests/e2e/seed-dev.mjs"

say "Done"
cat <<'EOF'
  The deployment now holds the fixture described in tests/e2e/seed-dev.mjs.

  Run the suite:   cd tests/e2e && npx playwright test
  Re-seed only:    ./deploy/dev-fixture.sh --seed-only

  The fixture is idempotent: seeding twice leaves the same objects, so a failed run can be
  recovered with --seed-only rather than a full reinstall.
EOF

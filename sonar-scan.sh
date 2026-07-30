#!/usr/bin/env bash
set -e

# Single SonarQube project: RediensIAM.
# Covers the ASP.NET Core API (C#) and both SPAs (TypeScript/React) in one scan.
# The MSBuild scanner also indexes non-MSBuild files under sonar.projectBaseDir,
# so frontend/*/src is analysed by the same run — no separate scanner-cli passes.

SONAR_HOST="http://192.168.1.97:9000"
ENV_FILE="$(dirname "$0")/.sonar.env"

if [[ -f "$ENV_FILE" ]]; then
  # R-08. This file holds a live SonarQube token. It shipped `-rw-rw-r--`, so every local
  # account and every process running as this user could read it. The save path below already
  # chmodded; a file written before that existed did not, and sourcing it is the one moment we
  # are guaranteed to be looking at it.
  mode=$(stat -c %a "$ENV_FILE" 2>/dev/null || echo 600)
  if [[ "$mode" != "600" ]]; then
    echo "warning: $ENV_FILE was mode $mode — tightening to 600 (R-08)" >&2
    chmod 600 "$ENV_FILE"
  fi
  # shellcheck disable=SC1090
  source "$ENV_FILE"
fi

# Back-compat: older .sonar.env files only carried the per-component tokens.
if [[ -z "$SONAR_TOKEN" && -n "$SONAR_TOKEN_API" ]]; then
  SONAR_TOKEN="$SONAR_TOKEN_API"
fi

if [[ -z "$SONAR_TOKEN" ]]; then
  read -rsp "SonarQube token for project 'RediensIAM': " SONAR_TOKEN
  echo
  read -rp "Save token to .sonar.env for future runs? [y/N] " save
  if [[ "$save" =~ ^[Yy]$ ]]; then
    # umask before the redirect: chmod after the write leaves a window in which the token is
    # on disk under the caller's umask.
    ( umask 077; printf 'SONAR_TOKEN=%s\n' "$SONAR_TOKEN" > "$ENV_FILE" )
    chmod 600 "$ENV_FILE"
    echo "Token saved to .sonar.env"
  fi
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

# ── Frontend coverage (optional — only if the SPA defines a coverage script) ──
run_frontend_coverage() {
  local dir="$1"
  [[ -d "$dir/node_modules" ]] || return 0
  if node -e "process.exit(require('./$dir/package.json').scripts?.['test:coverage'] ? 0 : 1)" 2>/dev/null; then
    echo "==> Frontend coverage: $dir"
    (cd "$dir" && npm run test:coverage) || true
  fi
}
run_frontend_coverage "frontend/admin"
run_frontend_coverage "frontend/login"

# ── Single scan: API + both SPAs ─────────────────────────────────────────────
echo ""
echo "==> Scanning RediensIAM (API + Admin SPA + Login SPA)..."

rm -rf tests/RediensIAM.IntegrationTests/TestResults
rm -rf .sonarqube src/bin src/obj
# Recreate Debug stub so MSBuild glob expansion (bin/Debug) doesn't fail before Release build
mkdir -p src/bin/Debug/net10.0

dotnet sonarscanner begin \
  /k:"RediensIAM" \
  /n:"RediensIAM" \
  /d:sonar.host.url="$SONAR_HOST" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.projectBaseDir="$ROOT" \
  /d:sonar.exclusions="**/obj/**,**/bin/**,**/Migrations/**,**/node_modules/**,**/dist/**,**/coverage/**,**/playwright-report/**,**/test-results/**,**/.sonarqube/**,**/*.min.js,**/package-lock.json" \
  /d:sonar.cs.opencover.reportsPaths="tests/**/TestResults/**/coverage.opencover.xml" \
  /d:sonar.javascript.lcov.reportPaths="frontend/admin/coverage/lcov.info,frontend/login/coverage/lcov.info"

dotnet build RediensIAM.slnx --no-incremental

dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/RediensIAM.IntegrationTests/TestResults \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover || true

dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"

echo ""
echo "Done. Results: $SONAR_HOST/dashboard?id=RediensIAM"

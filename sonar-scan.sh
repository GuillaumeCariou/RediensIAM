#!/usr/bin/env bash
set -e

SONAR_HOST="http://192.168.1.97:9000"
ENV_FILE="$(dirname "$0")/.sonar.env"

if [[ -f "$ENV_FILE" ]]; then
  source "$ENV_FILE"
fi

# Prompt for any missing tokens
_needs_save=false

prompt_token() {
  local var_name="$1" label="$2"
  if [[ -z "${!var_name}" ]]; then
    read -rsp "$label token: " "$var_name"
    echo
    _needs_save=true
  fi
}

prompt_token SONAR_TOKEN_API   "API (ASP.NET Core)"
prompt_token SONAR_TOKEN_ADMIN "Admin SPA"
prompt_token SONAR_TOKEN_LOGIN "Login SPA"

if [[ "$_needs_save" == true ]]; then
  read -rp "Save tokens to .sonar.env for future runs? [y/N] " save
  if [[ "$save" =~ ^[Yy]$ ]]; then
    cat > "$ENV_FILE" <<EOF
SONAR_TOKEN_API=$SONAR_TOKEN_API
SONAR_TOKEN_ADMIN=$SONAR_TOKEN_ADMIN
SONAR_TOKEN_LOGIN=$SONAR_TOKEN_LOGIN
EOF
    echo "Tokens saved to .sonar.env"
  fi
fi

# ── API (ASP.NET Core) ────────────────────────────────────────────────────────
echo ""
echo "==> Scanning API..."
rm -rf tests/RediensIAM.IntegrationTests/TestResults
rm -rf .sonarqube src/bin src/obj
# Recreate Debug stub so MSBuild glob expansion (bin/Debug) doesn't fail before Release build
mkdir -p src/bin/Debug/net10.0

dotnet sonarscanner begin \
  /k:"RediensIAM" \
  /d:sonar.host.url="$SONAR_HOST" \
  /d:sonar.token="$SONAR_TOKEN_API" \
  /d:sonar.projectBaseDir="$(pwd)" \
  /d:sonar.exclusions="**/obj/**,**/bin/**,**/Migrations/**,**/frontend/**,**/tests/e2e/**" \
  /d:sonar.cs.opencover.reportsPaths="tests/**/TestResults/**/coverage.opencover.xml"

dotnet build RediensIAM.slnx --no-incremental

dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/RediensIAM.IntegrationTests/TestResults \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover || true

dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN_API"

# ── Admin SPA ────────────────────────────────────────────────────────────────
echo ""
echo "==> Scanning Admin SPA..."
docker run --rm \
  -e SONAR_TOKEN="$SONAR_TOKEN_ADMIN" \
  -v "$(pwd)/frontend/admin:/usr/src" \
  --network host \
  sonarsource/sonar-scanner-cli

# ── Login SPA ────────────────────────────────────────────────────────────────
echo ""
echo "==> Scanning Login SPA..."
docker run --rm \
  -e SONAR_TOKEN="$SONAR_TOKEN_LOGIN" \
  -v "$(pwd)/frontend/login:/usr/src" \
  --network host \
  sonarsource/sonar-scanner-cli

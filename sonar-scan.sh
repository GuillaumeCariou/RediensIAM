#!/usr/bin/env bash
set -e

SONAR_HOST="http://192.168.1.97:9000"
ENV_FILE="$(dirname "$0")/.sonar.env"

if [[ -f "$ENV_FILE" ]]; then
  source "$ENV_FILE"
fi

if [[ -z "$SONAR_TOKEN" ]]; then
  read -rsp "SonarQube token: " SONAR_TOKEN
  echo
  read -rp "Save token to .sonar.env for future runs? [y/N] " save
  if [[ "$save" =~ ^[Yy]$ ]]; then
    echo "SONAR_TOKEN=$SONAR_TOKEN" > "$ENV_FILE"
    echo "Token saved to .sonar.env"
  fi
fi

dotnet sonarscanner begin \
  /k:"RediensIAM" \
  /d:sonar.host.url="$SONAR_HOST" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.projectBaseDir="$(pwd)" \
  /d:sonar.sources="src" \
  /d:sonar.tests="tests" \
  /d:sonar.exclusions="**/obj/**,**/bin/**,**/Migrations/**" \
  /d:sonar.cs.opencover.reportsPaths="tests/**/TestResults/**/coverage.opencover.xml"

dotnet build RediensIAM.slnx --no-incremental

dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/RediensIAM.IntegrationTests/TestResults \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover

dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"

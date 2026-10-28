#!/usr/bin/env bash
# Builds the plugin with the official .NET 10 SDK container, so nothing but
# Docker is needed. Output: dist/Jellyfin.Plugin.Welcome.dll
#
# BUILD_HOST=user@host builds on another machine over SSH instead (needs
# rsync and passwordless docker there), for when this one has no Docker.
set -euo pipefail
cd "$(dirname "$0")"
PUBLISH="dotnet publish Jellyfin.Plugin.Welcome/Jellyfin.Plugin.Welcome.csproj -c Release -o /src/out -nologo -v q"
mkdir -p dist
if [[ -z "${BUILD_HOST:-}" ]]; then
  # A named volume keeps the NuGet cache between builds so restores are fast.
  docker run --rm -v "$PWD":/src -v welcome-nuget:/root/.nuget -w /src -u "$(id -u):$(id -g)" \
    -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp mcr.microsoft.com/dotnet/sdk:10.0 sh -c "$PUBLISH"
  cp out/Jellyfin.Plugin.Welcome.dll dist/
else
  REMOTE=/tmp/jellyfin-welcome-build
  rsync -a --delete --exclude dist --exclude .git --exclude bin --exclude obj --exclude out ./ "$BUILD_HOST:$REMOTE/"
  ssh "$BUILD_HOST" "sudo -n docker run --rm -v $REMOTE:/src -v welcome-nuget:/root/.nuget -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    $PUBLISH && sudo -n chown -R \$(id -u):\$(id -g) $REMOTE"
  scp -q "$BUILD_HOST:$REMOTE/out/Jellyfin.Plugin.Welcome.dll" dist/
fi
ls -l dist/

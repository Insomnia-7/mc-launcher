#!/usr/bin/env sh
set -eu
desktop_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
desktop_app="$desktop_root/src/Launcher.Desktop/bin/Release/net10.0/MCLauncher.Desktop.dll"
if [ ! -f "$desktop_app" ]; then
  dotnet build "$desktop_root/src/Launcher.Desktop/Launcher.Desktop.csproj" -c Release
fi
exec dotnet "$desktop_app" "$@"

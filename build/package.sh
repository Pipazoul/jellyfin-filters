#!/usr/bin/env bash
#
# Builds and packages the Duration Filter plugin into a Jellyfin-installable zip.
#
# Usage:  build/package.sh [version]
# Example: build/package.sh 0.1.0.0
#
# Produces, under dist/:
#   duration-filter-<version>.zip   (the DLL + meta.json, flat)
#   checksum.txt                    (MD5 of the zip, lowercase hex)
#   version.txt / zippath.txt       (convenience outputs for CI)
set -euo pipefail

VERSION="${1:-0.1.0.0}"

# --- static plugin metadata (keep the GUID in sync with Plugin.cs) -----------
GUID="957ba055-7b9a-4191-972a-59879fd73ee3"
NAME="Duration Filter"
OWNER="Pipazoul"
CATEGORY="General"
TARGET_ABI="10.10.0.0"
DLL="JellyfinPluginDurationFilter.dll"
OVERVIEW="Adds a Min/Max runtime filter (in minutes) to the library filter panel."
DESCRIPTION="Duration Filter injects a Min/Max runtime control into the jellyfin-web library filter panel. It composes with every other filter and the current sort order. Filtering is performed in the browser because the Jellyfin server has no runtime query parameter."
CHANGELOG="See the GitHub release notes for this version."

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo ">> Building $NAME $VERSION"
dotnet build JellyfinPluginDurationFilter.sln -c Release \
    -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

STAGE="$ROOT/dist/stage"
rm -rf "$ROOT/dist"
mkdir -p "$STAGE"

cp "JellyfinPluginDurationFilter/bin/Release/net8.0/$DLL" "$STAGE/$DLL"

# meta.json lives inside the zip; Jellyfin reads/populates it on install.
cat > "$STAGE/meta.json" <<EOF
{
    "category": "$CATEGORY",
    "changelog": "$CHANGELOG",
    "description": "$DESCRIPTION",
    "guid": "$GUID",
    "name": "$NAME",
    "overview": "$OVERVIEW",
    "owner": "$OWNER",
    "targetAbi": "$TARGET_ABI",
    "timestamp": "$TIMESTAMP",
    "version": "$VERSION",
    "assemblies": ["$DLL"]
}
EOF

ZIP="$ROOT/dist/duration-filter-$VERSION.zip"

# Flat zip (no nested folder) - Jellyfin extracts straight into the plugin dir.
python3 - "$ZIP" "$STAGE/$DLL" "$STAGE/meta.json" <<'PY'
import os, sys, zipfile
zippath = sys.argv[1]
with zipfile.ZipFile(zippath, "w", zipfile.ZIP_DEFLATED) as z:
    for f in sys.argv[2:]:
        z.write(f, os.path.basename(f))
PY

CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"

printf '%s\n' "$VERSION"  > "$ROOT/dist/version.txt"
printf '%s\n' "$CHECKSUM" > "$ROOT/dist/checksum.txt"
printf '%s\n' "$ZIP"      > "$ROOT/dist/zippath.txt"

echo ">> Packaged : $ZIP"
echo ">> Checksum : $CHECKSUM  (MD5)"

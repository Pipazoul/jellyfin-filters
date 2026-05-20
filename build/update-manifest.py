#!/usr/bin/env python3
"""Insert/update a version entry in the Jellyfin plugin repository manifest.

Usage:
    build/update-manifest.py <version> <checksum> <sourceUrl> [changelog] [timestamp]

The manifest (manifest.json at the repo root) is the file users add as a
"plugin repository" in their Jellyfin dashboard.
"""
import json
import os
import sys

GUID = "957ba055-7b9a-4191-972a-59879fd73ee3"
TARGET_ABI = "10.10.0.0"

PACKAGE_DEFAULTS = {
    "name": "Duration Filter",
    "description": (
        "Duration Filter injects a Min/Max runtime control into the jellyfin-web "
        "library filter panel. It composes with every other filter and the current "
        "sort order. Filtering is performed in the browser because the Jellyfin "
        "server has no runtime query parameter."
    ),
    "overview": "Adds a Min/Max runtime filter (in minutes) to the library filter panel.",
    "owner": "Pipazoul",
    "category": "General",
    "guid": GUID,
    "versions": [],
}


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)

    version = sys.argv[1]
    checksum = sys.argv[2]
    source_url = sys.argv[3]
    changelog = sys.argv[4] if len(sys.argv) > 4 else "See the GitHub release notes."
    timestamp = sys.argv[5] if len(sys.argv) > 5 else ""

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    manifest_path = os.path.join(root, "manifest.json")

    manifest = []
    if os.path.exists(manifest_path):
        with open(manifest_path, encoding="utf-8") as fh:
            manifest = json.load(fh)

    package = next(
        (p for p in manifest if str(p.get("guid", "")).lower() == GUID.lower()), None
    )
    if package is None:
        package = dict(PACKAGE_DEFAULTS)
        manifest.append(package)

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
    }

    # Replace any existing entry for this version, keep newest first.
    versions = [v for v in package.get("versions", []) if v.get("version") != version]
    versions.insert(0, entry)
    package["versions"] = versions

    with open(manifest_path, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
        fh.write("\n")

    print(f"Updated {manifest_path} -> version {version}")


if __name__ == "__main__":
    main()

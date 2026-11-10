"""Create flat Jellyfin packages and a manifest with durable GitHub release URLs."""
import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

# The revision matches AssemblyVersion in the project. A newer ABI must have
# a greater plugin version because targetAbi is only a minimum server version.
BUILD_ROOT = Path("src/Jellyfin.Plugin.CastCrew/bin/Release")
TARGETS = (("12", "net10.0", "12.0.0.0", 12),
           ("10.11", "net9.0", "10.11.0.0", 11),
           ("10.10", "net8.0", "10.10.0.0", 10))


def package_version(release, revision):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.0", release):
        raise ValueError("Release version must be major.minor.patch.0; revision is reserved for the server ABI")
    if any(int(part) > 65534 for part in release.split(".")):
        raise ValueError("Release components must fit a .NET assembly version")
    return release.rsplit(".", 1)[0] + f".{revision}"


def merge_versions(versions):
    # Remove ambiguous historical duplicates too. Prefer the newest ABI for an
    # old shared version; current releases provide unique builds for each server.
    ordered = sorted(versions, key=lambda v: (
        tuple(map(int, v["version"].split("."))),
        tuple(map(int, v["targetAbi"].split("."))),
    ), reverse=True)
    seen = set()
    result = []
    for entry in ordered:
        if entry["version"] not in seen:
            seen.add(entry["version"])
            result.append(entry)
    return result


def package(version, repository, output, existing=None):
    package_version(version, 12)  # Validate before writing any artifacts.
    output.mkdir(parents=True, exist_ok=True)
    versions = []
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    for series, framework, abi, revision in TARGETS:
        plugin_version = package_version(version, revision)
        filename = f"CastCrew_{plugin_version}_jellyfin-{series}.zip"
        build = BUILD_ROOT / framework
        with ZipFile(output / filename, "w", ZIP_DEFLATED) as archive:
            archive.write(build / "Jellyfin.Plugin.CastCrew.dll", "Jellyfin.Plugin.CastCrew.dll")
            symbols = build / "Jellyfin.Plugin.CastCrew.pdb"
            if symbols.exists():
                archive.write(symbols, symbols.name)
        with ZipFile(output / filename) as archive:
            assert "Jellyfin.Plugin.CastCrew.dll" in archive.namelist()
            assert all("/" not in name for name in archive.namelist())
        versions.append({
            "version": plugin_version,
            "changelog": f"Jellyfin {series}: use a distinct plugin version for each server ABI to prevent installation of an incompatible build.",
            "targetAbi": abi,
            "sourceUrl": f"https://github.com/{repository}/releases/download/v{version}/{filename}",
            "checksum": hashlib.md5((output / filename).read_bytes()).hexdigest(),
            "timestamp": timestamp,
        })
    if existing and existing.exists():
        old = json.loads(existing.read_text())[0]["versions"]
        keys = {v["version"] for v in versions}
        versions.extend(v for v in old if v["version"] not in keys)
    manifest = [{
        "guid": "a1c3e5f7-2b4d-6e8f-0a1c-3e5f7b9d1e3a",
        "name": "CastCrew",
        "description": "Adds a Cast & Crew page for browsing actors, directors, and producers in Jellyfin Web.",
        "overview": "Browse and filter cast and crew in Jellyfin Web.",
        "owner": repository.split("/")[0],
        "category": "General",
        "versions": merge_versions(versions),
    }]
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output", type=Path, default=Path("artifacts"))
    parser.add_argument("--existing", type=Path)
    args = parser.parse_args()
    package(args.version, args.repository, args.output, args.existing)

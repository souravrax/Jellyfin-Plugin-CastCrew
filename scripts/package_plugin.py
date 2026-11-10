"""Create flat Jellyfin packages and a manifest with durable GitHub release URLs."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

# Jellyfin selects the first compatible entry when plugin versions tie.
# Newer servers also satisfy older minimum ABIs, so newest ABI must come first.
TARGETS = (("12", "net10.0", "12.0.0.0"),
           ("10.11", "net9.0", "10.11.0.0"),
           ("10.10", "net8.0", "10.10.0.0"))


def package(version, repository, output, existing=None):
    output.mkdir(parents=True, exist_ok=True)
    versions = []
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    for series, framework, abi in TARGETS:
        filename = f"CastCrew_{version}_jellyfin-{series}.zip"
        build = Path("src/Jellyfin.Plugin.CastCrew/bin/Release") / framework
        with ZipFile(output / filename, "w", ZIP_DEFLATED) as archive:
            archive.write(build / "Jellyfin.Plugin.CastCrew.dll", "Jellyfin.Plugin.CastCrew.dll")
            symbols = build / "Jellyfin.Plugin.CastCrew.pdb"
            if symbols.exists():
                archive.write(symbols, symbols.name)
        with ZipFile(output / filename) as archive:
            assert "Jellyfin.Plugin.CastCrew.dll" in archive.namelist()
            assert all("/" not in name for name in archive.namelist())
        versions.append({
            "version": version,
            "changelog": f"Jellyfin {series}: add Jellyfin 12 support, updated people queries and authentication; retain 10.10/10.11 builds.",
            "targetAbi": abi,
            "sourceUrl": f"https://github.com/{repository}/releases/download/v{version}/{filename}",
            "checksum": hashlib.md5((output / filename).read_bytes()).hexdigest(),
            "timestamp": timestamp,
        })
    if existing and existing.exists():
        old = json.loads(existing.read_text())[0]["versions"]
        keys = {(v["version"], v["targetAbi"]) for v in versions}
        versions.extend(v for v in old if (v["version"], v["targetAbi"]) not in keys)
    manifest = [{
        "guid": "a1c3e5f7-2b4d-6e8f-0a1c-3e5f7b9d1e3a",
        "name": "CastCrew",
        "description": "Adds a Cast & Crew page for browsing actors, directors, and producers in Jellyfin Web.",
        "overview": "Browse and filter cast and crew in Jellyfin Web.",
        "owner": repository.split("/")[0],
        "category": "General",
        "versions": versions,
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

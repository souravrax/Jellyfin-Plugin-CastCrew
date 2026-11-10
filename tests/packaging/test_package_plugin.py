import hashlib
import importlib.util
import itertools
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_plugin", ROOT / "scripts/package_plugin.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


def number(version):
    return tuple(map(int, version.split(".")))


def select(versions, server):
    # Jellyfin InstallationManager: targetAbi <= server, then highest plugin version.
    compatible = [v for v in versions if number(v["targetAbi"]) <= number(server)]
    return max(compatible, key=lambda v: number(v["version"]))


class PackageSelectionTests(unittest.TestCase):
    def test_current_shared_versions_reproduce_wrong_build_selection(self):
        old = [{"version": "0.2.0.0", "targetAbi": abi} for abi in
               ["10.10.0.0", "10.11.0.0", "12.0.0.0"]]
        self.assertEqual(select(old, "12.4.0.0")["targetAbi"], "10.10.0.0")

    def test_real_package_manifest_selects_correct_build_in_every_order(self):
        with tempfile.TemporaryDirectory(prefix="castcrew-packaging-") as directory:
            output = Path(directory)
            # Build inputs are intentionally synthetic: this test checks packaging,
            # selection and checksums. C# tests verify the actual assembly versions.
            build_root = output / "build"
            for _, framework, _, _ in packager.TARGETS:
                build = build_root / framework
                build.mkdir(parents=True)
                (build / "Jellyfin.Plugin.CastCrew.dll").write_bytes(framework.encode())
            with patch.object(packager, "BUILD_ROOT", build_root):
                packager.package("0.2.1.0", "example/CastCrew", output)
            versions = json.loads((output / "manifest.json").read_text())[0]["versions"]
            self.assertEqual(len({v["version"] for v in versions}), 3)
            for entries in itertools.permutations(versions):
                for server, abi, version in [
                    ("10.10.7.0", "10.10.0.0", "0.2.1.10"),
                    ("10.11.11.0", "10.11.0.0", "0.2.1.11"),
                    ("12.0.0.0", "12.0.0.0", "0.2.1.12"),
                    ("12.4.0.0", "12.0.0.0", "0.2.1.12"),
                ]:
                    chosen = select(entries, server)
                    self.assertEqual((chosen["targetAbi"], chosen["version"]), (abi, version))
            for entry in versions:
                filename = entry["sourceUrl"].rsplit("/", 1)[-1]
                self.assertIn("/releases/download/v0.2.1.0/", entry["sourceUrl"])
                self.assertIn(entry["version"], filename)
                archive_path = output / filename
                self.assertEqual(hashlib.md5(archive_path.read_bytes()).hexdigest(), entry["checksum"])
                with ZipFile(archive_path) as archive:
                    self.assertEqual(archive.namelist(), ["Jellyfin.Plugin.CastCrew.dll"])

    def test_historical_duplicates_cannot_reintroduce_shared_versions(self):
        entries = [{"version": "0.2.0.0", "targetAbi": abi} for abi in
                   ["10.10.0.0", "10.11.0.0", "12.0.0.0"]]
        for order in itertools.permutations(entries):
            merged = packager.merge_versions(order)
            self.assertEqual(len(merged), 1)
            self.assertEqual(merged[0]["targetAbi"], "12.0.0.0")

    def test_release_must_reserve_revision_for_abi(self):
        for release in ["0.2.1.12", "0.2.1", "v0.2.1.0", "0.2.1.0-beta", "0.2.65535.0"]:
            with self.assertRaises(ValueError):
                packager.package_version(release, 12)
        self.assertEqual(packager.package_version("0.2.2.0", 10), "0.2.2.10")


if __name__ == "__main__":
    unittest.main()

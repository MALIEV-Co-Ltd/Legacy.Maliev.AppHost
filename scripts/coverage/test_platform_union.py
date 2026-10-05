"""Synthetic guard controls only; never production coverage evidence."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("union", Path(__file__).with_name("verify-platform-union.py"))
union = importlib.util.module_from_spec(spec)
spec.loader.exec_module(union)


class PlatformUnionControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.linux = self.root / "ubuntu"
        self.windows = self.root / "windows"
        for platform, directory in [("ubuntu", self.linux), ("windows", self.windows)]:
            directory.mkdir()
            names = sorted(union.MODULES if platform == "ubuntu" else {union.LOCAL_DELTA})
            packages = ET.Element("packages")
            inventory = []
            for name in names:
                source_key = name + "/Program.cs"
                source = directory / "sources" / source_key
                source.parent.mkdir(parents=True)
                source.write_bytes(b"synthetic executable guard fixture\n")
                artifact = directory / "modules" / name
                artifact.mkdir(parents=True)
                for ext in ["dll", "pdb"]:
                    (artifact / (name + "." + ext)).write_bytes((platform + ext).encode())
                digest = union.digest(source)
                inventory.append({"Name": name, "AssemblyVersion": "1.0.0.0", "ModuleVersionId": "12345678-1234-1234-1234-123456789012", "PdbId": "A" * 40, "DllSha256": union.digest(artifact / (name + ".dll")),
                                  "PdbSha256": union.digest(artifact / (name + ".pdb")), "Documents": [{
                                      "Path": "/fixture/" + source_key, "RelativePath": source_key, "Generated": False,
                                      "Lines": [1, 2, 3, 4, 5], "GitBlob": "a" * 40, "SourceSha256": digest,
                                      "PdbChecksum": {"Algorithm": "8829d00f-11b8-4213-878b-770e8597ac16", "Value": digest}}]})
                lines = ET.SubElement(ET.SubElement(ET.SubElement(ET.SubElement(packages, "package", name=name),
                                                                     "classes"), "class", filename="/fixture/" + source_key), "lines")
                for number in range(1, 6):
                    hits = name != union.LOCAL_DELTA or (number <= 3 if platform == "ubuntu" else number >= 3)
                    ET.SubElement(lines, "line", number=str(number), hits=str(int(hits)))
            coverage = ET.Element("coverage")
            coverage.append(packages)
            ET.ElementTree(coverage).write(directory / "raw.cobertura.xml")
            identity = {"Version": 1, "Platform": platform, "SdkVersion": "10.0.401", "CandidateHead": "b" * 40, "Parents": "e" * 40, "Head": "b" * 40, "Tree": "c" * 40,
                        "Dependencies": {**{name: {"Head": pin, "Tree": "d" * 40} for name, pin in union.PINS.items()}, "Legacy.Maliev.AppHost": {"Head": "b" * 40, "Tree": "c" * 40}}, "RepositoryRoot": "/fixture", "Coverage": {"Complete": True},
                        "ArtifactInventory": inventory, "RawSha256": union.digest(directory / "raw.cobertura.xml")}
            (directory / "identity.json").write_text(json.dumps(identity), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def mutate(self, callback):
        path = self.windows / "identity.json"
        identity = json.loads(path.read_text())
        callback(identity)
        path.write_text(json.dumps(identity), encoding="utf-8")

    def reject(self):
        with self.assertRaises((ValueError, FileNotFoundError, KeyError)):
            union.evaluate(self.linux, self.windows)

    def test_positive_union_uses_hits_not_percentage_average(self):
        report = union.evaluate(self.linux, self.windows)
        local = next(a for a in report["Assemblies"] if a["Name"] == union.LOCAL_DELTA)
        self.assertEqual((local["CoveredLines"], local["RawLines"], local["CoveragePercent"]), (5, 5, 100))
        self.assertTrue(report["MeetsThreshold"])

    def test_identity_mismatches_fail_closed(self):
        original = json.loads((self.windows / "identity.json").read_text())
        for field in ["CandidateHead", "Head", "Tree", "Parents", "Dependencies"]:
            with self.subTest(field=field):
                changed = copy.deepcopy(original)
                changed[field] = {"different": "e" * 40} if field == "Dependencies" else "f" * 40
                (self.windows / "identity.json").write_text(json.dumps(changed))
                self.reject()

    def test_source_blob_mismatch_is_not_filename_equivalence(self):
        self.mutate(lambda i: i["ArtifactInventory"][0]["Documents"][0].update(GitBlob="e" * 40))
        self.reject()

    def test_source_bytes_must_match_compiled_checksum(self):
        (self.windows / "sources" / union.LOCAL_DELTA / "Program.cs").write_bytes(b"changed")
        self.reject()

    def test_line_set_mismatch_rejected(self):
        self.mutate(lambda i: i["ArtifactInventory"][0]["Documents"][0]["Lines"].append(6))
        self.reject()

    def test_unknown_module_rejected(self):
        self.mutate(lambda i: i["ArtifactInventory"][0].update(Name="Unknown"))
        self.reject()

    def test_generated_mapping_mismatch_rejected(self):
        self.mutate(lambda i: i["ArtifactInventory"][0]["Documents"][0].update(Generated=True))
        self.reject()

    def test_missing_raw_symbols_rejected(self):
        (self.windows / "modules" / union.LOCAL_DELTA / (union.LOCAL_DELTA + ".pdb")).unlink()
        self.reject()

    def test_unknown_raw_line_rejected_even_with_rehashed_report(self):
        path = self.windows / "raw.cobertura.xml"
        tree = ET.parse(path)
        tree.find(".//line").set("number", "999")
        tree.write(path)
        self.mutate(lambda i: i.update(RawSha256=union.digest(path)))
        self.reject()

    def test_omitted_raw_line_rejected_even_with_rehashed_report(self):
        path = self.windows / "raw.cobertura.xml"
        tree = ET.parse(path)
        lines = tree.find(".//lines")
        lines.remove(lines[0])
        tree.write(path)
        self.mutate(lambda i: i.update(RawSha256=union.digest(path)))
        self.reject()

    def test_missing_windows_evidence_never_falls_back_to_linux(self):
        with self.assertRaises(FileNotFoundError):
            union.evaluate(self.linux, self.root / "missing-windows")

    def test_zero_hits_fail_actual_cli_threshold_and_preserve_failure_report(self):
        for directory in [self.linux, self.windows]:
            path = directory / "raw.cobertura.xml"
            tree = ET.parse(path)
            for line in tree.findall(".//line"):
                line.set("hits", "0")
            tree.write(path)
            metadata_path = directory / "identity.json"
            identity = json.loads(metadata_path.read_text())
            identity["RawSha256"] = union.digest(path)
            metadata_path.write_text(json.dumps(identity))
        output = self.root / "zero-hit-decision.json"
        result = subprocess.run([sys.executable, "-B", str(Path(__file__).with_name("verify-platform-union.py")),
                                 "--ubuntu", str(self.linux), "--windows", str(self.windows), "--output", str(output)],
                                capture_output=True, text=True, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("below 80%", result.stderr)
        self.assertFalse(json.loads(output.read_text())["MeetsThreshold"])

    def test_changed_raw_dll_and_pdb_bytes_fail_closed(self):
        for extension in ["dll", "pdb"]:
            with self.subTest(extension=extension):
                path = self.windows / "modules" / union.LOCAL_DELTA / (union.LOCAL_DELTA + "." + extension)
                original = path.read_bytes()
                path.write_bytes(b"different compiled artifact")
                self.reject()
                path.write_bytes(original)

    def test_malformed_and_empty_metadata_fail_closed(self):
        path = self.windows / "identity.json"
        for value in ["", "not-json", "{}", "null", "[]"]:
            with self.subTest(value=value):
                path.write_text(value)
                self.reject()

    def test_typed_git_pin_records_preserve_all_frozen_references(self):
        records = [{"repository": name, "commit": commit} for name, commit in union.PINS.items()]
        path = self.root / "pins.json"
        path.write_text(json.dumps(records))
        self.assertEqual(union.load_pins(path), union.PINS)

    def test_pin_records_reject_missing_duplicate_unknown_schema_and_partial_commit(self):
        records = [{"repository": name, "commit": commit} for name, commit in union.PINS.items()]
        path = self.root / "pins.json"
        invalid = [records[:-1], [records[0]] * 19]
        unknown = copy.deepcopy(records)
        unknown[0]["extra"] = "not allowed"
        invalid.append(unknown)
        partial = copy.deepcopy(records)
        partial[0]["commit"] = partial[0]["commit"][:7]
        invalid.append(partial)
        for value in invalid:
            with self.subTest(value=value):
                path.write_text(json.dumps(value))
                with self.assertRaises(ValueError):
                    union.load_pins(path)

    def test_actual_dependency_set_rejects_unknown_repository(self):
        def change(identity):
            dependency = identity["Dependencies"].pop(next(iter(union.PINS)))
            identity["Dependencies"]["Legacy.Maliev.UnknownRepository"] = dependency
        self.mutate(change)
        self.reject()


if __name__ == "__main__":
    unittest.main()

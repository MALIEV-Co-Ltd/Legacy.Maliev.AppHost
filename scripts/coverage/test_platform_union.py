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

    def eol_documents(self, key=None):
        key = key or sorted(union.SDK_ZERO_LINE_DOCUMENTS)[0]
        return key, {"RelativePath": key, "Generated": True, "Lines": []}

    def prove_eol(self, left, right, key=None, left_document=None, right_document=None, right_sdk="10.0.401"):
        key, document = self.eol_documents(key)
        return union.prove_sdk_zero_line_eol_pair(key, left_document or document, right_document or document,
                                                left, right, "10.0.401", right_sdk)

    def test_genuine_sdk_zero_line_eol_pairs_retain_raw_hashes(self):
        left = b'// SDK fixture\n[assembly: System.Reflection.AssemblyTitle("fixture")]\n'
        right = b'// SDK fixture\r\n[assembly: System.Reflection.AssemblyTitle("fixture")]\r\n'
        for key in union.SDK_ZERO_LINE_DOCUMENTS:
            with self.subTest(key=key):
                proof = self.prove_eol(left, right, key=key)
                self.assertEqual(proof["ExecutableKeys"], 0)
                self.assertEqual(proof["NewlineCount"], 2)
                self.assertNotEqual(proof["UbuntuSourceSha256"], proof["WindowsSourceSha256"])
                self.assertEqual(left, b'// SDK fixture\n[assembly: System.Reflection.AssemblyTitle("fixture")]\n')
                self.assertEqual(right, b'// SDK fixture\r\n[assembly: System.Reflection.AssemblyTitle("fixture")]\r\n')

    def test_sdk_eol_predicate_rejects_standalone_cr_and_bom_edits(self):
        left = b'// fixture\nusing System;\n'
        for right in [b'// fixture\rusing System;\r\n', b'// fixture\r', b'\xef\xbb\xbf// fixture\r\nusing System;\r\n']:
            with self.subTest(right=right):
                with self.assertRaises(ValueError):
                    self.prove_eol(left, right)

    def test_sdk_eol_predicate_rejects_content_token_comment_whitespace_attribute_edits(self):
        left = b'// fixture\n[assembly: AssemblyTitle("fixture")]\n'
        changes = [b'// other\r\n[assembly: AssemblyTitle("fixture")]\r\n',
                   b'// fixture\r\n[assembly: AssemblyCompany("fixture")]\r\n',
                   b'// fixture\r\n[assembly: AssemblyTitle("other")]\r\n',
                   b'// fixture\r\n [assembly: AssemblyTitle("fixture")]\r\n',
                   b'// fixture\r\n[assembly:  AssemblyTitle("fixture")]\r\n']
        for right in changes:
            with self.subTest(right=right):
                with self.assertRaises(ValueError):
                    self.prove_eol(left, right)

    def test_sdk_eol_predicate_rejects_newline_count_or_order_change(self):
        left = b'// fixture\nusing System;\n'
        for right in [b'// fixture\r\nusing System;\r\n\r\n', b'// fixture using\r\nSystem;\r\n']:
            with self.subTest(right=right):
                with self.assertRaises(ValueError):
                    self.prove_eol(left, right)

    def test_sdk_eol_predicate_rejects_nonzero_generated_executable_keys(self):
        key, document = self.eol_documents()
        document["Lines"] = [1]
        with self.assertRaises(ValueError):
            self.prove_eol(b'// fixture\n', b'// fixture\r\n', key, document, document)

    def test_sdk_eol_predicate_rejects_unknown_zero_line_document_and_wrong_tfm(self):
        for key in [union.LOCAL_DELTA + '/obj/Release/net10.0/Unknown.g.cs',
                    union.LOCAL_DELTA + '/obj/Release/net11.0/' + union.LOCAL_DELTA + '.GlobalUsings.g.cs']:
            with self.subTest(key=key):
                with self.assertRaises(ValueError):
                    self.prove_eol(b'// fixture\n', b'// fixture\r\n', key)

    def test_sdk_eol_predicate_rejects_path_sdk_and_generated_identity_mismatch(self):
        key, document = self.eol_documents()
        for field, value in [("RelativePath", "different"), ("Generated", False)]:
            changed = copy.deepcopy(document)
            changed[field] = value
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    self.prove_eol(b'// fixture\n', b'// fixture\r\n', key, document, changed)
        with self.assertRaises(ValueError):
            self.prove_eol(b'// fixture\n', b'// fixture\r\n', right_sdk="10.0.402")

    def add_sdk_zero_line_route_fixture(self):
        key = union.LOCAL_DELTA + '/obj/Release/net10.0/' + union.LOCAL_DELTA + '.GlobalUsings.g.cs'
        for directory, content in [(self.linux, b'// fixture\nglobal using System;\n'),
                                   (self.windows, b'// fixture\r\nglobal using System;\r\n')]:
            source = directory / "sources" / key
            source.parent.mkdir(parents=True)
            source.write_bytes(content)
            path = directory / "identity.json"
            identity = json.loads(path.read_text())
            module = next(a for a in identity["ArtifactInventory"] if a["Name"] == union.LOCAL_DELTA)
            module["Documents"].append({"Path": "/fixture/" + key, "RelativePath": key, "Generated": True,
                                        "Lines": [], "GitBlob": None, "SourceSha256": union.digest(source),
                                        "PdbChecksum": {"Algorithm": "8829d00f-11b8-4213-878b-770e8597ac16", "Value": union.digest(source)}})
            path.write_text(json.dumps(identity))
        return key

    def test_evaluate_routes_known_zero_line_eol_pair_without_denominator_change(self):
        key = self.add_sdk_zero_line_route_fixture()
        before = [(directory / "sources" / key).read_bytes() for directory in [self.linux, self.windows]]
        result = union.evaluate(self.linux, self.windows)
        self.assertTrue(result["MeetsThreshold"])
        self.assertEqual(len(result["GeneratedZeroLineEolProofs"]), 1)
        local = next(a for a in result["Assemblies"] if a["Name"] == union.LOCAL_DELTA)
        self.assertEqual((local["RawLines"], local["CoveredLines"]), (5, 5))
        self.assertEqual(before, [(directory / "sources" / key).read_bytes() for directory in [self.linux, self.windows]])

    def test_evaluate_rejects_properly_rehashed_sdk_token_change(self):
        key = self.add_sdk_zero_line_route_fixture()
        source = self.windows / "sources" / key
        source.write_bytes(b'// fixture\r\nglobal using Other;\r\n')
        def change(identity):
            document = next(d for d in identity["ArtifactInventory"][0]["Documents"] if d["RelativePath"] == key)
            document["SourceSha256"] = document["PdbChecksum"]["Value"] = union.digest(source)
        self.mutate(change)
        self.reject()

    def test_evaluate_rejects_generated_nonzero_keys_after_complete_rehashed_raw_mapping(self):
        key = self.add_sdk_zero_line_route_fixture()
        for directory in [self.linux, self.windows]:
            identity_path = directory / "identity.json"
            identity = json.loads(identity_path.read_text())
            module = next(a for a in identity["ArtifactInventory"] if a["Name"] == union.LOCAL_DELTA)
            next(d for d in module["Documents"] if d["RelativePath"] == key)["Lines"] = [1]
            raw_path = directory / "raw.cobertura.xml"
            raw = ET.parse(raw_path)
            package = next(p for p in raw.findall("./packages/package") if p.attrib["name"] == union.LOCAL_DELTA)
            cls = ET.SubElement(package.find("classes"), "class", filename="/fixture/" + key)
            ET.SubElement(ET.SubElement(cls, "lines"), "line", number="1", hits="0")
            raw.write(raw_path)
            identity["RawSha256"] = union.digest(raw_path)
            identity_path.write_text(json.dumps(identity))
        self.reject()


if __name__ == "__main__":
    unittest.main()

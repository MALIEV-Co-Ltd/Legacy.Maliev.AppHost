"""Read-only union of complete, symbol-validated raw platform evidence."""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET

MODULES = {
    "Legacy.Maliev.AppHost",
    "Legacy.Maliev.AppHost.MigrationRunner",
    "Legacy.Maliev.AppHost.LocalDeltaRunner",
    "Legacy.Maliev.AppHost.Topology",
}
LOCAL_DELTA = "Legacy.Maliev.AppHost.LocalDeltaRunner"
PINS = json.loads(Path(__file__).with_name("platform-dependency-pins.json").read_text())


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def digest(path, algorithm="sha256"):
    return hashlib.new(algorithm, path.read_bytes()).hexdigest().upper()


def source_key(path, root):
    path = path.replace("\\", "/")
    root = root.replace("\\", "/").rstrip("/")
    if path.startswith(root + "/"):
        path = path[len(root) + 1:]
    key = PurePosixPath(path)
    require(not key.is_absolute() and not re.match(r"^[A-Za-z]:", path), "Unmapped absolute source")
    require(".." not in key.parts and str(key) == path, "Noncanonical source identity")
    return path


def load_platform(directory, platform):
    identity = json.loads((directory / "identity.json").read_text(encoding="utf-8"))
    require(isinstance(identity, dict) and bool(identity), "Malformed/empty evidence metadata")
    require(identity["Version"] == 1 and identity["Platform"] == platform, "Wrong evidence version/platform")
    require(identity["SdkVersion"] == "10.0.401", "Unreviewed compiler SDK identity")
    require(re.fullmatch(r"[0-9a-f]{40}", identity["Head"]) is not None, "Invalid exact head")
    require(re.fullmatch(r"[0-9a-f]{40}", identity["Tree"]) is not None, "Invalid tree")
    require(re.fullmatch(r"[0-9a-f]{40}", identity["CandidateHead"]) is not None, "Invalid candidate head")
    require(identity["Coverage"]["Complete"] is True, "Incomplete platform evidence")
    dependencies = identity["Dependencies"]
    require(set(dependencies) == set(PINS) | {"Legacy.Maliev.AppHost"}, "Missing/unknown dependency ownership")
    require(dependencies["Legacy.Maliev.AppHost"] == {"Head": identity["Head"], "Tree": identity["Tree"]},
            "AppHost dependency identity mismatch")
    for name, pin in PINS.items():
        require(dependencies[name]["Head"] == pin, "Frozen dependency pin mismatch")
        require(re.fullmatch(r"[0-9a-f]{40}", dependencies[name]["Tree"]) is not None, "Invalid dependency tree")
    expected = MODULES if platform == "ubuntu" else {LOCAL_DELTA}
    assemblies = identity["ArtifactInventory"]
    require(len(assemblies) == len(expected) and {a["Name"] for a in assemblies} == expected,
            "Unknown/missing/duplicate module identity")
    report_path = directory / "raw.cobertura.xml"
    require(digest(report_path) == identity["RawSha256"], "Raw report checksum mismatch")
    report = ET.parse(report_path)
    packages = report.findall("./packages/package")
    require(len(packages) == len(expected) and {p.attrib["name"] for p in packages} == expected,
            "Unknown/missing/duplicate raw module")
    result = {}
    for assembly in assemblies:
        name = assembly["Name"]
        require(isinstance(assembly["AssemblyVersion"], str) and assembly["AssemblyVersion"], "Missing assembly identity")
        require(re.fullmatch(r"[0-9a-f-]{36}", assembly["ModuleVersionId"]) is not None, "Missing compiled module identity")
        require(re.fullmatch(r"[A-F0-9]{40}", assembly["PdbId"]) is not None, "Missing symbol identity")
        for extension, hash_key in [("dll", "DllSha256"), ("pdb", "PdbSha256")]:
            artifact = directory / "modules" / name / (name + "." + extension)
            require(digest(artifact) == assembly[hash_key], "Raw module/symbol checksum mismatch")
        documents = {}
        for document in assembly["Documents"]:
            key = source_key(document["Path"], identity["RepositoryRoot"])
            require(key == document["RelativePath"] and key not in documents, "Ambiguous source mapping")
            source = directory / "sources" / key
            require(digest(source) == document["SourceSha256"], "Source bytes checksum mismatch")
            algorithm = {"8829d00f-11b8-4213-878b-770e8597ac16": "sha256",
                         "ff1816ec-aa5e-4d10-87f7-6f4963833460": "sha1"}.get(document["PdbChecksum"]["Algorithm"])
            require(algorithm is not None, "Unknown PDB source checksum algorithm")
            require(digest(source, algorithm) == document["PdbChecksum"]["Value"], "PDB/source mismatch")
            require(isinstance(document["Generated"], bool), "Invalid generated identity")
            if not document["Generated"]:
                require(re.fullmatch(r"[0-9a-f]{40}", document["GitBlob"] or "") is not None,
                        "Missing source Git blob identity")
            lines = document["Lines"]
            require(isinstance(lines, list) and all(type(n) is int and n > 0 for n in lines), "Invalid executable line")
            require(len(set(lines)) == len(lines), "Duplicate executable line identity")
            documents[key] = document
        package = next(p for p in packages if p.attrib["name"] == name)
        observed = {}
        raw_paths = {}
        for cls in package.findall("./classes/class"):
            raw_path = cls.attrib["filename"]
            key = source_key(raw_path, identity["RepositoryRoot"])
            require(key in documents, "Unknown/generated raw source mapping")
            require(key not in raw_paths or raw_paths[key] == raw_path, "Ambiguous raw source alias")
            raw_paths[key] = raw_path
            values = observed.setdefault(key, {})
            for line in cls.findall("./lines/line"):
                number, hits = int(line.attrib["number"]), int(line.attrib["hits"])
                require(number in documents[key]["Lines"] and hits >= 0, "Unknown executable raw line/hit")
                values[number] = max(values.get(number, 0), hits)
        for key, document in documents.items():
            require(set(observed.get(key, {})) == set(document["Lines"]), "Omitted executable/generated line")
        result[name] = {"documents": documents, "hits": observed}
    return identity, result


def evaluate(ubuntu_directory, windows_directory):
    ubuntu, linux = load_platform(ubuntu_directory, "ubuntu")
    windows, win = load_platform(windows_directory, "windows")
    for field in ["CandidateHead", "Head", "Tree", "Parents", "Dependencies"]:
        require(ubuntu[field] == windows[field], "Cross-platform " + field + " mismatch")
    require(set(linux[LOCAL_DELTA]["documents"]) == set(win[LOCAL_DELTA]["documents"]),
            "Cross-platform document inventory mismatch")
    linux_module = next(a for a in ubuntu["ArtifactInventory"] if a["Name"] == LOCAL_DELTA)
    require(linux_module["AssemblyVersion"] == windows["ArtifactInventory"][0]["AssemblyVersion"],
            "Cross-platform production assembly version mismatch")
    for key, document in linux[LOCAL_DELTA]["documents"].items():
        other = win[LOCAL_DELTA]["documents"][key]
        require(set(document["Lines"]) == set(other["Lines"]), "Cross-platform executable line set mismatch")
        require(document["Generated"] == other["Generated"], "Cross-platform generated identity mismatch")
        if document["Generated"]:
            require(document["SourceSha256"] == other["SourceSha256"], "Generated source bytes mismatch")
        else:
            require(document["GitBlob"] == other["GitBlob"], "Production source Git blob mismatch")
    results = []
    for name in sorted(MODULES):
        keys = [(key, number) for key, document in linux[name]["documents"].items() for number in document["Lines"]]
        require(keys, "Empty production denominator")
        covered = sum(linux[name]["hits"][key][number] > 0 or
                      (name == LOCAL_DELTA and win[name]["hits"][key][number] > 0)
                      for key, number in keys)
        results.append({"Name": name, "RawLines": len(keys), "CoveredLines": covered,
                        "CoveragePercent": 100 * covered / len(keys), "MeetsThreshold": covered * 100 >= len(keys) * 80})
    return {"Complete": True, "Head": ubuntu["Head"], "Tree": ubuntu["Tree"], "MinimumPercent": 80,
            "Assemblies": results, "MeetsThreshold": all(a["MeetsThreshold"] for a in results),
            "Platforms": {"ubuntu": ubuntu, "windows": windows}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--ubuntu", type=Path, required=True)
    parser.add_argument("--windows", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.ubuntu, args.windows)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
    require(result["MeetsThreshold"], "Generated-inclusive production union remains below 80% per assembly")

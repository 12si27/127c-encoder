"""Stage a verified fdkaac release binary in a Windows/Linux publish folder."""

import argparse
import hashlib
import io
import json
import os
import subprocess
import tarfile
import urllib.request
import zipfile
from pathlib import Path


RELEASE_API = "https://api.github.com/repos/12si27/127c-encoder/releases/tags/deps-fdkaac-v1"
PLATFORMS = {"win-x64", "win-arm64", "linux-x64", "linux-arm64"}


def read_asset(rid):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "127c-encoder-release"}
    if os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = f"Bearer {os.environ['GITHUB_TOKEN']}"

    with urllib.request.urlopen(urllib.request.Request(RELEASE_API, headers=headers), timeout=30) as response:
        release = json.load(response)

    name = f"fdkaac-{rid}{'.zip' if rid.startswith('win-') else '.tar.gz'}"
    asset = next((item for item in release["assets"] if item["name"] == name), None)
    if not asset or not (asset.get("digest") or "").startswith("sha256:"):
        raise ValueError(f"Missing SHA-256 digest for {name}")

    request = urllib.request.Request(asset["browser_download_url"], headers={"User-Agent": "127c-encoder-release"})
    with urllib.request.urlopen(request, timeout=120) as response:
        archive = response.read()

    if hashlib.sha256(archive).hexdigest() != asset["digest"][len("sha256:"):].lower():
        raise ValueError(f"SHA-256 mismatch for {name}")
    return archive


def extract_files(archive, rid):
    wanted = {"fdkaac.exe" if rid.startswith("win-") else "fdkaac", "FDK-AAC-NOTICE"}
    result = {}

    if rid.startswith("win-"):
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            for entry in bundle.infolist():
                filename = Path(entry.filename).name
                if filename in wanted and filename not in result and not entry.is_dir():
                    result[filename] = bundle.read(entry)
    else:
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
            for entry in bundle:
                filename = Path(entry.name).name
                if filename in wanted and filename not in result and entry.isfile():
                    result[filename] = bundle.extractfile(entry).read()

    if result.keys() != wanted:
        raise ValueError(f"Archive does not contain exactly the expected files: {wanted - result.keys()}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=sorted(PLATFORMS))
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--smoke", action="store_true", help="launch the native fdkaac binary")
    args = parser.parse_args()

    files = extract_files(read_asset(args.rid), args.rid)
    destination = args.publish_dir / "encoder"
    destination.mkdir(parents=True, exist_ok=True)
    for name, content in files.items():
        path = destination / name
        path.write_bytes(content)
        if name.startswith("fdkaac") and not args.rid.startswith("win-"):
            path.chmod(0o755)
        print(f"Staged {path}")

    if args.smoke:
        binary = destination / ("fdkaac.exe" if args.rid.startswith("win-") else "fdkaac")
        result = subprocess.run([str(binary.resolve()), "--help"], capture_output=True, timeout=15)
        if b"fdkaac" not in (result.stdout + result.stderr).lower():
            raise RuntimeError("Bundled fdkaac did not start correctly")


if __name__ == "__main__":
    main()

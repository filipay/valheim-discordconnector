#!/usr/bin/env python3
"""Build the testing-only LiteDB WAL flush backport; never publish or edit the app."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

COMMIT = "84065086a8e8716063b255d0abb332708d0b2ad3"
ARCHIVE_SHA256 = "6479655da7efe6beaaa59713127f5df7f8220019bc321ac2797f6bbd6e8f1f14"
PACKAGES = {
    ("microsoft.netframework.referenceassemblies.net45", "1.0.3"): "23a9f94ea3e2cb88cd8341af75b811c6fb5cb82516fc696e95ed4620279128e3",
    ("microsoft.netframework.referenceassemblies", "1.0.3"): "141a093f90c7645d101ccd312e6f727781c965540eb92a52280f95411a698441",
    ("system.buffers", "4.5.1"): "c30b3dd2c7e2f4cee4b823d692fd42118309b42ab1f5007f923d329a5b0d6b12",
}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def obtain(path, url, expected, cached=None):
    if not path.exists():
        if cached and cached.is_file():
            shutil.copyfile(cached, path)
        else:
            path.write_bytes(urllib.request.urlopen(url, timeout=120).read())
    require(sha256(path) == expected, f"Hash mismatch: {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--workspace", required=True, type=Path)
    args = parser.parse_args()
    dotnet = args.dotnet.resolve()
    workspace = args.workspace.resolve()
    recipe = Path(__file__).resolve().parent
    require(dotnet.is_file(), "Supply an existing SDK dotnet executable")
    workspace.mkdir(parents=True, exist_ok=True)
    require(not (workspace / "source").exists(), "Use a fresh workspace (source already exists)")
    feed = workspace / "feed"
    feed.mkdir(exist_ok=True)
    archive = workspace / "upstream.tar.gz"
    archive_url = f"https://codeload.github.com/litedb-org/LiteDB/tar.gz/{COMMIT}"
    obtain(archive, archive_url, ARCHIVE_SHA256)
    with tarfile.open(archive) as tar:
        members = tar.getmembers()
        prefix = f"LiteDB-{COMMIT}"
        require(all(m.name.split("/")[0] == prefix and (m.isfile() or m.isdir())
                    for m in members), "Unexpected archive entry")
        tar.extractall(workspace / "source", filter="data")
    source = workspace / "source" / prefix
    before = {str(p.relative_to(source)): sha256(p) for p in source.rglob("*") if p.is_file()}
    disk = source / "LiteDB/Engine/Disk/DiskService.cs"
    original = disk.read_bytes()
    old = b"                    count++;\n                }\n            }\n\n            return count;"
    new = old.replace(b"                }\n", b"                }\n                stream.Flush();\n", 1)
    require(original.count(old) == 1, "Unexpected upstream WriteLogDisk source")
    subprocess.run(["patch", "--batch", "--forward", "--fuzz=0", "-p1", "-i",
                    str(recipe / "wal-flush.patch")], cwd=source, check=True)
    require(disk.read_bytes() == original.replace(old, new, 1), "Patch is not exactly the single WAL flush insertion")
    changed = [name for name, digest in before.items() if sha256(source / name) != digest]
    require(changed == ["LiteDB/Engine/Disk/DiskService.cs"], f"Unexpected source changes: {changed}")
    package_proof = []
    for (name, version), expected in PACKAGES.items():
        filename = f"{name}.{version}.nupkg"
        url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}"
        package = feed / filename
        obtain(package, url, expected, Path.home() / ".nuget/packages" / name / version / filename)
        with zipfile.ZipFile(package) as z:
            require(all(not n.startswith(("/", "\\")) and ".." not in n.replace("\\", "/").split("/")
                        for n in z.namelist()), f"Unsafe package path: {filename}")
        package_proof.append({"url": url, "sha256": expected})
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true",
               NUGET_PACKAGES=str(workspace / "packages"), NUGET_HTTP_CACHE_PATH=str(workspace / "http-cache"))
    properties = ["-p:TargetFrameworks=net45", "-p:TargetFramework=net45", "-p:Configuration=Release",
                  "-p:DefineConstants=RELEASE",
                  "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
                  "-p:AssemblyVersion=5.0.21.0", "-p:FileVersion=5.0.21.1",
                  f"-p:InformationalVersion=5.0.21-dc-walflush+{COMMIT}",
                  "-p:IncludeSourceRevisionInInformationalVersion=false", "-p:SignAssembly=true",
                  "-p:PublicSign=false", "-p:DelaySign=false", "-p:Deterministic=true", "-p:NuGetAudit=false"]
    project = source / "LiteDB/LiteDB.csproj"
    commands = []

    def run(label, command):
        result = subprocess.run([str(dotnet), *command], cwd=workspace, env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (workspace / f"{label}.log").write_text(result.stdout)
        commands.append({"cwd": str(workspace), "command": [str(dotnet), *command], "exit": result.returncode})
        (workspace / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        require(result.returncode == 0, f"{label} failed; see {workspace / (label + '.log')}")
        return result.stdout

    run("restore", ["restore", str(project), *properties, "--source", str(feed), "--packages", env["NUGET_PACKAGES"]])
    evaluated = json.loads(run("evaluated", ["msbuild", str(project), *properties,
        "-getProperty:TargetFramework,DefineConstants,SignAssembly,AssemblyOriginatorKeyFile,AssemblyVersion,FileVersion,InformationalVersion,GenerateAssemblyInfo"]))["Properties"]
    require(evaluated["TargetFramework"] == "net45" and evaluated["SignAssembly"] == "true", "Wrong target/signing")
    constants = evaluated["DefineConstants"].split(";")
    require("RELEASE" in constants and not any(c.startswith("HAVE_") or c == "DEBUG" for c in constants),
            "Constants must preserve shipped NuGet fallback behavior")
    run("build", ["build", str(project), "--no-restore", *properties, f"-bl:{workspace / 'build.binlog'}"])
    dist = workspace / "dist"
    dist.mkdir(exist_ok=True)
    shutil.copyfile(source / "LiteDB/bin/Release/net45/LiteDB.dll", dist / "LiteDB.dll")
    shutil.copyfile(source / "LICENSE", dist / "LICENSE.LiteDB.txt")
    provenance = {"testing_only": True, "source_commit": COMMIT, "archive_url": archive_url,
                  "archive_sha256": ARCHIVE_SHA256, "patch_sha256": sha256(recipe / "wal-flush.patch"),
                  "changed_source_files": changed, "packages": package_proof, "evaluated": evaluated,
                  "upstream_key_sha256": sha256(source / "LiteDB/LiteDB.snk"),
                  "output_sha256": sha256(dist / "LiteDB.dll"), "license_sha256": sha256(dist / "LICENSE.LiteDB.txt")}
    (dist / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(dist / "LiteDB.dll")


if __name__ == "__main__":
    main()

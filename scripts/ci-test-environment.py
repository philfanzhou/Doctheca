#!/usr/bin/env python3
"""Run the unchanged test suite in the pinned, test-only Linux browser environment."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET

VERSION = "1.63.0"
IMAGE = "mcr.microsoft.com/playwright/dotnet:v1.63.0-noble@sha256:aa3eaf29a65df8c93fc32ab20cf9359f4d8ecc53e6e89f79378647ace87b46b3"
MANIFEST = "sha256:4bb37fb71e75c18b6f8b8e4230139c7654de3dd39e55b66e84392c93933fed4e"
ROOT = Path(__file__).resolve().parent.parent
REPORT = ROOT / "reports/ci-test-environment.json"
DOCKER = ["docker", "--host", "unix:///var/run/docker.sock"]


class SetupError(Exception):
    pass


def output(args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=30)
    if result.returncode:
        raise SetupError("CI_TEST_TOOL_UNAVAILABLE: " + Path(args[0]).name)
    return result.stdout.strip()


def package_version(root):
    project = ET.parse(root / "src/Tests/Doctheca.Tests/Doctheca.Tests.csproj")
    return next(p.attrib.get("Version") for p in project.iter("PackageReference")
                if p.attrib.get("Include") == "Microsoft.Playwright")


def image_identity():
    result = subprocess.run(DOCKER + ["image", "inspect", IMAGE], capture_output=True, text=True, timeout=30)
    if result.returncode:
        return None
    data = json.loads(result.stdout)[0]
    if data["Architecture"] != "amd64" or data["Os"] != "linux":
        raise SetupError("CI_TEST_IMAGE_PLATFORM_MISMATCH")
    allowed = {IMAGE.split("@", 1)[1], MANIFEST}
    if not any(d.rsplit("@", 1)[-1] in allowed for d in data.get("RepoDigests", [])):
        raise SetupError("CI_TEST_IMAGE_DIGEST_MISMATCH")
    return data["Id"]


def toolchain():
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "amd64"):
        raise SetupError("CI_TEST_LINUX_AMD64_REQUIRED: use native verification on other hosts")
    if os.environ.get("DOCKER_HOST") not in (None, "", "unix:///var/run/docker.sock"):
        raise SetupError("CI_TEST_LOCAL_DOCKER_REQUIRED")
    socket = Path("/var/run/docker.sock")
    if not socket.exists() or not stat.S_ISSOCK(socket.stat().st_mode):
        raise SetupError("CI_TEST_LOCAL_DOCKER_SOCKET_REQUIRED")
    if package_version(ROOT) != VERSION:
        raise SetupError("CI_TEST_PLAYWRIGHT_VERSION_MISMATCH: update the package/image mapping together")
    dotnet = Path(shutil.which("dotnet") or "/missing/dotnet").resolve(strict=True)
    node = Path(shutil.which("node") or "/missing/node").resolve(strict=True)
    npm = Path(shutil.which("npm") or "/missing/npm").absolute()
    node_root = node.parent.parent
    if not npm.resolve(strict=True).is_relative_to(node_root):
        raise SetupError("CI_TEST_NODE_INSTALLATION_MISMATCH")
    node_version = output([str(node), "--version"])
    if not node_version.startswith("v20."):
        raise SetupError("CI_TEST_NODE_20_REQUIRED")
    packages = Path(output([str(dotnet), "nuget", "locals", "global-packages", "--list"]).split(": ", 1)[-1]).resolve(strict=True)
    if packages.name != "packages":
        raise SetupError("CI_TEST_PACKAGES_DIRECTORY_REQUIRED: credentials/config must not be mounted")
    # Read only packages, never the enclosing .nuget configuration/credentials directory.
    if not (packages / "microsoft.playwright" / VERSION).is_dir():
        raise SetupError("CI_TEST_PACKAGES_MISSING")
    return {"dotnet": str(dotnet), "dotnetRoot": str(dotnet.parent), "node": str(node),
            "nodeRoot": str(node_root), "npm": str(npm), "packages": str(packages),
            "nodeVersion": node_version, "npmVersion": output([str(npm), "--version"]),
            "sdk": output([str(dotnet), "--version"]),
            "sdks": output([str(dotnet), "--list-sdks"]),
            "runtimes": output([str(dotnet), "--list-runtimes"]),
            "playwrightVersion": VERSION}


def container(state, directory, mode):
    name = "doctheca-test-" + uuid.uuid4().hex
    state_file = directory / "state.json"
    state_file.write_text(json.dumps(state))
    mounts = [(ROOT, "rw"), (directory, "rw"), (Path(state["dotnetRoot"]), "ro"),
              (Path(state["nodeRoot"]), "ro"), (Path(state["packages"]), "ro")]
    args = DOCKER + ["run", "--name", name, "--rm", "--init", "--network", "host",
            "--shm-size", "1g", "--user", f"{os.getuid()}:{os.getgid()}",
            "--group-add", str(Path("/var/run/docker.sock").stat().st_gid),
            "--workdir", str(ROOT)]
    for path, access in mounts:
        args += ["--mount", f"type=bind,src={path},dst={path}" + (",readonly" if access == "ro" else "")]
    args += ["--mount", "type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock"]
    env = {"HOME": str(directory / "home"), "DOTNET_CLI_HOME": str(directory / "home"),
           "XDG_CACHE_HOME": str(directory / "cache"), "TMPDIR": str(directory / "tmp"),
           "NPM_CONFIG_CACHE": str(directory / "npm"), "NUGET_PACKAGES": state["packages"],
           "DOTNET_ROOT": state["dotnetRoot"], "DOTNET_ROOT_X64": state["dotnetRoot"],
           "DOTNET_HOST_PATH": state["dotnet"], "DOTNET_NOLOGO": "1",
           "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "false",
           "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "ASPNETCORE_HTTP_PORTS": "",
           "TESTCONTAINERS_HOST_OVERRIDE": "127.0.0.1", "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE": "/var/run/docker.sock",
           "TESTCONTAINERS_RYUK_DISABLED": "false", "DOCKER_HOST": "unix:///var/run/docker.sock",
           "PLAYWRIGHT_BROWSERS_PATH": "/ms-playwright",
           "PATH": f'{Path(state["node"]).parent}:{state["dotnetRoot"]}:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin'}
    for key, value in env.items():
        args += ["--env", key + "=" + value]
    args += ["--entrypoint", state["node"], IMAGE, str(ROOT / "scripts/ci-test-container.cjs"), str(state_file), mode]
    process = None

    def terminate(signum, _):
        if process and process.poll() is None:
            try:
                subprocess.run(DOCKER + ["stop", "--time", "10", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)
            finally:
                raise SystemExit(128 + signum)
        raise SystemExit(128 + signum)

    previous = {s: signal.signal(s, terminate) for s in (signal.SIGTERM, signal.SIGINT)}
    try:
        print("CI wrapper container: " + name, flush=True)
        process = subprocess.Popen(args)
        return process.wait()
    finally:
        # An exact unique name is the only Docker resource this helper removes.
        try:
            subprocess.run(DOCKER + ["rm", "--force", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)
        except subprocess.TimeoutExpired:
            print("CI_TEST_CLEANUP_TIMEOUT: runner disposal remains the final boundary", file=sys.stderr)
        for signum, handler in previous.items():
            signal.signal(signum, handler)


def pull_image():
    process = subprocess.Popen(DOCKER + ["pull", "--platform", "linux/amd64", IMAGE])

    def terminate(signum, _):
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
        raise SystemExit(128 + signum)

    previous = {s: signal.signal(s, terminate) for s in (signal.SIGTERM, signal.SIGINT)}
    try:
        return process.wait(timeout=300)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
        raise SetupError("CI_TEST_IMAGE_PULL_TIMEOUT")
    finally:
        for signum, handler in previous.items():
            signal.signal(signum, handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("prepare", "test", "probe", "cancel-probe"))
    args = parser.parse_args()
    REPORT.parent.mkdir(exist_ok=True)
    started = time.monotonic()
    record = json.loads(REPORT.read_text()) if REPORT.exists() else {"image": IMAGE, "phases": []}
    entry = {"phase": args.phase, "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    record["phases"].append(entry)
    def terminate(signum, _):
        raise SystemExit(128 + signum)
    previous = {s: signal.signal(s, terminate) for s in (signal.SIGTERM, signal.SIGINT)}
    try:
        state = toolchain()
        identity = image_identity()
        entry["cache"] = "warm" if identity else "cold"
        if args.phase == "prepare" and not identity:
            pull_start = time.monotonic()
            # Public image only: no Docker credential directory enters the test container.
            result = pull_image()
            entry["pullSeconds"] = round(time.monotonic() - pull_start, 3)
            if result:
                raise SetupError("CI_TEST_IMAGE_PULL_FAILED")
            identity = image_identity()
        if not identity:
            raise SetupError("CI_TEST_IMAGE_MISSING: run prepare first")
        entry["imageId"] = identity
        record["toolchain"] = state
        with tempfile.TemporaryDirectory(prefix="doctheca-ci-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
            directory = Path(temporary).resolve()
            for name in ("home", "cache", "tmp", "npm"):
                (directory / name).mkdir()
            result = container(state, directory, args.phase)
            browser_report = directory / "browser.json"
            if browser_report.exists():
                entry["browser"] = json.loads(browser_report.read_text())
        entry["exitCode"] = result
        return result
    except SystemExit as error:
        entry["exitCode"] = error.code
        raise
    except (SetupError, OSError, ValueError, StopIteration, subprocess.TimeoutExpired) as error:
        # Fixed diagnostics only; never dump environment, daemon output or connection settings.
        diagnostic = str(error) if isinstance(error, SetupError) else "CI_TEST_SETUP_INPUT_INVALID"
        print(diagnostic, file=sys.stderr)
        entry["error"] = diagnostic
        entry["exitCode"] = 1
        return 1
    finally:
        for signum, handler in previous.items():
            signal.signal(signum, handler)
        entry["seconds"] = round(time.monotonic() - started, 3)
        REPORT.write_text(json.dumps(record, indent=2) + "\n")
        print(f'CI test environment: {args.phase}, {entry["seconds"]}s, exit={entry.get("exitCode", "cancelled")}', flush=True)


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Failure-path tests for the CI wrapper's external Docker/tool boundary."""
import importlib.util
import json
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("ci_environment", Path(__file__).with_name("ci-test-environment.py"))
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)


class EnvironmentFailures(unittest.TestCase):
    def inspect(self, data):
        return patch.object(ci.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps([data])))

    def test_wrong_digest_and_platform_cannot_be_used(self):
        for data in [{"Os": "linux", "Architecture": "amd64", "RepoDigests": ["example@sha256:wrong"]},
                     {"Os": "linux", "Architecture": "arm64", "RepoDigests": []}]:
            with self.inspect(data), self.assertRaises(ci.SetupError):
                ci.image_identity()

    def test_absent_image_requires_preparation(self):
        with patch.object(ci.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "")):
            self.assertIsNone(ci.image_identity())

    def test_changed_package_is_observable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / "src/Tests/Doctheca.Tests/Doctheca.Tests.csproj"
            project.parent.mkdir(parents=True)
            project.write_text('<Project><PackageReference Include="Microsoft.Playwright" Version="0.0.0" /></Project>')
            with patch.object(ci, "ROOT", root), patch.object(ci.platform, "system", return_value="Linux"), \
                 patch.object(ci.platform, "machine", return_value="x86_64"), \
                 patch.object(Path, "stat", return_value=type("Socket", (), {"st_mode": stat.S_IFSOCK})()), \
                 patch.dict(ci.os.environ, {"DOCKER_HOST": "unix:///var/run/docker.sock"}), \
                 self.assertRaisesRegex(ci.SetupError, "CI_TEST_PLAYWRIGHT_VERSION_MISMATCH"):
                ci.toolchain()

    def test_registry_failure_is_failure_with_phase_timing(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "reports/environment.json"
            with patch.object(ci, "REPORT", report), patch.object(ci, "toolchain", return_value={}), \
                 patch.object(ci, "image_identity", return_value=None), \
                 patch.object(ci, "pull_image", return_value=23), \
                 patch("sys.argv", ["helper", "prepare"]):
                self.assertEqual(ci.main(), 1)
            phase = json.loads(report.read_text())["phases"][0]
            self.assertEqual(phase["error"], "CI_TEST_IMAGE_PULL_FAILED")
            self.assertEqual(phase["exitCode"], 1)
            self.assertGreaterEqual(phase["seconds"], 0)

    def test_nonzero_and_cancellation_remove_only_the_owned_container(self):
        for cancel in (False, True):
            with self.subTest(cancel=cancel), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                state = {"dotnet": "/tools/dotnet/dotnet", "dotnetRoot": "/tools/dotnet", "nodeRoot": "/tools/node", "packages": "/cache/packages",
                         "node": "/tools/node/bin/node"}
                calls = []

                class Process:
                    def poll(self): return None
                    def wait(self):
                        if cancel: signal.raise_signal(signal.SIGTERM)
                        return 37

                original_stat = Path.stat
                def socket_stat(path, *args, **kwargs):
                    if str(path) == "/var/run/docker.sock":
                        return type("Socket", (), {"st_gid": 123})()
                    return original_stat(path, *args, **kwargs)

                with patch.object(ci.subprocess, "Popen", return_value=Process()) as popen, \
                     patch.object(ci.subprocess, "run", side_effect=lambda args, **_: calls.append(args)), \
                     patch.object(Path, "stat", socket_stat):
                    if cancel:
                        with self.assertRaises(SystemExit) as error:
                            ci.container(state, directory, "test")
                        self.assertEqual(error.exception.code, 143)
                    else:
                        self.assertEqual(ci.container(state, directory, "test"), 37)
                args = popen.call_args.args[0]
                name = args[args.index("--name") + 1]
                self.assertTrue(name.startswith("doctheca-test-"))
                self.assertEqual(calls[-1], ci.DOCKER + ["rm", "--force", name])
                if cancel:
                    self.assertEqual(calls[0], ci.DOCKER + ["stop", "--time", "10", name])
                self.assertNotIn("--privileged", args)
                self.assertNotIn("--env-file", args)


def linux_cancellation_probe():
    process = subprocess.Popen([sys.executable, str(Path(__file__).with_name("ci-test-environment.py")), "cancel-probe"],
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    ready = threading.Event()
    owned = []
    def read():
        for line in process.stdout:
            print(line.rstrip(), flush=True)
            if line.startswith("CI wrapper container: "):
                owned.append(line.strip().split(": ", 1)[1])
            if line.strip() == "CI cancellation probe ready":
                ready.set()
    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    try:
        if not ready.wait(60):
            raise AssertionError("CI_TEST_CANCELLATION_PROBE_NOT_READY")
        process.send_signal(signal.SIGTERM)
        if process.wait(timeout=30) != 143:
            raise AssertionError("CI_TEST_CANCELLATION_STATUS_LOST")
        reader.join(timeout=5)
        if len(owned) != 1 or not owned[0].startswith("doctheca-test-"):
            raise AssertionError("CI_TEST_CANCELLATION_OWNER_UNKNOWN")
        exists = subprocess.run(ci.DOCKER + ["container", "ls", "--all", "--filter", "name=^/" + owned[0] + "$", "--format", "{{.Names}}"], capture_output=True, text=True, timeout=20)
        if exists.returncode != 0 or exists.stdout.strip():
            raise AssertionError("CI_TEST_CANCELLED_CONTAINER_RETAINED")
        print("CI cancellation probe passed: wrapper returned 143 and its exact container is absent.")
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=30)


if __name__ == "__main__":
    linux = "--linux" in sys.argv
    if linux: sys.argv.remove("--linux")
    result = unittest.main(exit=False)
    if not result.result.wasSuccessful(): sys.exit(1)
    if linux: linux_cancellation_probe()

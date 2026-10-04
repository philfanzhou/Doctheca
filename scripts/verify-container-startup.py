#!/usr/bin/env python3
"""Check the release image's default entrypoint using isolated synthetic inputs."""

import json
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path


def docker(*args, check=True):
    result = subprocess.run(
        ["docker", *args], capture_output=True, text=True, timeout=90
    )
    if check and result.returncode:
        # Docker errors can echo configuration. Do not print stderr or arguments.
        raise RuntimeError("Docker operation failed; configuration output suppressed")
    return result.stdout.strip()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def state(name):
    return json.loads(docker("inspect", "--format", "{{json .State}}", name))


def wait_exit(name, forbidden_url=None):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        current = state(name)
        if not current["Running"]:
            require(not current["OOMKilled"], "Container was killed by OOM")
            require(not current["Error"], "Container runtime reported an error")
            return current["ExitCode"]
        if forbidden_url:
            try:
                with urllib.request.urlopen(forbidden_url, timeout=0.25):
                    raise RuntimeError("Refused startup accepted an HTTP request")
            except urllib.error.HTTPError:
                raise RuntimeError("Refused startup accepted an HTTP request") from None
            except (OSError, urllib.error.URLError):
                pass
        time.sleep(0.25)
    raise RuntimeError("Container did not exit within 45 seconds")


def main(image, rollback_image=None):
    prefix = "doctheca-startup-" + secrets.token_hex(6)
    network = prefix + "-net"
    postgres = prefix + "-pg"
    password = "synthetic-" + secrets.token_hex(20)
    wrong_password = "synthetic-wrong-" + secrets.token_hex(20)
    invalid = "synthetic-invalid-" + secrets.token_hex(20)
    app_secret = "synthetic-app-" + secrets.token_hex(20)
    protocol_canaries = tuple("synthetic-protocol-" + secrets.token_hex(20) for _ in range(4))
    canaries = (password, wrong_password, invalid, app_secret, *protocol_canaries)
    containers = []
    network_created = False
    with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
        directory = Path(temporary)

        def run(name, selected_image, settings, *options):
            env_file = directory / (name + ".env")
            env_file.write_text("".join(f"{k}={v}\n" for k, v in settings.items()))
            env_file.chmod(0o600)
            containers.append(name)
            docker("run", "-d", "--name", name, "--network", network,
                   "--env-file", str(env_file), *options, selected_image)

        def database_exists(database):
            # Names are generated internally; no caller-provided SQL identifiers.
            return docker("exec", postgres, "psql", "-U", "startup_test", "-d", "postgres",
                          "-tAc", f"SELECT 1 FROM pg_database WHERE datname = '{database}'") == "1"

        def check_logs(name):
            # Unhandled exceptions are written to stderr, so collect both streams.
            result = subprocess.run(["docker", "logs", name], capture_output=True,
                                    text=True, timeout=30)
            require(result.returncode == 0, "Could not read container logs")
            logs = result.stdout + result.stderr
            require(all(canary not in logs for canary in canaries),
                    "Synthetic credential or configuration value leaked into logs")
            return logs

        try:
            docker("network", "create", network)
            network_created = True
            run(postgres, "postgres:16-alpine", {
                "POSTGRES_USER": "startup_test",
                "POSTGRES_PASSWORD": password,
                "POSTGRES_DB": "startup_existing",
            })
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                # Check TCP, avoiding the temporary local-only initialization server.
                if "accepting connections" in docker("exec", postgres, "pg_isready",
                        "-h", "127.0.0.1", "-U", "startup_test", "-d", "startup_existing", check=False):
                    break
                time.sleep(0.25)
            else:
                raise RuntimeError("PostgreSQL did not become ready")

            settings = {
                "ASPNETCORE_ENVIRONMENT": "Testing",
                "Consul__Host": "127.0.0.1", "Consul__Port": "1",
                "Consul__EnableCache": "false", "Consul__RetryCount": "0",
                "Loki__Uri": "", "LlmDocumentAnalysis__ApiKey": "",
                "StructaDoc__BaseUrl": "", "OpenSearch__Url": "http://127.0.0.1:1",
                "PostgreSql__Host": postgres, "PostgreSql__Port": "5432",
                "PostgreSql__Username": "startup_test", "PostgreSql__Password": password,
                "IdentityService__Authority": "http://localhost:5002",
                "IdentityService__Issuer": "http://localhost:5002",
                "IdentityService__Audience": "QuantumZhou.microservices",
                "IdentityService__RequireHttpsMetadata": "false",
                "IdentityService__AppId": "synthetic-startup-app",
                "IdentityService__AppSecret": app_secret,
            }
            cases = (
                ("invalid", {"Database__AllowCreate": invalid}, "database_target_preparation.invalid_target"),
                ("missing", {}, "database_target_preparation.creation_not_allowed"),
                ("unreachable", {"PostgreSql__Port": "1", "Database__AllowCreate": "true"},
                 "database_target_preparation.connection_failed"),
                ("authentication", {"PostgreSql__Password": wrong_password, "Database__AllowCreate": "true"},
                 "database_target_preparation.authentication_failed"),
            )
            for label, overrides, code in cases:
                database = "startup_missing_" + secrets.token_hex(8)
                name = prefix + "-" + label
                started = time.monotonic()
                run(name, image, settings | {"Database__Name": database} | overrides,
                    "-p", "127.0.0.1::5012")
                bindings = json.loads(docker("inspect", "--format", "{{json .NetworkSettings.Ports}}", name))
                port = bindings.get("5012/tcp")
                url = f"http://127.0.0.1:{port[0]['HostPort']}/health/live" if port else None
                exit_code = wait_exit(name, url)
                require(exit_code != 0, f"{label}: refusal exited successfully")
                logs = check_logs(name)
                require(code in logs, f"{label}: safe refusal code missing")
                require("Now listening on:" not in logs and "Application started." not in logs,
                        f"{label}: HTTP started before refusal")
                require(not database_exists(database), f"{label}: missing database was created")
                print(f"PASS {label}: stopped, exit={exit_code}, no listener/write/disclosure "
                      f"({time.monotonic() - started:.1f}s)", flush=True)

            name = prefix + "-success"
            run(name, image, settings | {"Database__Name": "startup_existing"},
                "-p", "127.0.0.1::5012")
            port = json.loads(docker("inspect", "--format", "{{json .NetworkSettings.Ports}}", name))["5012/tcp"][0]["HostPort"]
            expected = '{"status":"ready","phase":"completed","migrationStatus":"succeeded","databaseStatus":"reachable","errorCode":null}'

            def get(path):
                with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=2) as response:
                    require(response.status == 200, "Health status changed")
                    return response.read().decode()

            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                require(state(name)["Running"], "Successful startup exited unexpectedly")
                try:
                    if get("/health/ready") == expected:
                        break
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(0.25)
            else:
                raise RuntimeError("Successful startup did not become ready")
            require(get("/health") == expected, "Readiness alias changed")
            require(get("/health/live") == '{"status":"live"}', "Liveness contract changed")
            require(check_logs(name).count("Database startup gate completed") == 1,
                    "Expected exactly one successful startup gate")
            docker("kill", "--signal", "TERM", name)
            require(wait_exit(name) == 0, "SIGTERM did not shut down gracefully")
            check_logs(name)
            print("PASS success: exact health contracts, single gate, graceful SIGTERM", flush=True)
            name = prefix + "-unconfigured-login"
            run(name, image, settings | {"Database__Name": "startup_existing",
                "IdentityService__Authority": "", "IdentityService__Issuer": "", "IdentityService__Audience": "",
                "IdentityService__AppId": "", "IdentityService__AppSecret": "",
                "Logging__LogLevel__Microsoft.AspNetCore": "Trace"},
                "-p", "127.0.0.1::5012")
            port = json.loads(docker("inspect", "--format", "{{json .NetworkSettings.Ports}}", name))["5012/tcp"][0]["HostPort"]
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                require(state(name)["Running"], "Missing-login startup exited unexpectedly")
                try:
                    if get("/health/ready") == expected:
                        break
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(0.25)
            else:
                raise RuntimeError("Missing-login startup did not become ready")
            require("<!doctype html>" in get("/").lower(), "SPA not available without login configuration")

            def request_status(path, expected_status, expected_body=None, body=None):
                request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=body,
                    headers={"Content-Type": "text/plain"} if body is not None else {})
                try:
                    response = urllib.request.urlopen(request, timeout=2)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    require(response.code == expected_status, "Missing-login HTTP contract changed")
                    if expected_body:
                        require(response.read().decode() == expected_body, "Missing-login JSON contract changed")
                    require(not response.headers.get("Set-Cookie"), "Unconfigured or retired route wrote a Cookie")

            for path in ("start", "callback", "csrf", "session", "logout/return", "logout"):
                request_status("/admin/auth/oidc/" + path, 503,
                    '{"success":false,"message":"Hosted sign-in is not configured."}',
                    b"" if path == "logout" else None)
            protocol_query = "?" + "&".join(f"{key}={value}" for key, value in zip(
                ("code", "state", "error", "error_description"), protocol_canaries))
            for path in ("callback", "logout/return"):
                request_status("/admin/auth/oidc/" + path + protocol_query, 503,
                    '{"success":false,"message":"Hosted sign-in is not configured."}')
            for path in ("login", "refresh", "logout"):
                request_status("/admin/auth/" + path, 410,
                    '{"success":false,"message":"Password authentication has been retired. Use hosted sign-in."}', b"{invalid")
            request_status("/admin/auth/session", 401)
            require("DOCTHECA_OIDC_NOT_CONFIGURED" in check_logs(name), "Missing-login fixed diagnostic missing")
            docker("kill", "--signal", "TERM", name)
            require(wait_exit(name) == 0, "Missing-login shutdown failed")
            print("PASS missing login: startup/health/SPA, six OIDC 503, retired POST 410, auth 401, no protocol query disclosure at Trace", flush=True)

            if rollback_image:
                def fingerprint():
                    return docker("exec", postgres, "psql", "-U", "startup_test", "-d", "startup_existing", "-tAc", """
                        SELECT jsonb_agg(jsonb_build_array(c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull) ORDER BY c.relname, a.attnum)
                        FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
                        WHERE n.nspname='public' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped;
                        SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
                        SELECT count(*) FROM document_files;
                        """)
                before = fingerprint()
                # This database is owned by the random test prefix. Remove only its exact
                # verified history table to exercise legacy takeover through the shared writer.
                docker("exec", postgres, "psql", "-U", "startup_test", "-d", "startup_existing", "-c",
                       'DROP TABLE public."__EFMigrationsHistory"')
                for label, selected_image in (("legacy", image), ("rollback", rollback_image)):
                    name = prefix + "-" + label
                    run(name, selected_image, settings | {"Database__Name": "startup_existing"},
                        "-p", "127.0.0.1::5012")
                    port = json.loads(docker("inspect", "--format", "{{json .NetworkSettings.Ports}}", name))["5012/tcp"][0]["HostPort"]
                    deadline = time.monotonic() + 45
                    while time.monotonic() < deadline:
                        require(state(name)["Running"], label + " startup exited unexpectedly")
                        try:
                            if get("/health/ready") == expected:
                                break
                        except (OSError, urllib.error.URLError):
                            pass
                        time.sleep(0.25)
                    else:
                        raise RuntimeError(label + " image did not become ready")
                    require(fingerprint() == before, label + " image changed schema/history/data")
                    logs = check_logs(name)
                    require(logs.count("Database startup gate completed") == 1,
                            "Expected one " + label + " startup gate")
                    if label == "legacy":
                        require(logs.count("Legacy database taken over") == 1,
                                "Shared baseline writer was not exercised")
                    docker("kill", "--signal", "TERM", name)
                    require(wait_exit(name) == 0, label + " SIGTERM did not shut down gracefully")
                    check_logs(name)
                    print("PASS " + label + ": same database accepted; schema/history/data unchanged", flush=True)
        finally:
            # Remove only resources created under this run's random prefix.
            for name in reversed(containers):
                docker("rm", "-f", name, check=False)
            if network_created:
                docker("network", "rm", network, check=False)


if __name__ == "__main__":
    if len(sys.argv) not in (2, 3):
        sys.exit("Usage: python3 scripts/verify-container-startup.py IMAGE [ROLLBACK_IMAGE]")
    try:
        main(sys.argv[1], sys.argv[2] if len(sys.argv) == 3 else None)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        # Do not expose subprocess arguments or environment files on failures.
        sys.exit(str(error) if isinstance(error, RuntimeError) else "Docker operation timed out")

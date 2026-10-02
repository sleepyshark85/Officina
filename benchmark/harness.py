"""What the hidden acceptance tests share: they test the built program from outside, as its user would.

The workspace the team built is in BENCH_WORKSPACE. A test runs a console program's built assembly and a web service's on a
free port, and never reads the source. The assembly is run directly, not through `dotnet run`, so that stopping a service
stops the service itself. Only the standard library is used, on Linux and Windows.
"""

import atexit
import json
import os
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

WORKSPACE = os.environ.get("BENCH_WORKSPACE", os.getcwd())


def project(path):
    """The project folder, relative to the workspace."""
    return os.path.join(WORKSPACE, *path.split("/"))


ASSEMBLIES = {}


def build(path):
    """Builds the project once, and finds its assembly."""
    result = subprocess.run(["dotnet", "build", project(path), "-c", "Release", "-v", "q"], capture_output=True, text=True)
    if result.returncode != 0:
        raise AssertionError("the project does not build:\n" + result.stdout[-2000:])
    target = subprocess.run(["dotnet", "msbuild", project(path), "-getProperty:TargetPath", "-p:Configuration=Release"],
                            capture_output=True, text=True)
    ASSEMBLIES[path] = target.stdout.strip()


def command(path):
    return ["dotnet", ASSEMBLIES[path]]


def run(path, *args, stdin="", cwd=None, timeout=60):
    """Runs the console program with the arguments; returns (exit code, stdout, stderr)."""
    result = subprocess.run([*command(path), *args], input=stdin, capture_output=True, text=True, cwd=cwd, timeout=timeout)
    return result.returncode, result.stdout, result.stderr


def folder(prefix="bench-"):
    """A temporary folder, removed when the tests end."""
    path = tempfile.mkdtemp(prefix=prefix)
    atexit.register(shutil.rmtree, path, True)
    return path


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Service:
    """A web service the test starts on a free port, with its data in a folder of its own, and stops."""

    def __init__(self, path, environment):
        self.path = path
        self.port = free_port()
        self.data = folder()
        self.environment = {name: value.format(data=self.data) for name, value in environment.items()}
        self.process = None

    def start(self):
        env = dict(os.environ, PORT=str(self.port), **self.environment)
        self.process = subprocess.Popen(
            command(self.path), cwd=project(self.path), env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=1):
                    return self
            except OSError:
                if self.process.poll() is not None:
                    raise AssertionError("the service exited at start")
                time.sleep(0.2)
        raise AssertionError("the service did not listen within 60 seconds")

    def stop(self):
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            self.process = None

    def restart(self):
        self.stop()
        return self.start()

    def call(self, method, path, body=None, headers=None, follow=True):
        """Returns (status, headers, parsed JSON or text or None)."""
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", data=data, method=method, headers=dict(headers or {}))
        if data is not None:
            request.add_header("Content-Type", "application/json")
        opener = urllib.request.build_opener() if follow else urllib.request.build_opener(NoRedirect)
        try:
            with opener.open(request, timeout=30) as response:
                return response.status, response.headers, parse(response.read(), response.headers)
        except urllib.error.HTTPError as error:
            return error.code, error.headers, parse(error.read(), error.headers)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def parse(raw, headers):
    if not raw:
        return None
    text = raw.decode()
    if "json" in (headers.get("Content-Type") or ""):
        return json.loads(text)
    return text

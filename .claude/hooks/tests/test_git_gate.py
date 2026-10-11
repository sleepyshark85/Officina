"""Tests for git-gate.py's checks running in dotnet/. Run: python3 -m unittest discover -s .claude/hooks/tests"""
import importlib.util
import os
import stat
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "git-gate.py")
spec = importlib.util.spec_from_file_location("git_gate", SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ChecksRunInDotnetFolderTest(unittest.TestCase):
    """A stub dotnet records the folder it ran in."""

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.repo = os.path.realpath(os.path.join(folder.name, "repo"))
        stubs = os.path.join(folder.name, "stubs")
        self.record = os.path.join(folder.name, "folders")
        os.mkdir(stubs)
        stub = os.path.join(stubs, "dotnet")
        with open(stub, "w") as file:
            file.write(f'#!/bin/sh\npwd -P >> "{self.record}"\n')
        os.chmod(stub, os.stat(stub).st_mode | stat.S_IEXEC)
        patched = mock.patch.dict(os.environ, {"PATH": stubs + os.pathsep + os.environ["PATH"]})
        patched.start()
        self.addCleanup(patched.stop)
        os.makedirs(os.path.join(self.repo, "dotnet"))
        self.git("init", "-q", "-b", "main")
        self.write("README.md")
        self.git("add", "README.md")
        self.git("commit", "-q", "-m", "first")
        self.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.git("switch", "-q", "-c", "fix/x")
        self.write("dotnet/X.slnx")
        self.write("dotnet/A.cs")
        self.git("add", "dotnet")

    def git(self, *args):
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], cwd=self.repo, check=True,
                       capture_output=True)

    def write(self, path):
        with open(os.path.join(self.repo, path), "w") as file:
            file.write("x")

    def folders(self):
        with open(self.record) as file:
            return set(file.read().split())

    def test_a_staged_dotnet_path_is_dotnet_code(self):
        self.assertTrue(gate.is_dotnet("dotnet/src/Core/Run.cs"))

    def test_commit_checks_run_in_the_dotnet_folder(self):
        gate.before_commit(self.repo)
        self.assertEqual(self.folders(), {os.path.join(self.repo, "dotnet")})

    def test_push_tests_run_in_the_dotnet_folder(self):
        self.git("commit", "-q", "-m", "change")
        gate.before_push(self.repo, "fix/x")
        self.assertEqual(self.folders(), {os.path.join(self.repo, "dotnet")})


if __name__ == "__main__":
    unittest.main()

"""Tests for git-gate.py's choice of the .NET folder. Run: python3 -m unittest discover -s .claude/hooks/tests"""
import importlib.util
import os
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "git-gate.py")
spec = importlib.util.spec_from_file_location("git_gate", SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class DotnetFolderTest(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = folder.name

    def test_root_without_a_dotnet_folder(self):
        self.assertEqual(gate.dotnet_folder(self.root), self.root)

    def test_root_when_the_dotnet_folder_holds_no_solution(self):
        os.mkdir(os.path.join(self.root, "dotnet"))
        self.assertEqual(gate.dotnet_folder(self.root), self.root)

    def test_dotnet_folder_when_it_holds_a_solution(self):
        os.mkdir(os.path.join(self.root, "dotnet"))
        open(os.path.join(self.root, "dotnet", "Officina.slnx"), "w").close()
        self.assertEqual(gate.dotnet_folder(self.root), os.path.join(self.root, "dotnet"))

    def test_a_staged_dotnet_path_is_dotnet_code(self):
        self.assertTrue(gate.is_dotnet("dotnet/src/Core/Run.cs"))


if __name__ == "__main__":
    unittest.main()

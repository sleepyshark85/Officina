import json
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, folder, run  # noqa: E402

APP = "src/Taskr"
PY = f'"{sys.executable}"' if " " in sys.executable else sys.executable


def py(code):
    """A command that runs a line of Python, so the tests are the same on every system."""
    return f"{PY} -c \"{code}\""


class Taskr(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def setUp(self):
        self.folder = folder("taskr-")

    def tasks(self, tasks):
        with open(os.path.join(self.folder, "taskr.json"), "w") as file:
            json.dump(tasks, file)

    def taskr(self, *args):
        return run(APP, *args, cwd=self.folder, timeout=120)

    def append(self, name):
        return py(f"open('order.txt','a').write('{name}\\n')")

    def order(self):
        with open(os.path.join(self.folder, "order.txt")) as file:
            return file.read().split()

    def test_runs_dependencies_first_and_each_once(self):
        self.tasks({
            "lib": {"command": self.append("lib")},
            "app": {"command": self.append("app"), "deps": ["lib"]},
            "test": {"command": self.append("test"), "deps": ["lib", "app"]},
        })
        code, out, err = self.taskr("test", "app")
        self.assertEqual(0, code, err)
        self.assertEqual(["lib", "app", "test"], self.order())

    def test_independent_tasks_run_at_once_within_the_limit(self):
        sleep = py("import time; time.sleep(2)")
        self.tasks({name: {"command": sleep} for name in ("a", "b", "c", "d")} | {"all": {"deps": ["a", "b", "c", "d"]}})
        started = time.monotonic()
        self.assertEqual(0, self.taskr("all", "-j", "4")[0])
        parallel = time.monotonic() - started
        started = time.monotonic()
        self.assertEqual(0, self.taskr("all", "-j", "1")[0])
        serial = time.monotonic() - started
        self.assertLess(parallel + 4, serial)

    def test_output_lines_are_prefixed(self):
        self.tasks({"hello": {"command": py("print('one'); print('two')")}})
        out = self.taskr("hello")[1]
        self.assertIn("[hello] one", out)
        self.assertIn("[hello] two", out)

    def test_env_is_given_to_the_command(self):
        self.tasks({"show": {"command": py("import os; print(os.environ['GREETING'])"), "env": {"GREETING": "hi there"}}})
        self.assertIn("[show] hi there", self.taskr("show")[1])

    def test_an_unchanged_task_is_up_to_date(self):
        os.makedirs(os.path.join(self.folder, "src", "deep"))
        with open(os.path.join(self.folder, "src", "deep", "a.txt"), "w") as file:
            file.write("v1")
        self.tasks({"copy": {
            "command": py("import shutil; shutil.copy('src/deep/a.txt', 'out.txt'); open('runs.txt','a').write('x')"),
            "inputs": ["src/**/*.txt"], "outputs": ["out.txt"]}})
        self.taskr("copy")
        self.assertIn("copy: up to date", self.taskr("copy")[1])
        with open(os.path.join(self.folder, "src", "deep", "a.txt"), "w") as file:
            file.write("v2")
        self.assertNotIn("up to date", self.taskr("copy")[1])
        os.remove(os.path.join(self.folder, "out.txt"))
        self.assertNotIn("up to date", self.taskr("copy")[1])
        with open(os.path.join(self.folder, "runs.txt")) as file:
            self.assertEqual("xxx", file.read())
        self.assertEqual(0, self.taskr("--clean")[0])
        self.assertFalse(os.path.exists(os.path.join(self.folder, ".taskr")))
        self.assertNotIn("up to date", self.taskr("copy")[1])

    def test_a_failure_stops_new_tasks_and_is_not_recorded(self):
        self.tasks({
            "bad": {"command": py("import sys; sys.exit(3)"), "inputs": ["taskr.json"], "outputs": ["taskr.json"]},
            "after": {"command": self.append("after"), "deps": ["bad"]},
        })
        code, out, err = self.taskr("after")
        self.assertEqual(1, code)
        self.assertIn("bad failed with exit code 3", out + err)
        self.assertFalse(os.path.exists(os.path.join(self.folder, "order.txt")))
        self.assertNotIn("up to date", self.taskr("after")[1])

    def test_a_cycle_or_an_unknown_task_is_reported_before_anything_runs(self):
        self.tasks({"a": {"command": self.append("a"), "deps": ["b"]}, "b": {"deps": ["a"]}, "c": {"command": self.append("c"), "deps": ["nothing"]}})
        code, out, err = self.taskr("a")
        self.assertEqual(2, code)
        self.assertRegex(out + err, r"cycle: (a -> b -> a|b -> a -> b)")
        self.assertEqual(2, self.taskr("c")[0])
        self.assertEqual(2, self.taskr("missing")[0])
        self.assertFalse(os.path.exists(os.path.join(self.folder, "order.txt")))

    def test_lists_and_dry_runs(self):
        self.tasks({"lib": {"command": self.append("lib")}, "app": {"command": self.append("app"), "deps": ["lib"]}})
        listed = self.taskr("--list")[1]
        self.assertIn("lib", listed)
        self.assertIn("app", listed)
        code, out, _ = self.taskr("--dry-run", "app")
        self.assertEqual(0, code)
        self.assertLess(out.index("lib"), out.index("app"))
        self.assertFalse(os.path.exists(os.path.join(self.folder, "order.txt")))

    def test_another_file_with_f(self):
        os.makedirs(os.path.join(self.folder, "sub"))
        with open(os.path.join(self.folder, "sub", "build.json"), "w") as file:
            json.dump({"here": {"command": py("open('ran.txt','w').write('yes')")}}, file)
        self.assertEqual(0, self.taskr("-f", os.path.join("sub", "build.json"), "here")[0])
        self.assertTrue(os.path.exists(os.path.join(self.folder, "sub", "ran.txt")))  # in the file's folder


if __name__ == "__main__":
    unittest.main()

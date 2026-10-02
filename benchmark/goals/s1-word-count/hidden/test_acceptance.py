import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, run  # noqa: E402

APP = "src/WordCount"


class WordCount(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)
        cls.folder = tempfile.mkdtemp()
        cls.a = cls.write("a.txt", b"one two\nthree\n")
        cls.b = cls.write("b.txt", b"  four\tfive  six\n\nseven")

    @classmethod
    def write(cls, name, content):
        path = os.path.join(cls.folder, name)
        with open(path, "wb") as file:
            file.write(content)
        return path

    def test_counts_one_file(self):
        code, out, _ = run(APP, self.a)
        self.assertEqual((0, f"2 3 14 {self.a}"), (code, out.strip()))

    def test_counts_several_files_with_a_total(self):
        code, out, _ = run(APP, self.a, self.b)
        self.assertEqual(0, code)
        self.assertEqual([f"2 3 14 {self.a}", f"2 4 23 {self.b}", "4 7 37 total"], out.strip().splitlines())

    def test_reads_standard_input_without_a_path(self):
        code, out, _ = run(APP, stdin="a b c\n")
        self.assertEqual((0, "1 3 6"), (code, out.strip()))

    def test_options_choose_the_counts_in_a_fixed_order(self):
        self.assertEqual(f"3 {self.a}", run(APP, "-w", self.a)[1].strip())
        self.assertEqual(f"2 3 {self.a}", run(APP, "-wl", self.a)[1].strip())
        self.assertEqual(f"2 14 {self.a}", run(APP, "-c", "-l", self.a)[1].strip())

    def test_an_empty_input_counts_zero(self):
        self.assertEqual("0 0 0", run(APP, stdin="")[1].strip())

    def test_a_missing_file_is_reported_and_the_others_counted(self):
        missing = os.path.join(self.folder, "missing.txt")
        code, out, err = run(APP, missing, self.a)
        self.assertEqual(1, code)
        self.assertIn(f"wc: {missing}: No such file", err)
        self.assertIn(f"2 3 14 {self.a}", out)


if __name__ == "__main__":
    unittest.main()

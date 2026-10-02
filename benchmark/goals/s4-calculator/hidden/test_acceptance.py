import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, run  # noqa: E402

APP = "src/Calc"
CASES = {
    "1 + 2 * 3": "7", "(1 + 2) * 3": "9", "10 / 4": "2.5", "1/3": "0.3333333333", "0.1+0.2": "0.3",
    "7 % 3": "1", "2^3^2": "512", "-2^2": "-4", "(-2)^2": "4", "--3": "3", "  4 *( 2 - -1 )": "12",
    "2 * 3 / 4 - 1": "0.5", "100": "100", "1.50 * 2": "3",
}


class Calc(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def test_evaluates_with_precedence_and_associativity(self):
        for expression, result in CASES.items():
            code, out, _ = run(APP, expression)
            self.assertEqual((0, result), (code, out.strip()), expression)

    def test_reads_lines_and_continues_after_an_error(self):
        code, out, _ = run(APP, stdin="1+1\n2*(3\n6/3\n")
        lines = out.strip().splitlines()
        self.assertEqual("2", lines[0])
        self.assertTrue(lines[1].startswith("error: "))
        self.assertEqual("2", lines[2])

    def test_division_by_zero_is_an_error(self):
        for expression in ("1/0", "5 % 0", "1/(2-2)"):
            code, out, err = run(APP, expression)
            self.assertEqual(1, code, expression)
            self.assertIn("error: division by zero", out + err, expression)

    def test_an_invalid_expression_names_the_position(self):
        code, out, err = run(APP, "1 + * 2")
        self.assertEqual(1, code)
        self.assertIn("error: ", out + err)
        self.assertIn("5", out + err)
        for expression in ("", "(", "1 2", "2 +", "abc", "1..2"):
            self.assertEqual(1, run(APP, expression)[0], expression)


if __name__ == "__main__":
    unittest.main()

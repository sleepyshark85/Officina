import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, run  # noqa: E402

APP = "src/Roman"
KNOWN = {1: "I", 4: "IV", 9: "IX", 14: "XIV", 40: "XL", 90: "XC", 400: "CD", 1994: "MCMXCIV", 2024: "MMXXIV", 3999: "MMMCMXCIX"}


class Roman(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def test_numbers_to_numerals(self):
        for number, numeral in KNOWN.items():
            code, out, _ = run(APP, "to", str(number))
            self.assertEqual((0, numeral), (code, out.strip()), number)

    def test_numerals_to_numbers_in_either_case(self):
        for number, numeral in KNOWN.items():
            self.assertEqual(str(number), run(APP, "from", numeral)[1].strip())
        self.assertEqual("1994", run(APP, "from", "mcmxciv")[1].strip())

    def test_out_of_range_and_malformed_input_exits_2_with_a_reason(self):
        for args in (["to", "0"], ["to", "4000"], ["to", "-5"], ["to", "ten"], ["to", "2.5"],
                     ["from", "IIII"], ["from", "VX"], ["from", "IC"], ["from", "MMMM"], ["from", "ABC"], ["from", ""]):
            code, out, err = run(APP, *args)
            self.assertEqual(2, code, args)
            self.assertTrue(err.strip(), args)
            self.assertEqual("", out.strip(), args)

    def test_a_missing_or_unknown_command_prints_the_usage(self):
        for args in ([], ["convert", "5"]):
            code, out, err = run(APP, *args)
            self.assertEqual(2, code, args)
            self.assertIn("usage", (out + err).lower())

    def test_every_value_converts_back(self):
        for number in range(1, 4000, 37):
            numeral = run(APP, "to", str(number))[1].strip()
            self.assertEqual(str(number), run(APP, "from", numeral)[1].strip())


if __name__ == "__main__":
    unittest.main()

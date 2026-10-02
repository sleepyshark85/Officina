import json
import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, folder, run  # noqa: E402

APP = "src/CsvToJson"


class CsvToJson(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def convert(self, text, *args):
        code, out, err = run(APP, *args, stdin=text)
        self.assertEqual(0, code, err)
        return json.loads(out)

    def test_records_become_objects_keyed_by_the_header(self):
        self.assertEqual([{"name": "Ann", "age": "31"}, {"name": "Bo", "age": "4"}], self.convert("name,age\nAnn,31\nBo,4\n"))

    def test_the_keys_keep_the_headers_order(self):
        code, out, _ = run(APP, stdin="z,a,m\n1,2,3\n")
        self.assertLess(out.index('"z"'), out.index('"a"'))
        self.assertLess(out.index('"a"'), out.index('"m"'))

    def test_quoted_fields_hold_commas_line_breaks_and_quotes(self):
        text = 'id,note\r\n1,"a, b"\r\n2,"line one\r\nline two"\r\n3,"say ""hi"""\r\n'
        self.assertEqual(["a, b", "line one\r\nline two", 'say "hi"'], [record["note"] for record in self.convert(text)])

    def test_reads_the_named_file(self):
        path = os.path.join(folder(), "data.csv")
        with open(path, "w", newline="") as file:
            file.write("k\nv")
        self.assertEqual([{"k": "v"}], json.loads(run(APP, path)[1]))

    def test_types_turn_numbers_booleans_and_empty_fields(self):
        records = self.convert("n,d,b,e,s\n42,-1.5,true,,007x\n", "--types")
        self.assertEqual([{"n": 42, "d": -1.5, "b": True, "e": None, "s": "007x"}], records)

    def test_without_types_everything_is_a_string(self):
        self.assertEqual([{"n": "42", "e": ""}], self.convert("n,e\n42,\n"))

    def test_a_header_alone_is_an_empty_array(self):
        self.assertEqual([], self.convert("a,b\n"))

    def test_a_ragged_record_is_an_error_naming_its_line(self):
        code, out, err = run(APP, stdin="a,b\n1,2\n3\n")
        self.assertEqual((1, ""), (code, out.strip()))
        self.assertIn("3", err)


if __name__ == "__main__":
    unittest.main()

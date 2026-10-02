import gzip
import json
import os
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import build, run  # noqa: E402

APP = "src/LogReport"
LOG = """\
10.0.0.1 - - [10/Oct/2030:13:55:36 +0000] "GET /index.html HTTP/1.1" 200 1000
10.0.0.2 - ann [10/Oct/2030:13:59:59 +0000] "GET /index.html?x=1 HTTP/1.1" 200 500
10.0.0.1 - - [10/Oct/2030:14:00:00 +0000] "POST /api/items HTTP/1.1" 201 -
this line is not a log line
10.0.0.3 - - [10/Oct/2030:16:30:00 +0200] "GET /missing HTTP/1.1" 404 120
10.0.0.3 - - [10/Oct/2030:15:01:00 +0000] "GET /api/items HTTP/1.1" 500 80
"""


class LogReport(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)
        cls.folder = tempfile.mkdtemp()

    def report(self, *args, stdin=LOG):
        code, out, err = run(APP, *args, stdin=stdin)
        self.assertEqual(0, code, err)
        return json.loads(out)

    def test_reports_the_log(self):
        report = self.report()
        self.assertEqual((5, 1700, 3, 1), (report["requests"], report["bytes"], report["hosts"], report["malformed"]))
        self.assertEqual({"200": 2, "201": 1, "404": 1, "500": 1}, report["statuses"])
        self.assertEqual([{"path": "/api/items", "count": 2}, {"path": "/index.html", "count": 2}, {"path": "/missing", "count": 1}],
                         report["topPaths"])  # the query string is not part of the path; a tie goes by path
        self.assertEqual({"2030-10-10T13": 2, "2030-10-10T14": 2, "2030-10-10T15": 1}, report["perHour"])

    def test_filters_by_time_and_status(self):
        report = self.report("--from", "2030-10-10T14:00:00Z", "--to", "2030-10-10T15:00:00Z")
        self.assertEqual(2, report["requests"])  # 14:00:00 and 16:30:00+02:00
        self.assertEqual({"404": 1}, self.report("--status", "4xx")["statuses"])
        self.assertEqual({"500": 1}, self.report("--status", "500")["statuses"])
        self.assertEqual(["/api/items"], [entry["path"] for entry in self.report("--top", "1")["topPaths"]])

    def test_reads_files_and_gzip(self):
        plain = os.path.join(self.folder, "a.log")
        with open(plain, "w") as file:
            file.write(LOG)
        packed = os.path.join(self.folder, "b.log.gz")
        with gzip.open(packed, "wt") as file:
            file.write(LOG)
        self.assertEqual(10, self.report(plain, packed, stdin="")["requests"])

    def test_text_format(self):
        code, out, _ = run(APP, "--format", "text", stdin=LOG)
        self.assertEqual(0, code)
        self.assertIn("/index.html", out)
        self.assertRaises(ValueError, json.loads, out)

    def test_an_unreadable_file_exits_1(self):
        code, _, err = run(APP, os.path.join(self.folder, "none.log"))
        self.assertEqual(1, code)
        self.assertTrue(err.strip())

    def test_a_large_log_is_streamed(self):
        large = os.path.join(self.folder, "large.log")
        line = '10.0.0.9 - - [10/Oct/2030:13:55:36 +0000] "GET /p/{0} HTTP/1.1" 200 10\n'
        with open(large, "w") as file:
            for n in range(1_300_000):
                file.write(line.format(n % 50))
        started = time.monotonic()
        code, out, _ = run(APP, large, timeout=120)
        self.assertEqual(0, code)
        self.assertLess(time.monotonic() - started, 30)
        self.assertEqual(1_300_000, json.loads(out)["requests"])


if __name__ == "__main__":
    unittest.main()

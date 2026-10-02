import os
import re
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import Service, build  # noqa: E402

APP = "src/Shortener"
KEY = {"X-Api-Key": "s3cret"}


class Shortener(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def setUp(self):
        self.service = Service(APP, {"SHORTENER_DB": "{data}/links.db", "SHORTENER_KEY": "s3cret"}).start()
        self.addCleanup(self.service.stop)

    def shorten(self, url, code=None, expect=201):
        body = {"url": url} if code is None else {"url": url, "code": code}
        status, _, link = self.service.call("POST", "/api/links", body, KEY)
        self.assertEqual(expect, status, link)
        return link

    def test_a_generated_code_redirects_and_counts_visits(self):
        link = self.shorten("https://example.com/a?b=c")
        self.assertRegex(link["code"], r"^[A-Za-z0-9]{7}$")
        self.assertTrue(link["shortUrl"].endswith("/" + link["code"]))
        self.assertIsNone(self.service.call("GET", f"/api/links/{link['code']}", headers=KEY)[2]["lastVisitedAt"])
        for _ in range(3):
            status, headers, _ = self.service.call("GET", f"/{link['code']}", follow=False)
            self.assertEqual((302, "https://example.com/a?b=c"), (status, headers["Location"]))
        stats = self.service.call("GET", f"/api/links/{link['code']}", headers=KEY)[2]
        self.assertEqual(3, stats["visits"])
        self.assertIsNotNone(stats["lastVisitedAt"])

    def test_a_chosen_code_is_validated_and_unique(self):
        self.shorten("http://example.com", "my-link_1")
        self.shorten("http://example.org", "my-link_1", expect=409)
        for bad in ("ab", "x" * 33, "has space", "slash/no"):
            self.shorten("http://example.com", bad, expect=400)

    def test_only_absolute_http_urls(self):
        for bad in ("example.com", "ftp://example.com", "javascript:alert(1)", "/relative", ""):
            self.shorten(bad, expect=400)

    def test_unknown_codes_are_not_found(self):
        self.assertEqual(404, self.service.call("GET", "/nothing1", follow=False)[0])
        self.assertEqual(404, self.service.call("GET", "/api/links/nothing1", headers=KEY)[0])

    def test_a_deleted_code_redirects_nowhere_and_is_not_reused(self):
        self.shorten("https://example.com", "gone")
        self.assertEqual(204, self.service.call("DELETE", "/api/links/gone", headers=KEY)[0])
        self.assertEqual(404, self.service.call("GET", "/gone", follow=False)[0])
        self.assertIn(self.service.call("POST", "/api/links", {"url": "https://example.com", "code": "gone"}, KEY)[0], (400, 409))

    def test_the_api_needs_the_key_and_redirects_do_not(self):
        self.shorten("https://example.com", "open")
        self.assertEqual(401, self.service.call("POST", "/api/links", {"url": "https://example.com"})[0])
        self.assertEqual(401, self.service.call("GET", "/api/links", headers={"X-Api-Key": "wrong"})[0])
        self.assertEqual(302, self.service.call("GET", "/open", follow=False)[0])

    def test_lists_newest_first_and_survives_a_restart(self):
        codes = [self.shorten(f"https://example.com/{n}", f"code{n}")["code"] for n in range(3)]
        self.service.call("GET", "/code1", follow=False)
        self.service.restart()
        listed = self.service.call("GET", "/api/links", headers=KEY)[2]
        self.assertEqual(list(reversed(codes)), [link["code"] for link in listed if re.fullmatch(r"code\d", link["code"])])
        self.assertEqual(1, self.service.call("GET", "/api/links/code1", headers=KEY)[2]["visits"])


if __name__ == "__main__":
    unittest.main()

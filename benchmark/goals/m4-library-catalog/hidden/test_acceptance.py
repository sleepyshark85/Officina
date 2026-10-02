import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import Service, build  # noqa: E402

APP = "src/Library"
ISBNS = ["9780306406157", "9781861972712", "9780131103627", "9780201633610", "9780596007126"]


class Library(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def setUp(self):
        self.service = Service(APP, {"LIBRARY_DB": "{data}/library.db", "LIBRARY_TODAY": "2030-03-01"}).start()
        self.addCleanup(self.service.stop)

    def post(self, path, body, expect=201):
        status, _, created = self.service.call("POST", path, body)
        self.assertEqual(expect, status, created)
        return created

    def book(self, isbn, title, authors, copies=1, year=2000):
        return self.post("/books", {"isbn": isbn, "title": title, "year": year, "authorIds": authors, "copies": copies})

    def test_books_need_a_valid_unique_isbn_and_existing_authors(self):
        author = self.post("/authors", {"name": "Ada Lovelace"})["id"]
        self.book(ISBNS[0], "Notes", [author])
        self.post("/books", {"isbn": ISBNS[0], "title": "Again", "year": 2000, "authorIds": [author], "copies": 1}, 409)
        status, _, body = self.service.call("POST", "/books", {"isbn": "9780306406158", "title": "", "year": 2000, "authorIds": [], "copies": 1})
        self.assertEqual(400, status)
        self.assertTrue({"isbn", "title", "authorids"} <= {name.lower() for name in body["errors"]})
        self.assertIn(self.service.call("POST", "/books", {"isbn": ISBNS[1], "title": "X", "year": 2000, "authorIds": [999999], "copies": 1})[0], (400, 404))

    def test_an_author_with_books_cannot_be_deleted(self):
        author = self.post("/authors", {"name": "Grace Hopper"})["id"]
        book = self.book(ISBNS[2], "Compilers", [author])["id"]
        self.assertEqual(409, self.service.call("DELETE", f"/authors/{author}")[0])
        self.assertIn(self.service.call("DELETE", f"/books/{book}")[0], (200, 204))
        self.assertIn(self.service.call("DELETE", f"/authors/{author}")[0], (200, 204))
        self.assertEqual(404, self.service.call("GET", f"/authors/{author}")[0])

    def test_members_have_unique_emails(self):
        self.post("/members", {"name": "Ann", "email": "ann@example.com"})
        self.post("/members", {"name": "Ann B", "email": "ann@example.com"}, 409)
        self.post("/members", {"name": "Bo", "email": "not-an-email"}, 400)

    def test_loans_respect_copies_and_limits(self):
        author = self.post("/authors", {"name": "Edsger Dijkstra"})["id"]
        books = [self.book(isbn, f"Book {n}", [author], copies=1)["id"] for n, isbn in enumerate(ISBNS[:4])]
        ann = self.post("/members", {"name": "Ann", "email": "ann@example.com"})["id"]
        bo = self.post("/members", {"name": "Bo", "email": "bo@example.com"})["id"]
        loans = [self.post("/loans", {"bookId": book, "memberId": ann}) for book in books[:3]]
        self.assertEqual("2030-03-15", loans[0]["dueOn"])
        self.post("/loans", {"bookId": books[3], "memberId": ann}, 409)  # three loans already
        self.post("/loans", {"bookId": books[0], "memberId": bo}, 409)  # no free copy
        self.assertEqual(3, len(self.service.call("GET", f"/members/{ann}/loans")[2]))
        self.assertIn(self.service.call("POST", f"/loans/{loans[0]['id']}/return")[0], (200, 204))
        self.post("/loans", {"bookId": books[0], "memberId": bo})
        self.assertEqual(2, len(self.service.call("GET", f"/members/{ann}/loans")[2]))

    def test_an_overdue_loan_stops_new_ones(self):
        author = self.post("/authors", {"name": "Barbara Liskov"})["id"]
        first, second = (self.book(isbn, title, [author])["id"] for isbn, title in ((ISBNS[0], "One"), (ISBNS[1], "Two")))
        ann = self.post("/members", {"name": "Ann", "email": "ann@example.com"})["id"]
        self.post("/loans", {"bookId": first, "memberId": ann})
        self.service.environment["LIBRARY_TODAY"] = "2030-04-01"
        self.service.restart()
        self.post("/loans", {"bookId": second, "memberId": ann}, 409)

    def test_search_by_title_or_author_with_availability_and_paging(self):
        knuth = self.post("/authors", {"name": "Donald Knuth"})["id"]
        other = self.post("/authors", {"name": "Someone Else"})["id"]
        art = self.book(ISBNS[0], "The Art of Programming", [knuth])["id"]
        self.book(ISBNS[1], "Concrete Mathematics", [knuth, other], copies=2)
        self.book(ISBNS[2], "art and craft", [other])
        member = self.post("/members", {"name": "Ann", "email": "ann@example.com"})["id"]
        self.post("/loans", {"bookId": art, "memberId": member})
        found = self.service.call("GET", "/books?q=ART")[2]
        self.assertEqual((2, ["art and craft", "The Art of Programming"]), (found["total"], [book["title"] for book in found["items"]]))
        self.assertEqual(2, self.service.call("GET", "/books?q=knuth")[2]["total"])
        self.assertEqual(["Concrete Mathematics"], [book["title"] for book in self.service.call("GET", "/books?q=knuth&available=true")[2]["items"]])
        page = self.service.call("GET", "/books?page=2&pageSize=2")[2]
        self.assertEqual((3, 1), (page["total"], len(page["items"])))

    def test_everything_survives_a_restart(self):
        author = self.post("/authors", {"name": "Alan Kay"})["id"]
        book = self.book(ISBNS[4], "Smalltalk", [author])
        self.service.restart()
        self.assertEqual(book, self.service.call("GET", f"/books/{book['id']}")[2])


if __name__ == "__main__":
    unittest.main()

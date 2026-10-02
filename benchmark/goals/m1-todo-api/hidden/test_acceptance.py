import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import Service, build  # noqa: E402

APP = "src/TodoApi"


class TodoApi(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def setUp(self):
        self.service = Service(APP, {"TODO_DB": "{data}/todos.db"}).start()
        self.addCleanup(self.service.stop)

    def create(self, **todo):
        status, headers, body = self.service.call("POST", "/todos", todo)
        self.assertEqual(201, status, body)
        return headers, body

    def test_creates_reads_changes_and_deletes(self):
        headers, todo = self.create(title="  Buy milk  ", due="2030-01-31")
        self.assertEqual(("Buy milk", False, "2030-01-31"), (todo["title"], todo["done"], todo["due"]))
        self.assertIn("createdAt", todo)
        self.assertTrue(headers["Location"].endswith(f"/todos/{todo['id']}"))
        self.assertEqual(todo, self.service.call("GET", f"/todos/{todo['id']}")[2])
        status, _, changed = self.service.call("PATCH", f"/todos/{todo['id']}", {"done": True})
        self.assertEqual((200, True, "Buy milk"), (status, changed["done"], changed["title"]))
        self.assertEqual(204, self.service.call("DELETE", f"/todos/{todo['id']}")[0])
        self.assertEqual(404, self.service.call("GET", f"/todos/{todo['id']}")[0])

    def test_invalid_input_names_each_field(self):
        status, _, body = self.service.call("POST", "/todos", {"title": "   ", "due": "31/01/2030"})
        self.assertEqual(400, status)
        self.assertEqual({"title", "due"}, {name.lower() for name in body["errors"]})
        self.assertEqual(400, self.service.call("POST", "/todos", {"title": "x" * 201})[0])
        _, todo = self.create(title="ok")
        self.assertEqual(400, self.service.call("PATCH", f"/todos/{todo['id']}", {"title": ""})[0])

    def test_unknown_ids_are_not_found(self):
        for method, body in (("GET", None), ("PATCH", {"done": True}), ("DELETE", None)):
            self.assertEqual(404, self.service.call(method, "/todos/999999", body)[0], method)

    def test_lists_filtered_and_paged(self):
        ids = [self.create(title=f"t{n}", due=f"2030-01-{n + 1:02d}")[1]["id"] for n in range(25)]
        for id_ in ids[:5]:
            self.service.call("PATCH", f"/todos/{id_}", {"done": True})
        page = self.service.call("GET", "/todos")[2]
        self.assertEqual((25, ids[:20]), (page["total"], [todo["id"] for todo in page["items"]]))
        page = self.service.call("GET", "/todos?page=2&pageSize=10")[2]
        self.assertEqual(ids[10:20], [todo["id"] for todo in page["items"]])
        self.assertEqual(5, self.service.call("GET", "/todos?done=true")[2]["total"])
        self.assertEqual(20, self.service.call("GET", "/todos?done=false")[2]["total"])
        self.assertEqual(ids[:3], [todo["id"] for todo in self.service.call("GET", "/todos?dueBefore=2030-01-04")[2]["items"]])
        self.assertEqual(400, self.service.call("GET", "/todos?pageSize=101")[0])
        self.assertEqual(400, self.service.call("GET", "/todos?page=0")[0])

    def test_everything_survives_a_restart(self):
        _, todo = self.create(title="Keep me")
        self.service.restart()
        self.assertEqual(todo, self.service.call("GET", f"/todos/{todo['id']}")[2])
        _, second = self.create(title="Next")
        self.assertNotEqual(todo["id"], second["id"])


if __name__ == "__main__":
    unittest.main()

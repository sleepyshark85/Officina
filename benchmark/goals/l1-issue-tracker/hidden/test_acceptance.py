import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..")))
from harness import Service, build  # noqa: E402

APP = "src/Tracker"


class Tracker(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build(APP)

    def setUp(self):
        self.service = Service(APP, {"TRACKER_DB": "{data}/tracker.db"}).start()
        self.addCleanup(self.service.stop)
        self.admin = self.user("Admin")
        self.ann = self.user("Ann")
        self.bo = self.user("Bo")

    def user(self, name):
        status, _, user = self.service.call("POST", "/users", {"name": name, "email": f"{name.lower()}@example.com"})
        self.assertEqual(201, status, user)
        return user

    def as_(self, user, method, path, body=None):
        return self.service.call(method, path, body, {"Authorization": f"Bearer {user['token']}"})

    def project(self, owner, key="CORE", members=()):
        status, _, created = self.as_(owner, "POST", "/projects", {"key": key, "name": f"Project {key}"})
        self.assertEqual(201, status, created)
        for member in members:
            self.assertIn(self.as_(owner, "POST", f"/projects/{key}/members", {"userId": member["id"]})[0], (200, 201, 204))
        return key

    def issue(self, user, key, title, expect=201, **fields):
        body = {"title": title, "description": "", "type": "bug", "priority": "medium", "labels": [], **fields}
        status, _, issue = self.as_(user, "POST", f"/projects/{key}/issues", body)
        self.assertEqual(expect, status, issue)
        return issue

    def test_requests_need_a_valid_token(self):
        self.assertEqual(401, self.service.call("GET", "/projects")[0])
        self.assertEqual(401, self.service.call("GET", "/projects", headers={"Authorization": "Bearer nope"})[0])
        self.assertNotIn("token", self.as_(self.ann, "GET", f"/users/{self.ann['id']}")[2] or {})

    def test_projects_are_seen_only_by_their_members(self):
        key = self.project(self.ann, "CORE")
        self.issue(self.ann, key, "Crash")
        self.assertEqual(403, self.as_(self.bo, "GET", f"/projects/{key}/issues")[0])
        self.assertEqual(403, self.as_(self.bo, "GET", f"/issues/{key}-1")[0])
        self.assertEqual(403, self.as_(self.bo, "POST", f"/projects/{key}/members", {"userId": self.bo["id"]})[0])
        self.assertNotIn(key, [project["key"] for project in self.as_(self.bo, "GET", "/projects")[2]["items"]])
        self.assertIn(key, [project["key"] for project in self.as_(self.admin, "GET", "/projects")[2]["items"]])
        self.as_(self.admin, "POST", f"/projects/{key}/members", {"userId": self.bo["id"]})
        self.assertEqual(200, self.as_(self.bo, "GET", f"/issues/{key}-1")[0])

    def test_project_keys_are_validated_and_unique(self):
        self.project(self.ann, "WEB")
        self.assertEqual(409, self.as_(self.bo, "POST", "/projects", {"key": "WEB", "name": "Again"})[0])
        for bad in ("W", "web", "TOOLONGKEYXX", "W3B"):
            self.assertEqual(400, self.as_(self.ann, "POST", "/projects", {"key": bad, "name": "Bad"})[0], bad)

    def test_issues_are_numbered_per_project_and_validated(self):
        core = self.project(self.ann, "CORE", [self.bo])
        web = self.project(self.ann, "WEB")
        self.assertEqual(["CORE-1", "CORE-2", "WEB-1"], [self.issue(self.ann, core, "a")["id"], self.issue(self.ann, core, "b")["id"], self.issue(self.ann, web, "c")["id"]])
        self.issue(self.ann, core, "", 400)
        self.issue(self.ann, core, "x", 400, type="epic")
        self.issue(self.ann, core, "x", 400, priority="urgent")
        self.issue(self.ann, core, "x", 400, assigneeId=self.admin["id"])  # not a member
        self.issue(self.ann, core, "x", assigneeId=self.bo["id"])

    def test_status_moves_only_along_the_workflow(self):
        key = self.project(self.ann)
        issue = self.issue(self.ann, key, "Flow")["id"]
        move = lambda to: self.as_(self.ann, "POST", f"/issues/{issue}/transitions", {"to": to})[0]  # noqa: E731
        self.assertEqual(409, move("done"))
        for to in ("in_progress", "in_review", "in_progress", "in_review", "done"):
            self.assertEqual(200, move(to), to)
        self.assertEqual(409, move("open"))
        self.assertEqual(200, move("closed"))
        self.assertEqual("closed", self.as_(self.ann, "GET", f"/issues/{issue}")[2]["status"])

    def test_changes_are_kept_in_the_history(self):
        key = self.project(self.ann, members=[self.bo])
        issue = self.issue(self.ann, key, "Old title")["id"]
        self.assertEqual(200, self.as_(self.bo, "PATCH", f"/issues/{issue}", {"title": "New title"})[0])
        self.as_(self.ann, "POST", f"/issues/{issue}/transitions", {"to": "in_progress"})
        entries = self.as_(self.ann, "GET", f"/issues/{issue}/history")[2]["items"]
        title = next(entry for entry in entries if entry["field"] == "title")
        self.assertEqual(("Old title", "New title", self.bo["id"]), (title["old"], title["new"], title["userId"]))
        self.assertTrue(any(entry["field"] == "status" for entry in entries))

    def test_only_a_comments_author_edits_it(self):
        key = self.project(self.ann, members=[self.bo])
        issue = self.issue(self.ann, key, "Talk")["id"]
        status, _, comment = self.as_(self.bo, "POST", f"/issues/{issue}/comments", {"body": "First!"})
        self.assertEqual(201, status)
        self.assertEqual(403, self.as_(self.ann, "PUT", f"/issues/{issue}/comments/{comment['id']}", {"body": "Changed"})[0])
        self.assertEqual(200, self.as_(self.bo, "PUT", f"/issues/{issue}/comments/{comment['id']}", {"body": "Edited"})[0])
        self.assertEqual(["Edited"], [entry["body"] for entry in self.as_(self.ann, "GET", f"/issues/{issue}/comments")[2]["items"]])

    def test_lists_filtered_sorted_and_paged(self):
        key = self.project(self.ann, members=[self.bo])
        self.issue(self.ann, key, "Login fails", priority="critical", labels=["auth"], assigneeId=self.bo["id"])
        self.issue(self.ann, key, "Typo on page", priority="low", type="task")
        self.issue(self.ann, key, "Logout slow", priority="high", labels=["auth", "perf"])
        query = lambda q: [issue["title"] for issue in self.as_(self.ann, "GET", f"/projects/{key}/issues?{q}")[2]["items"]]  # noqa: E731
        self.assertEqual(["Login fails", "Logout slow"], query("label=auth&sort=-priority"))
        self.assertEqual(["Typo on page", "Logout slow", "Login fails"], query("sort=priority"))
        self.assertEqual(["Login fails"], query(f"assigneeId={self.bo['id']}"))
        self.assertEqual(["Typo on page"], query("type=task"))
        self.assertEqual(["Logout slow"], query("q=slow"))
        page = self.as_(self.ann, "GET", f"/projects/{key}/issues?sort=created&page=2&pageSize=2")[2]
        self.assertEqual((3, ["Logout slow"]), (page["total"], [issue["title"] for issue in page["items"]]))

    def test_everything_survives_a_restart(self):
        key = self.project(self.ann)
        issue = self.issue(self.ann, key, "Persist")
        self.service.restart()
        self.assertEqual(issue["title"], self.as_(self.ann, "GET", f"/issues/{issue['id']}")[2]["title"])
        self.assertEqual(f"{key}-2", self.issue(self.ann, key, "Next")["id"])


if __name__ == "__main__":
    unittest.main()

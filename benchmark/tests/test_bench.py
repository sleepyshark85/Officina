"""The benchmark runner, scoring and report, against a stand-in for sof (fake_sof.py), so no model is called.

    python3 -m unittest discover -s benchmark/tests
"""

import os
import random
import shutil
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import bench  # noqa: E402
import report  # noqa: E402
from scoring import BENCHMARK  # noqa: E402

FAKE = [sys.executable, os.path.join(HERE, "fake_sof.py")]
GOAL = "s1-word-count"


class Runner(unittest.TestCase):
    def setUp(self):
        self.results = tempfile.mkdtemp(prefix="bench-results-")
        self.addCleanup(shutil.rmtree, self.results, True)

    def run_goal(self, solution, timeout=600, **environment):
        environment = {**environment, "FAKE_SOF_SOLUTION": solution} if solution else environment
        with mock.patch.dict(os.environ, environment):
            if not solution:
                os.environ.pop("FAKE_SOF_SOLUTION", None)
            result = bench.run_once(FAKE, GOAL, 1, self.results, random.Random(7), max_cost=40, timeout=timeout)
        if os.path.isdir(result["workspace"]):
            self.addCleanup(bench.remove, result["workspace"])
        return result

    def test_a_run_restarts_once_answers_only_sign_offs_and_scores_its_baseline(self):
        result = self.run_goal(os.path.join(BENCHMARK, "reference", GOAL))

        self.assertTrue(result["success"], result["baseline"])
        self.assertTrue(result["restarted"])
        self.assertEqual(2, len(result["exitCodes"]))  # the killed run, then the resumed one
        self.assertEqual((1, 1), (result["humanInputs"], result["declined"]))  # the plan's sign-off; the command is declined
        self.assertEqual((1.0, 10, 10000), (result["cost"], result["modelCalls"], result["tokens"]))
        self.assertEqual(6, result["baseline"]["hidden"]["ran"])
        self.assertFalse(os.path.exists(result["workspace"]))  # a successful run's workspace is removed
        with open(os.path.join(self.results, f"{GOAL}-1.log"), encoding="utf-8") as log:
            self.assertIn("resuming", log.read())

    def test_the_model_calls_a_resumed_run_shows_again_are_counted_once(self):
        result = self.run_goal(os.path.join(BENCHMARK, "reference", GOAL), FAKE_SOF_NO_REPORT="1")

        self.assertTrue(result["restarted"])
        self.assertEqual(10, result["modelCalls"])  # counted from the console, as the report failed

    def test_a_run_past_its_timeout_is_stopped_and_fails(self):
        result = self.run_goal(None, timeout=3, FAKE_SOF_HANG="1")

        self.assertEqual((True, False), (result["timedOut"], result["success"]))
        self.assertLess(result["wallSeconds"], 60)

    def test_the_runner_refuses_to_start_without_accepting_unsandboxed_scoring(self):
        with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
            bench.main(["--sof", " ".join(FAKE), "--goals", GOAL])

    def test_a_run_whose_baseline_does_not_build_fails_and_keeps_its_workspace(self):
        result = self.run_goal(None)

        self.assertFalse(result["success"])
        self.assertFalse(result["baseline"]["checks"]["build"]["passed"])
        self.assertTrue(os.path.isdir(result["workspace"]))


class Report(unittest.TestCase):
    @staticmethod
    def run_result(goal, number, success, system="Linux", restarted=True):
        return {
            "goal": goal, "tier": "small", "run": number, "os": system, "success": success, "restarted": restarted, "cost": 2.0,
            "wallSeconds": 600, "humanInputs": 1, "declined": 0, "stoppedForCost": False,
            "baseline": {"checks": {"build": {"passed": True}, "tests": {"passed": True}}, "hidden": {"ran": 6, "failed": 0 if success else 2}},
        }

    def test_the_target_is_met_at_90_percent_with_three_runs_of_each_goal(self):
        goals = ["s1", "s2", "m1"]
        runs = [self.run_result(goal, number, not (goal == "s1" and number == 1 and system == "Linux"), system)
                for system in report.SYSTEMS for goal in goals for number in range(1, 4)]
        text, met = report.summarise(runs, 0.9, goals)
        self.assertTrue(met)
        self.assertIn("17 of 18 runs succeeded: 94%", text)
        self.assertIn("| s1 #1 (Linux) | 2 of 6 hidden tests failed |", text)

    def test_too_few_runs_or_a_run_never_restarted_misses_the_target(self):
        full = [self.run_result("s1", number, True, system) for system in report.SYSTEMS for number in range(1, 4)]
        self.assertTrue(report.summarise(full, 0.9, ["s1"])[1])
        self.assertFalse(report.summarise(full[1:], 0.9, ["s1"])[1])
        self.assertFalse(report.summarise([run | {"restarted": run["run"] != 2} for run in full], 0.9, ["s1"])[1])

    def test_the_target_needs_both_systems_and_every_goal(self):
        linux = [self.run_result(goal, number, True) for goal in ("s1", "s2") for number in range(1, 4)]
        text, met = report.summarise(linux, 0.9, ["s1", "s2"])
        self.assertFalse(met)
        self.assertIn("s1 has 0 runs on Windows; it needs 3.", text)
        both = linux + [run | {"os": "Windows"} for run in linux]
        self.assertTrue(report.summarise(both, 0.9, ["s1", "s2"])[1])
        text, met = report.summarise(both, 0.9, ["s1", "s2", "m1"])
        self.assertFalse(met)
        self.assertIn("m1 has 0 runs on Linux; it needs 3.", text)


if __name__ == "__main__":
    unittest.main()

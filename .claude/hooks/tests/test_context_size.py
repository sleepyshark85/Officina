"""Tests for context-size.py, against small transcripts written per test. Run: python3 -m unittest discover -s
.claude/hooks/tests"""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "context-size.py")
spec = importlib.util.spec_from_file_location("context_size", SCRIPT)
hook = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hook)


def assistant(input_tokens, cache_read=0, cache_creation=0):
    return {"type": "assistant", "message": {"role": "assistant", "usage": {
        "input_tokens": input_tokens, "cache_read_input_tokens": cache_read,
        "cache_creation_input_tokens": cache_creation, "output_tokens": 50}}}


def tool_result():
    return {"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "content": "ok"}]}}


class ContextSizeTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.session = os.path.join(self.folder.name, "session")

    def write(self, path, entries, tail=""):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as file:
            file.writelines(json.dumps(entry) + "\n" for entry in entries)
            file.write(tail)
        return path

    def main_transcript(self, *entries):
        return self.write(self.session + ".jsonl", entries)

    def subagent_transcript(self, agent, *entries):
        return self.write(os.path.join(self.session, "subagents", f"agent-{agent}.jsonl"), entries)

    def test_size_is_the_latest_requests_input_cache_reads_and_cache_writes(self):
        path = self.main_transcript(assistant(5, 1_000, 200), tool_result(), assistant(2, 120_000, 3_000), tool_result())
        self.assertEqual(hook.context_size(path), 123_002)

    def test_size_skips_requests_without_usage_and_a_line_still_being_written(self):
        path = self.write(self.session + ".jsonl", [assistant(10, 500), assistant(0)], tail='{"type": "assist')
        self.assertEqual(hook.context_size(path), 510)

    def test_size_is_none_before_the_first_model_request(self):
        self.assertIsNone(hook.context_size(self.main_transcript(tool_result())))

    def test_entries_are_read_last_first_across_chunks(self):
        self.patch("CHUNK", 16)
        path = self.write(self.session + ".jsonl", [{"n": n, "pad": "x" * n} for n in range(20)])
        self.assertEqual([entry["n"] for entry in hook.entries(path)], list(reversed(range(20))))

    def test_entries_stop_at_the_tail_limit(self):
        self.patch("CHUNK", 64)
        self.patch("TAIL_LIMIT", 256)
        path = self.main_transcript(assistant(9_000), *[tool_result()] * 10)
        self.assertIsNone(hook.context_size(path))

    def test_crossing_returns_the_highest_threshold_passed(self):
        self.assertIsNone(hook.crossed(0, 149_999))
        self.assertEqual(hook.crossed(149_999, 150_000), 150_000)
        self.assertEqual(hook.crossed(100_000, 310_000), 300_000)
        self.assertIsNone(hook.crossed(160_000, 290_000))
        self.assertIsNone(hook.crossed(150_000, 160_000))
        self.assertEqual(hook.crossed(160_000, 300_001), 300_000)

    def test_a_subagent_is_warned_once_per_threshold(self):
        payload = {"transcript_path": self.main_transcript(assistant(1, 1_000)), "agent_id": "a1",
                   "agent_type": "developer"}
        self.subagent_transcript("a1", assistant(1, 100_000))
        self.assertIsNone(hook.on_tool_use(payload, self.session))
        self.subagent_transcript("a1", assistant(1, 160_000))
        output = hook.on_tool_use(payload, self.session)
        self.assertEqual(output["hookSpecificOutput"]["hookEventName"], "PostToolUse")
        self.assertIn("past 150,000", output["hookSpecificOutput"]["additionalContext"])
        self.assertIsNone(hook.on_tool_use(payload, self.session))
        logged = [entry["tokens"] for entry in hook.entries(os.path.join(self.session, "context-sizes", "a1.jsonl"))]
        self.assertEqual(logged, [160_001, 160_001, 100_001])

    def test_the_main_agent_is_logged_from_the_session_transcript(self):
        payload = {"transcript_path": self.main_transcript(assistant(1, 2_000))}
        hook.on_tool_use(payload, self.session)
        latest = next(hook.entries(os.path.join(self.session, "context-sizes", "main.jsonl")))
        self.assertEqual((latest["agent_type"], latest["tokens"]), ("main", 2_001))

    def test_report_lists_the_latest_size_per_agent_largest_first(self):
        sizes = os.path.join(self.session, "context-sizes")
        self.write(os.path.join(sizes, "main.jsonl"), [{"time": "t1", "agent_type": "main", "tokens": 40_000}])
        self.write(os.path.join(sizes, "a1.jsonl"), [{"time": "t1", "agent_type": "developer", "tokens": 90_000},
                                                     {"time": "t2", "agent_type": "developer", "tokens": 200_000}])
        lines = hook.report(sizes).splitlines()
        self.assertEqual([line.split()[:3] for line in lines[1:]], [["a1", "developer", "200,000"],
                                                                    ["main", "main", "40,000"]])

    def test_a_failing_hook_still_exits_zero_and_logs_the_problem(self):
        payload = {"transcript_path": self.session + ".jsonl", "agent_id": "missing"}
        result = self.run_hook(json.dumps(payload))
        self.assertEqual((result.returncode, result.stdout), (0, ""))
        errors = list(hook.entries(os.path.join(self.session, "context-sizes", "errors.log")))
        self.assertIn("FileNotFoundError", errors[0]["error"])

    def test_unreadable_input_still_exits_zero(self):
        result = self.run_hook("not json")
        self.assertEqual((result.returncode, result.stdout), (0, ""))
        self.assertIn("context-size hook", result.stderr)

    def run_hook(self, stdin):
        return subprocess.run([sys.executable, SCRIPT], input=stdin, capture_output=True, text=True, check=False)

    def patch(self, name, value):
        original = getattr(hook, name)
        setattr(hook, name, value)
        self.addCleanup(setattr, hook, name, original)


if __name__ == "__main__":
    unittest.main()

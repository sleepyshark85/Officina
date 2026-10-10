"""Tests for agent-usage.py, against small transcripts written per test. Run: python3 -B -m unittest discover -s
scripts/tests"""
import importlib.util
import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "agent-usage.py"
spec = importlib.util.spec_from_file_location("agent_usage", SCRIPT)
agent_usage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent_usage)


def assistant(message_id, minute, input_tokens, cache_read=0, cache_write=0):
    return {"type": "assistant", "timestamp": f"2026-10-05T10:{minute:02}:00.000Z", "message": {
        "id": message_id, "role": "assistant", "usage": {
            "input_tokens": input_tokens, "cache_read_input_tokens": cache_read,
            "cache_creation_input_tokens": cache_write, "output_tokens": 5}}}


def user(minute):
    return {"type": "user", "timestamp": f"2026-10-05T10:{minute:02}:00.000Z",
            "message": {"role": "user", "content": "go on"}}


class AgentUsageTest(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.directory = Path(folder.name)

    def write(self, path, entries):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("".join(json.dumps(entry) + "\n" for entry in entries), encoding="utf-8")
        return path

    def lead(self, session_id, *entries):
        return self.write(self.directory / f"{session_id}.jsonl", entries)

    def subagent(self, session_id, agent_id, meta, *entries):
        folder = self.directory / session_id / "subagents"
        self.write(folder / f"agent-{agent_id}.meta.json", [meta])
        return self.write(folder / f"agent-{agent_id}.jsonl", entries)

    def test_a_message_repeated_across_streamed_entries_is_counted_once(self):
        path = self.lead("s", assistant("m1", 0, 10, 100, 5), assistant("m1", 0, 10, 100, 5), user(1),
                         assistant("m2", 2, 20, 200))
        usage = agent_usage.read_usage(path)
        self.assertEqual((usage.calls, usage.processed, usage.cache_reads), (2, 335, 300))

    def test_peak_context_is_the_largest_single_requests_input_cache_reads_and_cache_writes(self):
        path = self.lead("s", assistant("m1", 0, 1, 50, 9), assistant("m2", 1, 2, 30), assistant("m3", 2, 3, 40))
        self.assertEqual(agent_usage.read_usage(path).peak_context, 60)

    def test_active_time_leaves_out_gaps_of_the_idle_threshold_or_more(self):
        path = self.lead("s", assistant("m1", 0, 1), user(5), assistant("m2", 35, 1), user(40))
        self.assertEqual(agent_usage.read_usage(path).active, timedelta(minutes=10))

    def test_sessions_come_out_oldest_first_whatever_their_ids(self):
        self.lead("aaaa", assistant("m1", 30, 1))
        self.lead("bbbb", assistant("m2", 10, 1))
        self.lead("cccc", assistant("m3", 20, 1))
        ids = [session.id for session in agent_usage.read_sessions(self.directory)]
        self.assertEqual(ids, ["bbbb", "cccc", "aaaa"])

    def test_a_session_without_subagents_is_kept_with_its_lead(self):
        self.lead("solo", assistant("m1", 0, 7))
        [session] = agent_usage.read_sessions(self.directory)
        self.assertEqual([(agent.name, agent.role, agent.usage.processed) for agent in session.agents],
                         [("lead", "lead", 7)])

    def test_a_session_without_any_model_call_is_left_out(self):
        self.lead("empty", user(0))
        self.assertEqual(agent_usage.read_sessions(self.directory), [])

    def test_a_session_whose_lead_made_no_model_call_is_kept_with_its_subagents(self):
        self.lead("s", user(0))
        self.subagent("s", "a1234567890", {"agentType": "developer", "description": "Build"}, assistant("d", 5, 1))
        [session] = agent_usage.read_sessions(self.directory)
        self.assertEqual([agent.name for agent in session.agents], ["a1234567"])

    def test_subagents_follow_the_lead_in_the_order_they_started(self):
        self.lead("s", assistant("m1", 0, 1))
        self.subagent("s", "b1234567890", {"agentType": "reviewer", "description": "Review"}, assistant("r", 9, 1))
        self.subagent("s", "a1234567890", {"agentType": "developer", "description": "Build"}, assistant("d", 5, 1))
        [session] = agent_usage.read_sessions(self.directory)
        self.assertEqual([(agent.name, agent.role, agent.task) for agent in session.agents],
                         [("lead", "lead", "This session's main conversation"), ("a1234567", "developer", "Build"),
                          ("b1234567", "reviewer", "Review")])

    def test_a_meta_file_without_a_role_is_an_error(self):
        self.lead("s", assistant("m1", 0, 1))
        self.subagent("s", "a1", {"description": "Build"}, assistant("d", 5, 1))
        with self.assertRaisesRegex(ValueError, "agent-a1.meta.json lacks agentType"):
            agent_usage.read_sessions(self.directory)

    def test_roles_are_summed_in_the_order_they_first_appear(self):
        def agent(role, processed):
            return agent_usage.Agent("x", role, "t", agent_usage.read_usage(
                self.lead(f"{role}{processed}", assistant("m", 0, processed))))

        roles = agent_usage.by_role([agent("lead", 1), agent("reviewer", 2), agent("developer", 3),
                                     agent("reviewer", 4)])
        self.assertEqual(list(roles.items()), [("lead", (1, 1)), ("reviewer", (2, 6)), ("developer", (1, 3))])

    def test_a_session_renders_as_a_role_table_and_one_row_per_agent(self):
        def at(hour, minute):
            return datetime(2026, 10, 5, hour, minute, tzinfo=timezone.utc)

        lead = agent_usage.Agent("lead", "lead", "This session's main conversation", agent_usage.Usage(
            calls=1200, processed=999, cache_reads=1000, peak_context=1400, first=at(10, 0), last=at(10, 30),
            active=timedelta(minutes=12)))
        reviewer = agent_usage.Agent("a1234567", "reviewer", "Review a|b", agent_usage.Usage(
            calls=3, processed=2600, cache_reads=0, peak_context=999, first=at(10, 5), last=at(11, 40),
            active=timedelta(seconds=100)))
        self.assertEqual(agent_usage.render_session(agent_usage.Session("abcdef12-0000", [lead, reviewer])),
                         "## Session abcdef12 (2026-10-05 10:00 to 2026-10-05 11:40 UTC)\n"
                         "\n"
                         "| Role | Agents | Input processed |\n"
                         "|---|---|---|\n"
                         "| lead | 1 | 999 |\n"
                         "| reviewer | 1 | 3k |\n"
                         "\n"
                         "| Agent | Role | Task | Calls | Input processed | Of which cache reads | Peak context "
                         "| Active minutes |\n"
                         "|---|---|---|---|---|---|---|---|\n"
                         "| lead | lead | This session's main conversation | 1,200 | 999 | 1k | 1k | 12 |\n"
                         "| a1234567 | reviewer | Review a/b | 3 | 3k | 0 | 999 | 2 |\n")

    def test_a_directory_without_transcripts_exits_with_an_error_and_leaves_the_doc_as_it_was(self):
        doc = self.directory / "doc.md"
        doc.write_text("previous content", encoding="utf-8")
        missing = self.directory / "missing"
        with self.assertRaises(SystemExit) as raised:
            agent_usage.main(missing, doc)
        self.assertEqual(raised.exception.code,
                         f"No session transcripts with model calls in {missing}; {doc} is left as it was.")
        self.assertEqual(doc.read_text(encoding="utf-8"), "previous content")


if __name__ == "__main__":
    unittest.main()

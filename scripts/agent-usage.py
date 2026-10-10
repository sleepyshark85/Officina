#!/usr/bin/env python3
"""Writes docs/agent-usage.md: the agents each Claude Code session of this project ran, their role and task, and the
tokens they used, read from the session transcripts Claude Code keeps on this machine.

Usage: python3 scripts/agent-usage.py [PROJECT_TRANSCRIPTS_DIR] > docs/agent-usage.md
The directory defaults to ~/.claude/projects/<this checkout's path, with "/" as "-">.

A streamed reply is written to a transcript as several entries that repeat its usage, so each message is counted once,
by its id. Output tokens are not reported: a transcript holds each message's usage as the stream began, before
the output was counted. "Context" is the input of the agent's largest request (input, cache reads and cache
writes): what it carried at its peak."""
import json
import os
import sys
from datetime import datetime
from pathlib import Path

ROLE_ORDER = ["lead", "developer", "fixer", "reviewer", "Explore", "Plan", "general-purpose", "claude-code-guide"]


def default_dir():
    checkout = Path(__file__).resolve().parent.parent
    return Path.home() / ".claude" / "projects" / str(checkout).replace("/", "-")


def usage(transcript):
    """Model calls, token totals, peak context and the first and last timestamps of one transcript."""
    messages, first, last = {}, None, None
    with open(transcript, encoding="utf-8") as file:
        for line in file:
            entry = json.loads(line)
            stamp = entry.get("timestamp")
            if stamp:
                first = first or stamp
                last = stamp
            message = entry.get("message")
            if entry.get("type") == "assistant" and isinstance(message, dict) and message.get("usage"):
                messages[message.get("id") or len(messages)] = message["usage"]
    totals = {"calls": len(messages), "input": 0, "cache_read": 0, "cache_write": 0, "context": 0}
    for counts in messages.values():
        sent = (counts.get("input_tokens", 0), counts.get("cache_read_input_tokens", 0),
                counts.get("cache_creation_input_tokens", 0))
        totals["input"] += sent[0]
        totals["cache_read"] += sent[1]
        totals["cache_write"] += sent[2]
        totals["context"] = max(totals["context"], sum(sent))
    return totals, first, last


def minutes(first, last):
    if not first or not last:
        return 0
    parse = lambda stamp: datetime.fromisoformat(stamp.replace("Z", "+00:00"))
    return round((parse(last) - parse(first)).total_seconds() / 60)


def tokens(count):
    return f"{count / 1000:,.0f}k" if count >= 1000 else str(count)


def row(name, role, task, totals, span):
    processed = totals["input"] + totals["cache_read"] + totals["cache_write"]
    return (f"| {name} | {role} | {task} | {totals['calls']:,} | {tokens(processed)} | {tokens(totals['cache_read'])} "
            f"| {tokens(totals['context'])} | {span} |")


def session(directory, session_id):
    """The markdown section for one session, or None when it ran no agents."""
    agents_dir = directory / session_id / "subagents"
    if not agents_dir.is_dir():
        return None
    agents = []
    for meta_path in sorted(agents_dir.glob("agent-*.meta.json")):
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        transcript = meta_path.with_name(meta_path.name.replace(".meta.json", ".jsonl"))
        if transcript.exists():
            totals, first, last = usage(transcript)
            agents.append((meta.get("agentType", "?"), meta.get("description", ""), transcript.stem[6:], totals,
                           first, last))
    lead_path = directory / f"{session_id}.jsonl"
    lead = usage(lead_path) if lead_path.exists() else None
    starts = [agent[4] for agent in agents if agent[4]] + ([lead[1]] if lead and lead[1] else [])
    ends = [agent[5] for agent in agents if agent[5]] + ([lead[2]] if lead and lead[2] else [])
    lines = [f"## Session {session_id[:8]} ({min(starts)[:16].replace('T', ' ')} to {max(ends)[:16].replace('T', ' ')} "
             "UTC)", ""]

    by_role = {}
    for role, _, _, totals, _, _ in agents:
        summary = by_role.setdefault(role, {"agents": 0, "processed": 0})
        summary["agents"] += 1
        summary["processed"] += totals["input"] + totals["cache_read"] + totals["cache_write"]
    lines += ["| Role | Agents | Input processed |", "|---|---|---|"]
    if lead:
        totals = lead[0]
        lines.append(f"| lead | 1 | {tokens(totals['input'] + totals['cache_read'] + totals['cache_write'])} |")
    for role in sorted(by_role, key=lambda name: (ROLE_ORDER.index(name) if name in ROLE_ORDER else 99, name)):
        summary = by_role[role]
        lines.append(f"| {role} | {summary['agents']} | {tokens(summary['processed'])} |")
    lines += ["", "| Agent | Role | Task | Calls | Input processed | Of which cache reads | Peak context | Minutes |",
              "|---|---|---|---|---|---|---|---|"]
    if lead:
        lines.append(row("lead", "lead", "This session's main conversation", lead[0], minutes(lead[1], lead[2])))
    for role, task, agent_id, totals, first, last in sorted(agents, key=lambda agent: agent[4] or ""):
        lines.append(row(agent_id[:8], role, task.replace("|", "/"), totals, minutes(first, last)))
    return "\n".join(lines) + "\n"


def main():
    directory = Path(sys.argv[1]) if len(sys.argv) > 1 else default_dir()
    sessions = sorted(path.stem for path in directory.glob("*.jsonl"))
    sections = [section for section in (session(directory, session_id) for session_id in sessions) if section]
    print("# Agent usage\n")
    print("The agents each Claude Code session on this project ran, by role and task, and the tokens each used. "
          "Generated\nby [`scripts/agent-usage.py`](../scripts/agent-usage.py) from the session transcripts on the "
          "lead's machine;\nregenerate it at the end of a session rather than editing it.\n")
    print("- **Input processed:** every token sent to the model over the agent's calls (input, cache reads and cache "
          "writes).\n  Cache reads are billed at a fraction of input, so most of a long agent's input is cheap.\n"
          "- **Output** is not shown: transcripts record a message's usage as its stream begins, before the output is "
          "counted.\n- **Peak context:** the input of the agent's largest single request, what it carried at its "
          "fullest. The context-size\n  hook (`.claude/hooks/context-size.py`) warns an agent at 150k and 300k.\n"
          "- **Minutes:** from the agent's first to its last transcript entry, including time spent waiting for CI.\n")
    print("\n".join(sections), end="")


main()

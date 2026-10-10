#!/usr/bin/env python3
"""Writes docs/agent-usage.md: the agents each Claude Code session of this project ran, their role and task, and the
tokens they used, read from the session transcripts Claude Code keeps on this machine.

Usage: python3 -B scripts/agent-usage.py [PROJECT_TRANSCRIPTS_DIR]
The directory defaults to ~/.claude/projects/<the main checkout's path, with "/" as "-">, found through git's common
dir so that a worktree reads the same transcripts. The doc is written only once it is complete, so a run that fails
leaves it as it was.

A streamed reply is written to a transcript as several entries that repeat its usage, so each message is counted once,
by its id. Output tokens are not reported: a transcript holds each message's usage as the stream began, before
the output was counted."""
import json
import subprocess
import sys
from datetime import datetime, timedelta
from pathlib import Path
from typing import NamedTuple

DOC = Path(__file__).resolve().parent.parent / "docs" / "agent-usage.md"
# A longer gap between two entries is idle time (a night, the owner away), not work.
IDLE_GAP = timedelta(minutes=30)


class Usage(NamedTuple):
    """What one transcript sent to the model, and when it was active."""
    calls: int
    processed: int
    cache_reads: int
    peak_context: int
    first: datetime
    last: datetime
    active: timedelta


class Agent(NamedTuple):
    """One agent of a session: the lead, or a subagent it dispatched."""
    name: str
    role: str
    task: str
    usage: Usage


class Session(NamedTuple):
    """One Claude Code session: its lead first (when it made a model call), then its subagents in the order they
    started."""
    id: str
    agents: list[Agent]

    @property
    def start(self):
        return min(agent.usage.first for agent in self.agents)

    @property
    def end(self):
        return max(agent.usage.last for agent in self.agents)


def default_transcripts_dir():
    common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                            cwd=Path(__file__).resolve().parent, capture_output=True, text=True, check=True)
    checkout = Path(common.stdout.strip()).parent
    return Path.home() / ".claude" / "projects" / str(checkout).replace("/", "-")


def read_usage(transcript):
    """The usage of one transcript, or None when it made no model call."""
    by_message, stamps = {}, []
    with open(transcript, encoding="utf-8") as file:
        for line in file:
            entry = json.loads(line)
            if "timestamp" in entry:
                stamps.append(datetime.fromisoformat(entry["timestamp"].replace("Z", "+00:00")))
            message = entry.get("message")
            if entry.get("type") == "assistant" and isinstance(message, dict) and "usage" in message:
                by_message[message["id"]] = message["usage"]
    if not by_message:
        return None
    sizes = [counts["input_tokens"] + counts["cache_read_input_tokens"] + counts["cache_creation_input_tokens"]
             for counts in by_message.values()]
    gaps = (later - earlier for earlier, later in zip(stamps, stamps[1:]))
    return Usage(calls=len(by_message), processed=sum(sizes),
                 cache_reads=sum(counts["cache_read_input_tokens"] for counts in by_message.values()),
                 peak_context=max(sizes), first=stamps[0], last=stamps[-1],
                 active=sum((gap for gap in gaps if gap < IDLE_GAP), timedelta()))


def read_subagent(transcript):
    """The subagent of one transcript, with the role and task its meta file names, or None when it made no call."""
    meta_path = transcript.with_name(transcript.name.removesuffix(".jsonl") + ".meta.json")
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    if "agentType" not in meta or "description" not in meta:
        raise ValueError(f"{meta_path} lacks agentType or description")
    usage = read_usage(transcript)
    if usage is None:
        return None
    return Agent(transcript.stem.removeprefix("agent-")[:8], meta["agentType"], meta["description"], usage)


def read_session(lead_transcript):
    """The session whose main transcript this is, or None when neither its lead nor a subagent made a model call."""
    lead_usage = read_usage(lead_transcript)
    lead = [] if lead_usage is None else [Agent("lead", "lead", "This session's main conversation", lead_usage)]
    subagent_dir = lead_transcript.with_suffix("") / "subagents"
    subagents = [read_subagent(path) for path in sorted(subagent_dir.glob("agent-*.jsonl"))]
    started = sorted((agent for agent in subagents if agent), key=lambda agent: agent.usage.first)
    agents = lead + started
    return Session(lead_transcript.stem, agents) if agents else None


def read_sessions(directory):
    """Every session in the directory with a model call, oldest first."""
    sessions = (read_session(path) for path in directory.glob("*.jsonl"))
    return sorted((session for session in sessions if session), key=lambda session: session.start)


def by_role(agents):
    """Agent count and input processed per role, in the order the roles first appear."""
    roles = {}
    for agent in agents:
        count, processed = roles.get(agent.role, (0, 0))
        roles[agent.role] = (count + 1, processed + agent.usage.processed)
    return roles


def tokens(count):
    return f"{count / 1000:,.0f}k" if count >= 1000 else str(count)


def utc(moment):
    return moment.strftime("%Y-%m-%d %H:%M")


def render_session(session):
    lines = [f"## Session {session.id[:8]} ({utc(session.start)} to {utc(session.end)} UTC)", "",
             "| Role | Agents | Input processed |", "|---|---|---|"]
    lines += [f"| {role} | {count} | {tokens(processed)} |"
              for role, (count, processed) in by_role(session.agents).items()]
    lines += ["", "| Agent | Role | Task | Start (UTC) | End (UTC) | Calls | Input processed | Of which cache reads "
              "| Peak context | Active minutes |", "|---|---|---|---|---|---|---|---|---|---|"]
    for agent in session.agents:
        usage = agent.usage
        lines.append(f"| {agent.name} | {agent.role} | {agent.task.replace('|', '/')} "
                     f"| {utc(usage.first)} | {utc(usage.last)} | {usage.calls:,} "
                     f"| {tokens(usage.processed)} | {tokens(usage.cache_reads)} | {tokens(usage.peak_context)} "
                     f"| {round(usage.active / timedelta(minutes=1))} |")
    return "\n".join(lines) + "\n"


def render(sessions):
    idle = round(IDLE_GAP / timedelta(minutes=1))
    intro = f"""# Agent usage

The agents each Claude Code session on this project ran, by role and task, and the tokens each used, oldest session
first. Generated by [`scripts/agent-usage.py`](../scripts/agent-usage.py) from the session transcripts on the lead's
machine; regenerate it at the end of a session rather than editing it, from any checkout of the repository:

```sh
python3 -B scripts/agent-usage.py
```

- **Input processed:** every token sent to the model over the agent's calls (input, cache reads and cache writes).
  Cache reads are billed at a fraction of input, so most of a long agent's input is cheap.
- **Output** is not shown: transcripts record a message's usage as its stream begins, before the output is counted.
- **Peak context:** the input of the agent's largest single request, what it carried at its fullest. The context-size
  hook (`.claude/hooks/context-size.py`) warns an agent at 150k and 300k.
- **Start, End:** the times (UTC) of the agent's first and last model call.
- **Active minutes:** the time between the agent's consecutive transcript entries, leaving out every gap of {idle}
  minutes or more as idle (a night, the owner away). Waits for tools and CI shorter than that count as active.
"""
    return "\n".join([intro, *map(render_session, sessions)])


def main(directory, doc=DOC):
    """Writes the doc from the transcripts in the directory, or exits with a message and leaves it as it was when
    there are none."""
    sessions = read_sessions(directory)
    if not sessions:
        sys.exit(f"No session transcripts with model calls in {directory}; {doc} is left as it was.")
    doc.write_text(render(sessions), encoding="utf-8")


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else default_transcripts_dir())

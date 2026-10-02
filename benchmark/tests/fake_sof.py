"""A stand-in for `sof` at the console, so the benchmark runner is tested without the live model.

It speaks sof's console lines (`run <id>`, numbered requests, `model call:` lines with the cost so far) and reads the owner's
answers. A run makes 10 model calls, asking for the plan's sign-off first and for a command at the third; a resumed run shows the
calls it had made again, as sof replays a run's stored events, and goes on from them. At the end it commits the folder that
FAKE_SOF_SOLUTION names, if any, as the team's work. FAKE_SOF_HANG makes it hang after the sign-off; FAKE_SOF_NO_REPORT makes
`report` fail.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

CALLS = 10


def state_path(run_id):
    return os.path.join(tempfile.gettempdir(), f"fake-sof-{run_id}.json")


def load(run_id):
    with open(state_path(run_id)) as file:
        return json.load(file)


def save(state):
    """Saves the state whole or not at all, as a kill can come at any moment."""
    path = state_path(state["id"])
    with open(path + ".new", "w") as file:
        json.dump(state, file)
    os.replace(path + ".new", path)


def ask(number, text, expected):
    print(f"#{number} {text}", flush=True)
    answer = sys.stdin.readline().strip()
    if answer != expected:
        print(f"error: expected {expected!r}, got {answer!r}", flush=True)
        sys.exit(3)


def model_call(call):
    print(f"[developer[1]] model call: 1000 tokens, $0.10; cost so far ${call * 0.1:.2f}", flush=True)


def work(state):
    print(f"run {state['id']}", flush=True)
    for call in range(1, state["calls"] + 1):
        model_call(call)
    if not state["planApproved"]:
        ask(1, "lead needs your sign-off: Approve the lead's plan. Answer with approve or deny.", "approve 1")
        state["planApproved"] = True
        save(state)
    if os.environ.get("FAKE_SOF_HANG"):
        time.sleep(3600)
    while state["calls"] < CALLS:
        call = state["calls"] + 1
        if call == 3:
            ask(2, 'developer[1] asks to run run_command {"command": "ls"}: ls. Answer with approve, deny or change.', "deny 2")
        state["calls"] = call
        save(state)  # before the line, so a kill right after it does not repeat the call
        model_call(call)
    solution = os.environ.get("FAKE_SOF_SOLUTION")
    if solution:
        shutil.copytree(solution, os.getcwd(), dirs_exist_ok=True, ignore=shutil.ignore_patterns("bin", "obj"))
        git = ["git", "-c", "user.name=team", "-c", "user.email=team@localhost", "-c", "commit.gpgsign=false"]
        subprocess.run([*git, "add", "-A", "--", ".", ":!.sof"], check=True)
        subprocess.run([*git, "commit", "-q", "-m", "The team's work"], check=True)
    print("team: Completed, cost $1.00", flush=True)
    return 0


def main(args):
    if args[:2] == ["run", "--input"]:
        state = {"id": str(uuid.uuid4()), "calls": 0, "planApproved": False, "resumes": 0}
        save(state)
        return work(state)
    if args[:1] == ["resume"]:
        state = load(args[1])
        state["resumes"] += 1
        save(state)
        return work(state)
    if args[:1] == ["report"]:
        if os.environ.get("FAKE_SOF_NO_REPORT"):
            return 1
        state = load(args[1])
        print(f"Run {state['id']}: Completed, Completed")
        print(f"Agent team, started 2030-01-01 00:00:00Z, running 0:00:02, resumed {state['resumes']} times")
        print("Work: the goal")
        print(f"Cost: ${state['calls'] * 0.1:.2f} in {state['calls']} model calls, {state['calls'] * 1000} tokens")
        os.remove(state_path(state["id"]))
        return 0
    print(f"fake sof: unknown command {args}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

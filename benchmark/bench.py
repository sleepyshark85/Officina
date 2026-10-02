"""Runs the coding team benchmark (TEST-31): each goal, several times, each run from an empty repository with one forced restart.

    python3 benchmark/bench.py [--goals s1-word-count,m1-todo-api] [--runs 3] [--max-cost 60] [--seed 1] [--results <folder>]
                               [--sof "<command>"]

It runs the live model, so it costs money: ANTHROPIC_API_KEY must be set (`sof` reads it). Without --sof it builds `sof` from this
repository first. A run:

1. A new git repository outside this one, holding only `benchmark/sof.json`, so the team never sees the hidden tests or the
   reference solutions.
2. `sof run --input "<goal paragraph>"`, at the console. Every sign-off (the lead's plan, the run budget, an irreversible
   action) is approved and counted as a human input. Any other request (a command no rule allows, a question) is declined at
   once, as nobody answering would be by `run.approvalTimeout`; those are counted as declined, not as human input.
3. After a random number of model calls the process is killed, as a crash would, and the run goes on with `sof resume <run>`.
4. When `sof` ends, `sof report <run>` gives the cost, and the baseline (the repository's branch, cloned) is scored: its own build
   and tests, then the goal's hidden tests (scoring.py).

A run succeeds when it was restarted once, its baseline passes its own checks and every hidden test, and its only human inputs
were sign-offs. A run whose cost passes --max-cost is stopped and fails. Each run's result is written as JSON, with the console
log beside it, to the results folder; report.py summarises one or more results folders.
"""

import argparse
import datetime
import json
import os
import platform
import random
import re
import shlex
import shutil
import subprocess
import sys
import stat
import tempfile
import time

from scoring import BENCHMARK, goal_text, goals, score, tier

REPOSITORY = os.path.dirname(BENCHMARK)
GIT = ["git", "-c", "user.name=benchmark", "-c", "user.email=benchmark@localhost", "-c", "commit.gpgsign=false"]

# The model call after which a run is killed is drawn from 2 to this, by tier, so that it falls inside the run.
RESTART_BEFORE = {"small": 8, "medium": 16, "larger": 24}

REQUEST = re.compile(r"^#(\d+) (\S+) (needs your sign-off|asks to run|asks:)")
COST = re.compile(r"cost so far \$(\d+(?:\.\d+)?)")
MODEL_CALL = re.compile(r"^\[\S+\] model call: ")


def build_sof():
    """Builds sof from this repository; returns the command that runs it."""
    project = os.path.join(REPOSITORY, "src", "Sleepyshark.Officina.Cli")
    subprocess.run(["dotnet", "build", project, "-c", "Release", "-v", "q", "-nologo"], check=True)
    return ["dotnet", os.path.join(project, "bin", "Release", "net10.0", "Sleepyshark.Officina.Cli.dll")]


def new_workspace(goal):
    """An empty git repository, outside this one, holding only the benchmark's sof.json."""
    workspace = tempfile.mkdtemp(prefix=f"bench-{goal}-")
    subprocess.run([*GIT, "init", "-q", "-b", "main", workspace], check=True)
    shutil.copy(os.path.join(BENCHMARK, "sof.json"), os.path.join(workspace, "sof.json"))
    subprocess.run([*GIT, "-C", workspace, "add", "sof.json"], check=True)
    subprocess.run([*GIT, "-C", workspace, "commit", "-q", "-m", "The benchmark's configuration"], check=True)
    return workspace


class Console:
    """One `sof` process at the console: answers what waits for the owner and counts model calls and cost from its lines."""

    def __init__(self, sof, args, workspace, log, state, kill_at, max_cost):
        self.state, self.kill_at, self.max_cost = state, kill_at, max_cost
        self.log = log
        self.killed = None
        self.process = subprocess.Popen(
            [*sof, *args], cwd=workspace, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1)

    def run(self):
        """Reads the console until the process ends; returns its exit code."""
        with self.process:
            for line in self.process.stdout:
                self.log.write(line)
                self.log.flush()
                self.take(line.rstrip("\n"))
            return self.process.wait()

    def take(self, line):
        if self.killed:
            return  # what a killed process had already written is logged, not acted on
        state = self.state
        if state["runId"] is None and line.startswith("run "):
            state["runId"] = line[4:].strip()
        request = REQUEST.match(line)
        if request:
            number, kind = request.group(1), request.group(3)
            if kind == "needs your sign-off":
                state["signOffs"] += 1
                self.answer(f"approve {number}")
            else:
                state["declined"] += 1
                self.answer(f"deny {number}")
        if MODEL_CALL.match(line):
            state["modelCalls"] += 1
            cost = COST.search(line)
            if cost:
                state["cost"] = max(state["cost"], float(cost.group(1)))
            if self.kill_at is not None and state["modelCalls"] >= self.kill_at and not state["restarted"]:
                self.stop("restart")
            elif state["cost"] > self.max_cost:
                self.stop("cost")

    def answer(self, command):
        try:
            self.process.stdin.write(command + "\n")
            self.process.stdin.flush()
        except OSError:
            pass  # the process has ended

    def stop(self, why):
        """Kills the process at once, as a crash would."""
        if self.killed is None:
            self.killed = why
            self.process.kill()


def remove(folder):
    """Removes a folder this script made, read-only files too (git's objects are read-only on Windows)."""
    def writable(function, path, *_):
        os.chmod(path, stat.S_IWRITE)
        function(path)
    shutil.rmtree(folder, onerror=writable)


def report(sof, workspace, run_id):
    """What `sof report` says of the run: its status, outcome, running time, resumes and cost."""
    if run_id is None:
        return {}
    result = subprocess.run([*sof, "report", run_id], cwd=workspace, capture_output=True, text=True, errors="replace")
    text = result.stdout
    found = {"report": text[-4000:]}
    status = re.search(r"^Run \S+: (\w+), (\w+)", text, re.MULTILINE)
    if status:
        found["status"], found["outcome"] = status.group(1), status.group(2)
    running = re.search(r"running (\d+):(\d\d):(\d\d)(?:, resumed (\d+) times)?", text)
    if running:
        hours, minutes, seconds, resumes = running.groups()
        found["runningSeconds"] = int(hours) * 3600 + int(minutes) * 60 + int(seconds)
        found["resumes"] = int(resumes or 0)
    cost = re.search(r"^Cost: \$(\d+(?:\.\d+)?) in (\d+) model calls, (\d+) tokens", text, re.MULTILINE)
    if cost:
        found["cost"], found["modelCalls"], found["tokens"] = float(cost.group(1)), int(cost.group(2)), int(cost.group(3))
    return found


def run_once(sof, goal, number, results, rng, max_cost):
    """One run of a goal: from an empty repository, with a forced restart; writes and returns its result."""
    workspace = new_workspace(goal)
    kill_at = rng.randint(2, RESTART_BEFORE[tier(goal)])
    state = {"runId": None, "signOffs": 0, "declined": 0, "modelCalls": 0, "cost": 0.0, "restarted": False}
    started = time.monotonic()
    exits = []
    stopped_for_cost = False
    with open(os.path.join(results, f"{goal}-{number}.log"), "w", encoding="utf-8") as log:
        console = Console(sof, ["run", "--input", goal_text(goal)], workspace, log, state, kill_at, max_cost)
        exits.append(console.run())
        if console.killed == "restart" and state["runId"] is not None:
            state["restarted"] = True
            log.write(f"\n--- killed after model call {state['modelCalls']}; resuming ---\n")
            console = Console(sof, ["resume", state["runId"]], workspace, log, state, None, max_cost)
            exits.append(console.run())
        stopped_for_cost = console.killed == "cost"
    seconds = time.monotonic() - started

    found = report(sof, workspace, state["runId"])
    with tempfile.TemporaryDirectory(prefix=f"bench-baseline-{goal}-") as folder:
        baseline = os.path.join(folder, "baseline")
        subprocess.run([*GIT, "clone", "-q", workspace, baseline], check=True)
        scored = score(goal, baseline)

    success = state["restarted"] and not stopped_for_cost and scored["passed"]
    if success:
        remove(workspace)  # a failed run's workspace is kept, to look into
    result = {
        "goal": goal, "tier": tier(goal), "run": number, "os": platform.system(), "runId": state["runId"], "workspace": workspace,
        "restartAfterModelCall": kill_at, "restarted": state["restarted"], "exitCodes": exits, "stoppedForCost": stopped_for_cost,
        "humanInputs": state["signOffs"], "declined": state["declined"],
        "cost": found.get("cost", state["cost"]), "modelCalls": found.get("modelCalls", state["modelCalls"]), "tokens": found.get("tokens"),
        "wallSeconds": round(seconds), "runningSeconds": found.get("runningSeconds"), "status": found.get("status"), "outcome": found.get("outcome"),
        "baseline": scored,
        "success": success,
        "report": found.get("report", ""),
    }
    with open(os.path.join(results, f"{goal}-{number}.json"), "w", encoding="utf-8") as file:
        json.dump(result, file, indent=2)
    return result


def main(argv):
    parser = argparse.ArgumentParser(description="Runs the coding team benchmark against the live model.")
    parser.add_argument("--goals", default=",".join(goals()), help="the goals to run, comma-separated (default: all)")
    parser.add_argument("--runs", type=int, default=3, help="runs of each goal (default: 3)")
    parser.add_argument("--max-cost", type=float, default=60.0, help="stops a run whose cost passes this many dollars (default: 60)")
    parser.add_argument("--seed", type=int, default=None, help="seeds the restart points, to repeat a set of runs")
    parser.add_argument("--results", default=None, help="the folder for the results (default: benchmark/results/<time>-<os>)")
    parser.add_argument("--sof", default=None, help="the command that runs sof (default: build it from this repository)")
    options = parser.parse_args(argv)

    selected = [goal for goal in options.goals.split(",") if goal]
    unknown = [goal for goal in selected if goal not in goals()]
    if unknown:
        parser.error(f"unknown goals: {', '.join(unknown)}")
    sof = shlex.split(options.sof) if options.sof else build_sof()
    results = options.results or os.path.join(
        BENCHMARK, "results", f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{platform.system().lower()}")
    os.makedirs(results, exist_ok=True)
    rng = random.Random(options.seed)

    outcomes = []
    for goal in selected:
        for number in range(1, options.runs + 1):
            result = run_once(sof, goal, number, results, rng, options.max_cost)
            outcomes.append(result["success"])
            print(f"{goal} run {number}: {'success' if result['success'] else 'failure'}, ${result['cost']:.2f}, "
                  f"{result['wallSeconds']}s, {result['humanInputs']} sign-offs, {result['declined']} declined", flush=True)
    print(f"{sum(outcomes)} of {len(outcomes)} runs succeeded. Results: {results}")
    return 0 if outcomes and all(outcomes) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

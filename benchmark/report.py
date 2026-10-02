"""Summarises benchmark results (TEST-31): success rate, cost, time and human inputs, by goal, tier and system.

    python3 benchmark/report.py <results folder>... [--target 0.9]

Reads every run's JSON that bench.py wrote and prints a Markdown report. Exits with 1 when the success rate over all the runs
is below the target (90%, the M7 milestone), or a goal has fewer than 3 runs on a system.
"""

import argparse
import glob
import json
import os
import statistics
import sys

MINIMUM_RUNS = 3


def load(folders):
    runs = []
    for folder in folders:
        for path in sorted(glob.glob(os.path.join(folder, "*.json"))):
            with open(path, encoding="utf-8") as file:
                run = json.load(file)
            if "goal" in run and "success" in run:
                runs.append(run)
    return runs


def rate(runs):
    return sum(run["success"] for run in runs) / len(runs) if runs else 0.0


def line(name, runs):
    costs = [run["cost"] for run in runs]
    minutes = [run["wallSeconds"] / 60 for run in runs]
    return (f"| {name} | {len(runs)} | {sum(run['success'] for run in runs)} | {rate(runs):.0%} | ${statistics.mean(costs):.2f} | "
            f"${sum(costs):.2f} | {statistics.mean(minutes):.0f} | {statistics.mean(run['humanInputs'] for run in runs):.1f} | "
            f"{sum(run['declined'] for run in runs)} |")


def summarise(runs, target):
    """The report's Markdown and whether the benchmark met its target."""
    systems = sorted({run["os"] for run in runs})
    out = ["# Coding team benchmark (TEST-31)", ""]
    header = ["| | Runs | Succeeded | Rate | Mean cost | Total cost | Mean minutes | Mean sign-offs | Declined |", "|---|---|---|---|---|---|---|---|---|"]
    problems = []
    for system in systems:
        mine = [run for run in runs if run["os"] == system]
        out += [f"## {system}", "", *header]
        for goal in sorted({run["goal"] for run in mine}):
            of_goal = [run for run in mine if run["goal"] == goal]
            out.append(line(goal, of_goal))
            if len(of_goal) < MINIMUM_RUNS:
                problems.append(f"{goal} has {len(of_goal)} runs on {system}; it needs {MINIMUM_RUNS}.")
            if any(not run["restarted"] for run in of_goal):
                problems.append(f"{goal} has runs on {system} that were never restarted.")
        for tier in ("small", "medium", "larger"):
            of_tier = [run for run in mine if run["tier"] == tier]
            if of_tier:
                out.append(line(f"**{tier}**", of_tier))
        out += [line(f"**all on {system}**", mine), ""]
    overall = rate(runs)
    met = overall >= target and not problems
    out += ["## Result", "", f"{sum(run['success'] for run in runs)} of {len(runs)} runs succeeded: {overall:.0%}, against a target of {target:.0%}."]
    out += [f"- {problem}" for problem in problems]
    out += ["", "The target is met." if met else "The target is not met.", ""]
    failed = [run for run in runs if not run["success"]]
    if failed:
        out += ["## Failed runs", "", "| Run | Why |", "|---|---|"]
        for run in failed:
            out.append(f"| {run['goal']} #{run['run']} ({run['os']}) | {why(run)} |")
    return "\n".join(out) + "\n", met


def why(run):
    if run.get("stoppedForCost"):
        return f"stopped at ${run['cost']:.2f}, past the cost cap"
    if not run["restarted"]:
        return "never restarted (it ended before its restart point)"
    baseline = run["baseline"]
    failed = [name for name, check in baseline["checks"].items() if not check["passed"]]
    if failed:
        return f"the baseline's {' and '.join(failed)} failed"
    hidden = baseline["hidden"]
    return f"{hidden['failed']} of {hidden['ran']} hidden tests failed"


def main(argv):
    parser = argparse.ArgumentParser(description="Summarises benchmark results.")
    parser.add_argument("folders", nargs="+")
    parser.add_argument("--target", type=float, default=0.9)
    options = parser.parse_args(argv)
    runs = load(options.folders)
    if not runs:
        print("no results found", file=sys.stderr)
        return 1
    text, met = summarise(runs, options.target)
    print(text, end="")
    return 0 if met else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

"""Scores a goal's workspace (TEST-31): the baseline's own checks, then the goal's hidden acceptance tests.

The own checks are the project's commands in `sof.json` (`project.values.buildCommand` and `testCommand`), the same the team's
baseline checks run. The hidden tests run with BENCH_WORKSPACE set to the workspace, from outside it: they are never copied in.
"""

import json
import os
import re
import subprocess
import sys

BENCHMARK = os.path.dirname(os.path.abspath(__file__))
GOALS = os.path.join(BENCHMARK, "goals")
TAIL = 4000


def goals():
    """Every goal, by id, in order."""
    return sorted(name for name in os.listdir(GOALS) if os.path.isdir(os.path.join(GOALS, name)))


def goal_text(goal):
    """The goal's paragraph, the input a run is given."""
    with open(os.path.join(GOALS, goal, "goal.md"), encoding="utf-8") as file:
        return " ".join(file.read().split())


def tier(goal):
    return {"s": "small", "m": "medium", "l": "larger"}[goal[0]]


def configuration():
    """The benchmark's `sof.json`, read as JSON once its comment lines are dropped."""
    with open(os.path.join(BENCHMARK, "sof.json"), encoding="utf-8") as file:
        return json.loads("".join(line for line in file if not line.lstrip().startswith("//")))


def own_commands():
    values = configuration()["project"]["values"]
    return [("build", values["buildCommand"]), ("tests", values["testCommand"])]


def tail(text):
    return text[-TAIL:]


def own_checks(workspace, timeout=1800):
    """Runs the project's build and test commands in the workspace; a check after a failed one does not run."""
    results = {}
    for name, command in own_commands():
        try:
            result = subprocess.run(command, shell=True, cwd=workspace, capture_output=True, text=True, errors="replace", timeout=timeout)
            results[name] = {"passed": result.returncode == 0, "output": tail(result.stdout + result.stderr)}
        except subprocess.TimeoutExpired:
            results[name] = {"passed": False, "output": f"timed out after {timeout} seconds"}
        if not results[name]["passed"]:
            break
    return results


def hidden_tests(goal, workspace, timeout=3600):
    """Runs the goal's hidden tests against the workspace; returns whether all passed, with the counts unittest printed."""
    environment = dict(os.environ, BENCH_WORKSPACE=os.path.abspath(workspace), PYTHONDONTWRITEBYTECODE="1")
    command = [sys.executable, "-m", "unittest", "discover", "-s", os.path.join(GOALS, goal, "hidden"), "-v"]
    try:
        result = subprocess.run(command, env=environment, capture_output=True, text=True, errors="replace", timeout=timeout)
    except subprocess.TimeoutExpired:
        return {"passed": False, "ran": 0, "failed": 0, "output": f"timed out after {timeout} seconds"}
    output = result.stdout + result.stderr
    return {"passed": result.returncode == 0, **counts(output), "output": tail(output)}


def counts(output):
    """The tests unittest ran and the ones that failed or erred, from its summary."""
    ran = re.search(r"^Ran (\d+) tests?", output, re.MULTILINE)
    failed = sum(int(number) for number in re.findall(r"(?:failures|errors)=(\d+)", output))
    return {"ran": int(ran.group(1)) if ran else 0, "failed": failed}


def score(goal, workspace):
    """The baseline's own checks and the hidden tests; it passes only when all of them do."""
    checks = own_checks(workspace)
    hidden = hidden_tests(goal, workspace)
    passed = len(checks) == len(own_commands()) and all(check["passed"] for check in checks.values()) and hidden["passed"]
    return {"passed": passed, "checks": checks, "hidden": hidden}

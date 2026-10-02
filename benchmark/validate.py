"""Validates the hidden test suites against the reference solutions: each suite can be passed by a solution of its goal.

    python3 benchmark/validate.py [goal ...]

Each goal's reference solution, in `reference/<goal>`, is copied to an empty folder outside the repository, as a team's workspace
would be, and scored as a run's baseline is: its own build and tests, then every hidden test. As a cheap sanity check, each suite
must also fail against an empty workspace; that shows only that it needs a project, not that each test tells a wrong solution
from a right one. The references are written from
the goal paragraphs alone and are never given to the team: a run's workspace is a new repository holding only `sof.json`.
Exits with 1 when any reference fails.
"""

import os
import shutil
import sys
import tempfile
import time

from scoring import BENCHMARK, goals, hidden_tests, score

REFERENCE = os.path.join(BENCHMARK, "reference")


def validate(goal):
    source = os.path.join(REFERENCE, goal)
    if not os.path.isdir(source):
        return {"passed": False, "checks": {}, "hidden": {"passed": False, "ran": 0, "failed": 0, "output": "no reference solution"}, "failsWhenEmpty": True}
    with tempfile.TemporaryDirectory(prefix=f"bench-ref-{goal}-") as workspace:
        target = os.path.join(workspace, goal)
        shutil.copytree(source, target, ignore=shutil.ignore_patterns("bin", "obj", "TestResults"))
        result = score(goal, target)
    with tempfile.TemporaryDirectory(prefix=f"bench-empty-{goal}-") as empty:
        result["failsWhenEmpty"] = not hidden_tests(goal, empty)["passed"]
    result["passed"] = result["passed"] and result["failsWhenEmpty"]
    return result


def main(selected):
    unknown = [goal for goal in selected if goal not in goals()]
    if unknown:
        print(f"unknown goals: {', '.join(unknown)}", file=sys.stderr)
        return 2
    failed = []
    for goal in selected or goals():
        started = time.monotonic()
        result = validate(goal)
        hidden = result["hidden"]
        checks = ", ".join(f"{name} {'passed' if check['passed'] else 'FAILED'}" for name, check in result["checks"].items())
        print(f"{goal}: {'passed' if result['passed'] else 'FAILED'} ({checks}; hidden tests {hidden['ran'] - hidden['failed']} of "
              f"{hidden['ran']} passed{'' if result['failsWhenEmpty'] else '; they PASS on an empty workspace'}) in {time.monotonic() - started:.0f}s", flush=True)
        if not result["passed"]:
            failed.append(goal)
            for name, check in result["checks"].items():
                if not check["passed"]:
                    print(check["output"])
            print(hidden["output"])
    print(f"{len(selected or goals()) - len(failed)} of {len(selected or goals())} reference solutions pass their own checks and their goal's hidden tests.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

# The coding team benchmark (TEST-31)

Ten development goals for the coding team, each with a hidden acceptance test suite written before the team ever works on it.
S21 runs the benchmark against the live model and measures it; this folder is the fixed set.

| Tier | Goal | What it is |
|---|---|---|
| Small | [s1-word-count](goals/s1-word-count/goal.md) | A `wc` console program |
| Small | [s2-roman-numerals](goals/s2-roman-numerals/goal.md) | A library and a console program for Roman numerals |
| Small | [s3-csv-to-json](goals/s3-csv-to-json/goal.md) | A CSV to JSON converter |
| Small | [s4-calculator](goals/s4-calculator/goal.md) | An expression calculator |
| Medium | [m1-todo-api](goals/m1-todo-api/goal.md) | A to-do REST API with SQLite |
| Medium | [m2-link-shortener](goals/m2-link-shortener/goal.md) | A link shortener with an API key |
| Medium | [m3-log-report](goals/m3-log-report/goal.md) | An access log report, streamed |
| Medium | [m4-library-catalog](goals/m4-library-catalog/goal.md) | A library catalog with loans |
| Larger | [l1-issue-tracker](goals/l1-issue-tracker/goal.md) | An issue tracker with users, projects and a workflow |
| Larger | [l2-task-runner](goals/l2-task-runner/goal.md) | A build task runner with dependencies and caching |

## A run

[`bench.py`](bench.py) runs the benchmark against the live model. Each run:

1. Starts from a new git repository outside this one, holding only [`sof.json`](sof.json), the coding team preset on a .NET
   project, so the team never sees the hidden tests or the reference solutions.
2. Runs `sof run --input "<the goal paragraph>"` in it. Each configured sign-off (the lead's plan, the run budget, an
   irreversible action) is approved and counted as a human input. Any other request (a command no rule allows, a question) is
   declined at once, as nobody answering would be by `run.approvalTimeout`, and counted as declined.
3. Kills `sof` after a random model call, as a crash would, and resumes the run with `sof resume <run>`.
4. When the run ends, reads its cost and time with `sof report <run>`, clones its baseline and scores it
   ([`scoring.py`](scoring.py)): the project's own build and tests, then the hidden tests.

A run succeeds when it was restarted once and its baseline passes its own checks and every hidden test; its only human inputs
are the sign-offs. Each goal runs at least 3 times, on Linux and on Windows. [`report.py`](report.py) sums the results by goal,
tier and system, and checks the 90% target (M7).

**Run it in a throwaway VM or user account.** The team's commands run in `sof`'s sandbox, but scoring builds and runs the
team's code outside any sandbox, as you: its build, its tests and the hidden suites, with access to your home folder. So
`bench.py` asks for `--i-understand-unsandboxed-scoring`.

```sh
export ANTHROPIC_API_KEY=...         # the runs call the live model and cost money
python3 benchmark/bench.py --i-understand-unsandboxed-scoring --goals s1-word-count --runs 1    # a pilot run
python3 benchmark/bench.py --i-understand-unsandboxed-scoring                                  # every goal, 3 runs each
python3 benchmark/report.py benchmark/results/<linux run> benchmark/results/<windows run>
```

The report meets the target only with every goal run at least 3 times on both Linux and Windows. A run whose cost passes
`--max-cost` (60 dollars), or that takes longer than `--timeout` (8 hours), is stopped and fails. A failed run's workspace is kept, its path in the run's
JSON; a successful run's is removed.

## The reference solutions

[`reference/`](reference) holds a solution for each goal, written from its paragraph alone. [`validate.py`](validate.py)
copies each to an empty folder and scores it as a run's baseline would be: each suite can be passed. It also checks that each
suite fails against an empty workspace, which shows only that a suite needs a project, not that it tells a wrong solution from a
right one. CI runs it on Linux and Windows when `benchmark/` changes, with the runner's own
tests ([`tests/`](tests), against a stand-in for `sof`). Validating found that text sent to a program's standard input became
CRLF on Windows, which the goals do not allow; the harness now sends it as bytes.

## The hidden tests

The tests use the program from outside, as its user would: they build it, then run a console program's assembly, or start a
web service's on a free port with its data in a fresh folder. They read no source, depend only on what the goal paragraph says, and use only
the Python standard library ([`harness.py`](harness.py)), on Linux and Windows. They are never put in the team's workspace.
A project that does not build fails every test of its goal.

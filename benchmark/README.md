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

1. Start from an empty git repository holding only [`sof.json`](sof.json), the coding team preset on a .NET project.
2. Run `sof run --input "<the goal paragraph>"` in it. Answer only the configured sign-offs: the lead's plan, the run budget
   and irreversible actions. Stop the run once, at a random point, and resume it (`sof resume <run>`).
3. When the run ends, run the hidden tests against its baseline: `BENCH_WORKSPACE=<the repository> python3 -m unittest discover
   -s benchmark/goals/<goal>/hidden`.

A run succeeds when the baseline passes its own checks (the build and the tests) and every hidden test, with human input only
at the sign-offs. Each goal runs at least 3 times; a run records its success, cost, time and the human inputs it needed.

## The hidden tests

The tests use the program from outside, as its user would: they build it, then run a console program's assembly, or start a
web service's on a free port with its data in a fresh folder. They read no source, depend only on what the goal paragraph says, and use only
the Python standard library ([`harness.py`](harness.py)), on Linux and Windows. They are never put in the team's workspace.
A project that does not build fails every test of its goal.

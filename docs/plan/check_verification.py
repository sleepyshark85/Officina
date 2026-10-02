"""Check that every MUST in REQUIREMENTS.md is verified by a test or a recorded review (v1 acceptance, REQUIREMENTS.md §13).

A MUST is verified by a test when its id appears in a test's source (tests/**/*.cs, benchmark/tests/*.py, the hidden suites'
harness aside), and by a review when verification.md's "Recorded reviews" table has a row for it. One in the "Pending" table
is reported as pending. Exits with 1 when any MUST is neither verified nor reviewed, or is pending.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parents[2]
musts = re.findall(r"^\| ([A-Z]+-\d+) \| MUST \|", (root / "REQUIREMENTS.md").read_text(), re.M)
tests = "".join(path.read_text(errors="ignore") for path in [*root.glob("tests/**/*.cs"), *root.glob("benchmark/tests/*.py")])
verification = (root / "docs/plan/verification.md").read_text()
reviews_text, _, pending_text = verification.partition("## Pending")
reviewed = set(re.findall(r"^\| ([A-Z]+-\d+) \|", reviews_text, re.M))
pending = set(re.findall(r"^\| ([A-Z]+-\d+) \|", pending_text, re.M))

tested = {rid for rid in musts if re.search(rf"\b{rid}\b", tests)}
problems = [f"{rid} is pending" for rid in musts if rid in pending]
problems += [f"{rid} has no test and no recorded review" for rid in musts if rid not in tested | reviewed | pending]
print("\n".join(problems) or f"OK: all {len(musts)} MUSTs are verified ({len(tested)} by tests, {len(reviewed - tested)} by review only)")
sys.exit(1 if problems else 0)

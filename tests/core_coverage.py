"""TEST-33: checks that the tests cover at least 85% of the core's lines.

    dotnet test --collect "Code Coverage;Format=cobertura" --results-directory <folder>
    python3 tests/core_coverage.py <folder> [--minimum 85]

Reads every Cobertura report under the folder (one for each test assembly, or merged), and counts a line of
`src/Sleepyshark.Officina.Core` as covered when any test assembly ran it. Generated code (under obj/) is left out.
Prints the coverage of the core and of each of its folders, and exits with 1 below the minimum.
"""

import argparse
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ElementTree

CORE = os.sep.join(["src", "Sleepyshark.Officina.Core", ""])


def lines(folder):
    """{(file, line): covered} for the core's source lines in every report."""
    found = {}
    for report in glob.glob(os.path.join(folder, "**", "*.cobertura.xml"), recursive=True):
        for cls in ElementTree.parse(report).iter("class"):
            name = os.path.normpath(cls.get("filename", ""))
            if CORE not in name or os.sep + "obj" + os.sep in name:
                continue
            relative = name[name.index(CORE):]
            for line in cls.iter("line"):
                key = (relative, int(line.get("number")))
                found[key] = found.get(key, False) or int(line.get("hits", "0")) > 0
    return found


def main(argv):
    parser = argparse.ArgumentParser(description="Checks the core's line coverage.")
    parser.add_argument("folder")
    parser.add_argument("--minimum", type=float, default=85.0)
    options = parser.parse_args(argv)
    found = lines(options.folder)
    if not found:
        print(f"no coverage of the core found under {options.folder}", file=sys.stderr)
        return 1
    by_folder = collections.defaultdict(lambda: [0, 0])
    for (name, _), covered in found.items():
        parts = name.split(os.sep)
        key = parts[2] if len(parts) > 3 else "(top)"
        by_folder[key][0] += covered
        by_folder[key][1] += 1
    for key, (covered, total) in sorted(by_folder.items()):
        print(f"  {key}: {100 * covered / total:.1f}% of {total} lines")
    covered = sum(found.values())
    percent = 100 * covered / len(found)
    print(f"The core: {percent:.1f}% of {len(found)} lines covered (minimum {options.minimum:.0f}%).")
    return 0 if percent >= options.minimum else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

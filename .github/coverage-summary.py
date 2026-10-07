"""Prints a Markdown table of line and branch coverage per assembly from the Cobertura files under a folder."""
import glob
import sys
import xml.etree.ElementTree as ET

reports = glob.glob(f"{sys.argv[1]}/**/*.cobertura.xml", recursive=True)
if not reports:
    sys.exit("No coverage report found.")
print("## Coverage\n\n| Assembly | Lines | Branches |\n|---|---|---|")
for report in reports:
    root = ET.parse(report).getroot()
    for package in sorted(root.iter("package"), key=lambda each: each.get("name")):
        print(f"| `{package.get('name')}` | {float(package.get('line-rate')):.1%} | {float(package.get('branch-rate')):.1%} |")
    print(f"| **Total** | **{float(root.get('line-rate')):.1%}** | **{float(root.get('branch-rate')):.1%}** |")

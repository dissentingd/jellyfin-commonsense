"""Prints the CHANGELOG.md section for one version (used as release notes and the manifest changelog)."""

import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
version = sys.argv[1]
text = open("CHANGELOG.md", encoding="utf-8").read()
match = re.search(rf"^## \[?{re.escape(version)}\]?[^\n]*\n(.*?)(?=^## |\Z)", text, re.S | re.M)
if not match:
    sys.exit(f"No CHANGELOG.md section for {version}")
print(match.group(1).strip())

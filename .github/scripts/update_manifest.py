"""Adds (or replaces) a version in the Jellyfin plugin repository manifest."""

import json
import sys
from datetime import datetime, timezone

path, version, url, checksum, notes_file = sys.argv[1:6]
manifest = json.load(open(path, encoding="utf-8"))
plugin = manifest[0]
entry = {
    "version": version,
    "changelog": open(notes_file, encoding="utf-8").read().strip(),
    "targetAbi": "12.1.0.0",
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, entry)
with open(path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")

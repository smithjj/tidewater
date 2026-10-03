#!/bin/sh
# Prints the last N (default 20) entries of the open Editor's console: level + message (first 600 chars).
# usage: unity/tools/console.sh [N] [minLevel: log|warning|error]
N=${1:-20}
LEVEL=${2:-log}
cd "$(dirname "$0")/.." || exit 1
/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe --no-banner --json command console --tail "$N" --level "$LEVEL" 2>/dev/null | python3 -c "
import sys, json
d = json.load(sys.stdin)
r = d.get('data', d)
r = r.get('result', r) if isinstance(r, dict) else r
if isinstance(r, str): r = json.loads(r)
for e in (r.get('entries') if isinstance(r, dict) else None) or []:
    print(e['level'].upper(), e['message'][:600].replace('\n', ' | '))
"

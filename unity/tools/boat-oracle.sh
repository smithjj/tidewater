#!/bin/sh
# Runs BoatOracle.Compare in the open Editor (fast enough for a direct eval) and prints the report.
#   node unity/tools/dump-boat-hull.mjs unity/Temp/oracle/boat && node unity/tools/dump-boat.mjs unity/Temp/oracle/boat
#   unity/tools/boat-oracle.sh        (add a recompile first with: unity recompile --focus)
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
"$U" --no-banner recompile --focus >/dev/null 2>&1
sleep 3
"$U" --no-banner command eval 'try { return Tidewater.EditorTools.BoatOracle.Compare(); } catch (System.Exception e) { return e.ToString(); }' 2>&1 | python3 -c "
import sys,re
t=sys.stdin.read()
i=t.find('\"result\":\"'); j=t.rfind('\"}')
print(t[i+10:j].replace('\\\\r','').replace('\\\\n','\n') if i>=0 else t)
"

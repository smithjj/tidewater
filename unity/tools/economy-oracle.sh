#!/bin/sh
# Runs EconomyOracle.Compare in the open Editor and prints the report.
#   node unity/tools/dump-economy.mjs unity/Temp/oracle/economy && unity/tools/economy-oracle.sh
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
"$U" --no-banner recompile --focus >/dev/null 2>&1
for i in $(seq 1 30); do sleep 5; "$U" --no-banner command recompile_status 2>&1 | grep -q "completed\|up_to_date" && break; done
sleep 3
"$U" --no-banner command eval "try { return Tidewater.EditorTools.EconomyOracle.Compare(); } catch ( System.Exception e ) { return e.ToString(); }" 2>&1 | python3 -c "
import sys
t=sys.stdin.read()
i=t.find('\"result\":\"'); j=t.rfind('\"}')
print(t[i+10:j].replace('\\\\n','\n').replace('\\\\\"','\"') if i>=0 else t)
"

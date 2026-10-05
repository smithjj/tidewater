#!/bin/sh
# Runs TrapsOracle.Compare in the open Editor, one section per eval (each is fast), and prints the reports.
#   node unity/tools/dump-traps.mjs unity/Temp/oracle/traps && unity/tools/traps-oracle.sh
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
"$U" --no-banner recompile --focus >/dev/null 2>&1
for i in $(seq 1 40); do sleep 5; "$U" --no-banner command recompile_status 2>&1 | grep -q "completed\|up_to_date" && break; done
sleep 3
for sec in ${SECTIONS:-mesh rules yields sessions buoys}; do
"$U" --no-banner command eval "try { return Tidewater.EditorTools.TrapsOracle.Compare( null, \"$sec\" ); } catch ( System.Exception e ) { return e.ToString(); }" 2>&1 | python3 -c "
import sys
t=sys.stdin.read()
i=t.find('\"result\":\"'); j=t.find('\"}',i)
print(t[i+10:j].replace('\\\\n','\n').replace('\\\\\"','\"') if i>=0 else t)
"
done

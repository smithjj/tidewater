#!/bin/sh
# Runs BoatControllerOracle.Compare in the open Editor, one scenario per eval (each is fast), and prints the reports.
#   node unity/tools/dump-boat-controller.mjs unity/Temp/oracle/boatctl && unity/tools/boat-controller-oracle.sh
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
"$U" --no-banner recompile --focus >/dev/null 2>&1
for i in $(seq 1 30); do sleep 5; "$U" --no-banner command recompile_status 2>&1 | grep -q "completed\|up_to_date" && break; done
sleep 3
for sc in ${SCENARIOS:-moored drive shoal mini-moored mini-drive mini-shoal pelagic-moored pelagic-drive pelagic-shoal}; do
"$U" --no-banner command eval "try { return Tidewater.EditorTools.BoatControllerOracle.Compare( null, \"$sc\" ); } catch ( System.Exception e ) { return e.ToString(); }" 2>&1 | python3 -c "
import sys
t=sys.stdin.read()
i=t.find('\"result\":\"'); j=t.find('\"}',i)
print(t[i+10:j].replace('\\\\n','\n') if i>=0 else t)
"
done

#!/bin/sh
# Runs Tidewater.EditorTools.<Oracle>.Compare( <Temp/oracle/dir> ) on a worker thread of the open Editor (the delayCall of run-oracle.sh is not
# ticked by an unfocused Editor). Only for oracles that touch no Unity API.
# usage: unity/tools/run-oracle-bg.sh SchoolsOracle schools
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
"$U" --no-banner recompile --focus >/dev/null 2>&1
rm -f Temp/oracle/report.txt
n=0
while [ $n -lt 20 ]; do
	out=$("$U" --no-banner command eval 'var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../Temp/oracle/'"$2"'")); var rep = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../Temp/oracle/report.txt")); System.Threading.Tasks.Task.Run(() => { string r; try { r = Tidewater.EditorTools.'"$1"'.Compare(dir); } catch (System.Exception e) { r = e.ToString(); } System.IO.File.WriteAllText(rep, r); }); return "started";' 2>&1)
	echo "$out" | grep -q '"result":"started"' && break
	n=$((n+1)); sleep 5
done
i=0; while [ ! -f Temp/oracle/report.txt ] && [ $i -lt 100 ]; do sleep 3; i=$((i+1)); done
cat Temp/oracle/report.txt

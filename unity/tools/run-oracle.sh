#!/bin/sh
# Runs TerrainOracle.Compare in the open Editor (delayCall: Editor commands time out after 5 s) and prints the report.
# usage: unity/tools/run-oracle.sh [TerrainOracle|OceanOracle]   (default TerrainOracle; needs the Editor open on unity/ and the
# matching dump in Temp/oracle: dump-terrain.mjs -> Temp/oracle/terrain, dump-ocean-fft.mjs -> Temp/oracle/fft)
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
ORACLE=${1:-TerrainOracle}
rm -f Temp/oracle/report.txt
"$U" --no-banner recompile --focus || exit 1
# schedule the comparison (retry: the Editor may still be reloading its domain after the compile)
n=0
while [ $n -lt 20 ]; do
	out=$("$U" --no-banner command eval 'UnityEditor.EditorApplication.delayCall += () => { string r; try { r = Tidewater.EditorTools.'"$ORACLE"'.Compare(); } catch (System.Exception e) { r = e.ToString(); } System.IO.File.WriteAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../Temp/oracle/report.txt")), r); }; return "scheduled";' 2>&1)
	echo "$out" | grep -q '"result":"scheduled"' && break
	n=$((n+1)); sleep 5
done
# an unfocused Editor does not tick EditorApplication.delayCall, and focus does not always survive the domain reload:
# keep bringing the Editor forward (a no-op recompile with --focus) until the report appears
i=0
while [ ! -f Temp/oracle/report.txt ] && [ $i -lt 40 ]; do
	"$U" --no-banner recompile --focus >/dev/null 2>&1
	j=0; while [ ! -f Temp/oracle/report.txt ] && [ $j -lt 5 ]; do sleep 1; j=$((j+1)); done
	i=$((i+1))
done
cat Temp/oracle/report.txt

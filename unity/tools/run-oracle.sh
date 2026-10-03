#!/bin/sh
# Runs TerrainOracle.Compare in the open Editor (delayCall: Editor commands time out after 5 s) and prints the report.
# usage: unity/tools/run-oracle.sh   (from anywhere; needs the Editor open on unity/ and Temp/oracle/terrain from dump-terrain.mjs)
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
rm -f Temp/oracle/report.txt
# an unfocused Editor does not tick EditorApplication.delayCall: --focus brings it forward (and compiles)
"$U" --no-banner recompile --focus || exit 1
"$U" --no-banner command eval 'UnityEditor.EditorApplication.delayCall += () => { string r; try { r = Tidewater.EditorTools.TerrainOracle.Compare(); } catch (System.Exception e) { r = e.ToString(); } System.IO.File.WriteAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "../Temp/oracle/report.txt")), r); }; return "scheduled";' >/dev/null 2>&1
i=0; while [ ! -f Temp/oracle/report.txt ] && [ $i -lt 120 ]; do sleep 3; i=$((i+1)); done
cat Temp/oracle/report.txt

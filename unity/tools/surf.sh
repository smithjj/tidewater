#!/bin/sh
# usage: unity/tools/surf.sh [seaState=2.2] [seconds=7]
# Sets the sea state and runs the sea forward in 1.2 s chunks (a long Advance in one eval would hit the 5 s main-thread limit).
# A recompile resets the ocean, so run this after one.
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
STATE=${1:-2.2}; SECS=${2:-7}
"$U" --no-banner command eval "return Tidewater.EditorTools.OceanDebug.State(${STATE}f);" 2>&1 | tail -1 | cut -c1-120
n=$(python3 -c "import math; print(int(math.ceil($SECS / 1.2)))")
i=0
while [ "$i" -lt "$n" ]; do
	"$U" --no-banner command eval 'Tidewater.EditorTools.OceanDebug.Advance(1.2f); return "ok";' >/dev/null 2>&1
	i=$((i+1))
done

#!/bin/sh
# usage: unity/tools/whale-shot.sh <name> <dist> <yawDeg round from the whale's heading> <pitchDeg down> [aimY] [width] [height]
# Puts the camera next to the whale (Editor/WhaleDebug.cs) and saves unity/Temp/shots/<name>.png from the Game view. RUN=<seconds> first runs the whale forward by that long;
# UNTIL=<state> runs it until the sequencer is in that state.
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
NAME=$1; D=${2:-20}; YAW=${3:-90}; PITCH=${4:-5}; AIM=${5:-0.5}; W=${6:-1280}; H=${7:-720}
mkdir -p Temp/shots
if [ -n "$UNTIL" ]; then "$U" --no-banner command eval "return Tidewater.EditorTools.WhaleDebug.Until(\"$UNTIL\");" 2>&1 | tail -1 | cut -c1-300; fi
if [ -n "$RUN" ]; then "$U" --no-banner command eval "return Tidewater.EditorTools.WhaleDebug.Run($RUN);" 2>&1 | tail -1 | cut -c1-300; fi
"$U" --no-banner command eval "Tidewater.EditorTools.CameraPlacer.Exposure(float.NaN); return Tidewater.EditorTools.WhaleDebug.Look($D, $YAW, $PITCH, $AIM);" 2>&1 | grep -o '"result":"[^}]*' | head -1 | cut -c1-300
sleep ${SETTLE:-8}
"$U" --no-banner command capture_game_view --source camera --width "$W" --height "$H" --save_path "C:\\Users\\smith\\OneDrive\\Documents\\tidewater\\tidewater\\unity\\Temp\\shots\\$NAME.png" >/dev/null 2>&1
if [ -f "Assets/Temp/shots/$NAME.png" ]; then mv "Assets/Temp/shots/$NAME.png" "Temp/shots/$NAME.png"; rm -rf Assets/Temp Assets/Temp.meta; fi
ls -la "Temp/shots/$NAME.png"

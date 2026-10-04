#!/bin/sh
# usage: unity/tools/shot.sh <name> <simX> <simZ> <eyeHeight> <yawDeg> <pitchDeg> [seaVisible=true] [width] [height]
# ADV=<seconds> first runs the sea forward by that long. The Day Night driver volume's eye (auto exposure) overrides the scene volume's, so EV= no longer fixes the exposure while a DayNight is in the scene; EV=auto is the default look.
# Places the camera (sim coordinates: x east, z south; yaw 0 = north, pitch > 0 looks down) and saves unity/Temp/shots/<name>.png from the Game view.
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
EV=${EV:-14}
ADV=${ADV:-0}
NAME=$1; X=$2; Z=$3; EYE=$4; YAW=$5; PITCH=$6; SEA=${7:-true}; W=${8:-1280}; H=${9:-720}
mkdir -p Temp/shots
if [ "$EV" = auto ]; then EVC=float.NaN; else EVC=${EV}f; fi
"$U" --no-banner command eval "Tidewater.EditorTools.OceanDebug.Advance($ADV); Tidewater.EditorTools.CameraPlacer.Exposure($EVC); Tidewater.EditorTools.CameraPlacer.Sea($SEA); return Tidewater.EditorTools.CameraPlacer.Place($X, $Z, $EYE, $YAW, $PITCH);" 2>&1 | tail -1 | cut -c1-200
sleep ${SETTLE:-8}
"$U" --no-banner command capture_game_view --source camera --width "$W" --height "$H" --save_path "C:\\Users\\smith\\OneDrive\\Documents\\tidewater\\tidewater\\unity\\Temp\\shots\\$NAME.png" >/dev/null 2>&1
if [ -f "Assets/Temp/shots/$NAME.png" ]; then mv "Assets/Temp/shots/$NAME.png" "Temp/shots/$NAME.png"; rm -rf Assets/Temp Assets/Temp.meta; fi
ls -la "Temp/shots/$NAME.png"

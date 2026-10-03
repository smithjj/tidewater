#!/bin/sh
# usage: unity/tools/snap.sh <name> [width] [height]   Saves the Game view as unity/Temp/shots/<name>.png with the camera where it is.
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
NAME=$1; W=${2:-960}; H=${3:-540}
mkdir -p Temp/shots
"$U" --no-banner command capture_game_view --source camera --width "$W" --height "$H" --save_path "C:\\Users\\smith\\OneDrive\\Documents\\tidewater\\tidewater\\unity\\Temp\\shots\\$NAME.png" >/dev/null 2>&1
if [ -f "Assets/Temp/shots/$NAME.png" ]; then mv "Assets/Temp/shots/$NAME.png" "Temp/shots/$NAME.png"; rm -rf Assets/Temp Assets/Temp.meta; fi
ls -la "Temp/shots/$NAME.png"

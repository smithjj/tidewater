#!/bin/sh
# Stands the player in front of a vendor in Play mode and takes a picture: unity/tools/at-stall.sh <joe|marta> <distance m> <out.png>
cd "$(dirname "$0")/.." || exit 1
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
if [ "$1" = marta ]; then S=CHANDLERY; Z=-0.75; else S=STAND; Z=0.12; fi
sh tools/ev.sh 'double jx, jz, px, pz; var S = Tidewater.Game.Stalls.'$S'; Tidewater.Game.Stalls.ToWorld(S, 0.0, '$Z', out jx, out jz); Tidewater.Game.Stalls.ToWorld(S, 0.0, '$Z' + '$2', out px, out pz); D.At(px, pz, System.Math.Atan2(-(jx - px), -(jz - pz))); D.Run(0.3); return "at " + px.ToString("F1") + "," + pz.ToString("F1");' 2>&1 | tail -1
for i in 1 2 3; do "$U" --no-banner command eval 'for (int i = 0; i < 10; i++) UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); return 1;' >/dev/null 2>&1; sleep 0.7; done
"$U" --no-banner command capture_game_view --source screen > /tmp/claude-1000/cap.json 2>&1; python3 /tmp/claude-1000/cap.py "$3"

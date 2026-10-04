#!/bin/sh
# usage: unity/tools/ev.sh '<statements; D. stands for Tidewater.EditorTools.GameDebug.>'  -> prints the returned string
U=/mnt/c/Users/smith/AppData/Local/Unity/bin/unity.exe
CODE=$(printf '%s' "$1" | sed 's/\bD\./Tidewater.EditorTools.GameDebug./g')
"$U" --no-banner command eval "$CODE" 2>&1 | sed -n 's/.*"result":"\(.*\)"}\t{"code.*/\1/p;/^Error/,$p' | cut -c1-${W:-700}

#!/bin/sh
# The minimap's oracle: Minimap.js run for real (dump-minimap.mjs) against Minimap.cs (MinimapOracle.Compare, on a worker thread of the open Editor).
#   unity/tools/minimap-oracle.sh
cd "$(dirname "$0")/.." || exit 1
node tools/dump-minimap.mjs Temp/oracle/minimap || exit 1
exec sh tools/run-oracle-bg.sh MinimapOracle minimap

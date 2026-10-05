#!/usr/bin/env python3
# Renders the settings panel's icons (src/ui/icons.js: the 24 px stroke set, 1.6 px strokes, round joins) as white-on-transparent 96 px PNGs for IMGUI, which tints them:
#   python3 -m venv /tmp/iv && /tmp/iv/bin/pip install cairosvg && /tmp/iv/bin/python unity/tools/make-ui-icons.py
# -> unity/Assets/Tidewater/Resources/ui/icons/<name>.png  (the JS aliases are resolved in IconKit, not baked twice)
import json, os, subprocess, sys, cairosvg

here = os.path.dirname(os.path.abspath(__file__))
icons = json.loads(subprocess.check_output(['node', os.path.join(here, 'dump-ui-icons.mjs')]))
out = os.path.join(here, '..', 'Assets', 'Tidewater', 'Resources', 'ui', 'icons')
os.makedirs(out, exist_ok=True)
for name, svg in icons.items():
	svg = svg.replace('currentColor', '#ffffff')
	cairosvg.svg2png(bytestring=svg.encode(), write_to=os.path.join(out, name + '.png'), output_width=96, output_height=96)
print(len(icons), 'icons ->', os.path.normpath(out))

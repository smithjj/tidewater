#!/usr/bin/env python3
# Renders the minimap marker icons (the ICON svg strings of src/game/Minimap.js) as white-on-transparent 128 px PNGs for IMGUI, which tints them:
#   pip install cairosvg && python3 unity/tools/make-map-icons.py
# -> unity/Assets/Tidewater/Resources/ui/map-{fish,anchor,boat,trap,me}.png
import os, cairosvg

ICON = {
	'fish': '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M3 12c3-4 7-6 11-6 3 0 5 2 7 4l-2 2 2 2c-2 2-4 4-7 4-4 0-8-2-11-6Zm12-1.2a1.2 1.2 0 1 0 0 2.4 1.2 1.2 0 0 0 0-2.4Z"/></svg>',
	'anchor': '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><circle cx="12" cy="5" r="2"/><path d="M12 7v13M7 11h10M4 14c1 4 4 6 8 6s7-2 8-6"/></svg>',
	'boat': '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 3v9H6l6-9Zm1 2 5 7h-5V5ZM3 14h18l-3 5H6l-3-5Z"/></svg>',
	'trap': '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M11 3h2v3h-2z"/><path d="M8 7h8l2 4v8H6v-8l2-4Zm1.6 1.6L8.4 11h7.2l-1.2-2.4H9.6ZM7.6 13v4.2h8.8V13H7.6Z"/></svg>',
}
# the arrow in the middle (the player): white with the dark stroke, as the DOM has it (not tinted)
ME = '<svg viewBox="0 0 24 24"><path d="M12 2 20 21 12 16.5 4 21Z" fill="#fff" stroke="#0b1418" stroke-width="1.4" stroke-linejoin="round"/></svg>'
out = os.path.join(os.path.dirname(__file__), '..', 'Assets', 'Tidewater', 'Resources', 'ui')
os.makedirs(out, exist_ok=True)
for name, svg in ICON.items():
	cairosvg.svg2png(bytestring=svg.replace('currentColor', '#ffffff').encode(), write_to=os.path.join(out, f'map-{name}.png'), output_width=128, output_height=128)
	print('map-' + name + '.png')
cairosvg.svg2png(bytestring=ME.encode(), write_to=os.path.join(out, 'map-me.png'), output_width=128, output_height=128)
print('map-me.png')

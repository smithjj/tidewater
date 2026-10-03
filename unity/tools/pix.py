#!/usr/bin/env python3
# usage: pix.py <png> [x,y ...] (fractions of width/height, default a few probes). Prints the sRGB pixel values.
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert('RGB'); w, h = im.size
pts = [tuple(map(float, a.split(','))) for a in sys.argv[2:]] or [(0.5, 0.9), (0.5, 0.7), (0.5, 0.55), (0.5, 0.2)]
print(im.size, ' '.join(f'({x:.2f},{y:.2f})={im.getpixel((int(x*(w-1)), int(y*(h-1))))}' for x, y in pts))

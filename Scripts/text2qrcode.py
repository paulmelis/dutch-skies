#!/usr/bin/env python
# This uses https://pypi.org/project/qrcode/
import sys, json
from PIL import ImageDraw
import qrcode

if len(sys.argv) < 3:
    print('Usage: %s "<text>" qrimage.png' % sys.argv[0])
    sys.exit(-1)

s = sys.argv[1]

img = qrcode.make(s)

# Label image
draw = ImageDraw.Draw(img)
sz = draw.textsize(s)
x = img.size[0]/2 - sz[0]/2
y = img.size[0] - 24
draw.multiline_text((x,y), s, fill='black')
del draw

img.save(sys.argv[2])



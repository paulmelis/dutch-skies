#!/usr/bin/env python
# This uses https://pypi.org/project/qrcode/
import sys, os, json, gzip
from PIL import ImageDraw
import qrcode

data = json.load(open(sys.argv[1],'rt'))
# Dump back to strip unnecessary whitespace
js = json.dumps(data).encode('utf8')
#cs = gzip.compress(s, 9)

img = qrcode.make(js)

# Label image
draw = ImageDraw.Draw(img)
s = os.path.abspath(sys.argv[1])
sz = draw.textsize(s)
x = img.size[0]/2 - sz[0]/2
y = img.size[0] - 24
draw.text((x,y), s, fill='black')
del draw

img.save(sys.argv[2])



#!/usr/bin/env python
# This uses https://pypi.org/project/qrcode/
import sys, json
import qrcode

data = json.load(open(sys.argv[1],'rt'))
# Dump back to strip unnecessary whitespace
s = json.dumps(data)

img = qrcode.make(s)
img.save(sys.argv[2])



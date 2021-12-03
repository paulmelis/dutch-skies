#!/usr/bin/env python
# This uses https://pypi.org/project/qrcode/
import sys, json
import qrcode
from PIL import ImageDraw
import PIL.ImageShow as show

class EOMViewer(show.UnixViewer):
    """Inherit from given class."""
    # pylint: disable=too-few-public-methods,no-self-use,unused-argument

    def get_command_ex(self, file, **options):
        """Define a shell command line"""
        # Fullscreen
        command = "eom -f "
        executable = "/usr/bin/eom"
        return command, executable


if show.shutil.which('eom'):
    show.register(EOMViewer, -1)

if len(sys.argv) < 2:
    print('Usage: %s "<text>"' % sys.argv[0])
    sys.exit(-1)

s = sys.argv[1]

img = qrcode.make(s)
img.show()


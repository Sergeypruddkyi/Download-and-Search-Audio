# One-off helper: build DownloadSearchAudio.ico from the mockup PNG.
# The mockup already carries a transparent background outside the green circle
# (verified: corner pixels are (0,0,0,0)), so no background removal is needed:
# crop to the circle, square it, write a multi-size Windows .ico.
# The original PNG is not modified.
from PIL import Image
import os

SRC = os.path.join(os.path.dirname(__file__), "Download_and_serch audio_.png")
DST = os.path.join(os.path.dirname(__file__), "DownloadSearchAudio.ico")

im = Image.open(SRC).convert("RGBA")
print("source size:", im.size)

bbox = im.getbbox()
print("content bbox:", bbox)
side = max(bbox[2] - bbox[0], bbox[3] - bbox[1])
cx, cy = (bbox[0] + bbox[2]) // 2, (bbox[1] + bbox[3]) // 2
half = side // 2 + 2
square = im.crop((cx - half, cy - half, cx + half, cy + half))
print("square crop:", square.size)

base = square.resize((1024, 1024), Image.LANCZOS)
base.save(DST, format="ICO",
          sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("saved:", DST, os.path.getsize(DST), "bytes")

"""Render the small, flat cat mark at all Windows icon sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / 'Assets'
assets.mkdir(exist_ok=True)
scale = 32
image = Image.new('RGBA', (24 * scale, 24 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 24 * scale - 1, 24 * scale - 1), radius=6 * scale, fill='#203C34')
points = [(5, 4), (9, 7)]
def cubic(p0, p1, p2, p3):
    for i in range(1, 49):
        t = i / 48
        points.append(tuple((1-t)**3*p0[k] + 3*(1-t)**2*t*p1[k] + 3*(1-t)*t*t*p2[k] + t**3*p3[k] for k in (0, 1)))
cubic((9,7), (11,6.2), (13,6.2), (15,7))
points.extend([(19,4), (19,12)])
cubic((19,12), (19,16.5), (16,19.5), (12,19.5))
cubic((12,19.5), (8,19.5), (5,16.5), (5,12))
draw.polygon([(round(x*scale), round(y*scale)) for x,y in points], fill='#7DC5AB')
image = image.resize((256,256), Image.Resampling.LANCZOS)
image.save(assets / 'SneakyFlow.png')
image.save(assets / 'SneakyFlow.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])

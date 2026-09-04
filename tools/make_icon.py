"""Generate myrouter.ico: rounded square gradient bg + white "M" with forward arrow."""
import os
from PIL import Image, ImageDraw


def lerp_color(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))


def make_icon(size: int) -> Image.Image:
    c1 = (30, 64, 175)    # blue-800
    c2 = (6, 182, 212)    # cyan-500
    img = Image.new('RGB', (size, size), c1)
    px = img.load()
    last = 2 * size - 2
    for y in range(size):
        for x in range(size):
            t = (x + y) / last
            px[x, y] = lerp_color(c1, c2, t)
    img = img.convert('RGBA')

    mask = Image.new('L', (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [(0, 0), (size - 1, size - 1)],
        radius=int(size * 0.22), fill=255,
    )
    bg = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    bg.paste(img, mask=mask)

    d = ImageDraw.Draw(bg)
    fg = (255, 255, 255, 255)
    s = size
    stroke = max(2, int(s * 0.13))
    shaft = max(2, int(s * 0.09))

    pad_l = int(s * 0.13)
    pad_t = int(s * 0.20)
    m_w = int(s * 0.44)
    m_h = int(s * 0.60)
    m_left = pad_l
    m_top = pad_t
    m_right = m_left + m_w
    m_bottom = m_top + m_h
    m_mid_x = (m_left + m_right) // 2

    d.line([(m_left, m_top), (m_left, m_bottom)], fill=fg, width=stroke)
    d.line([(m_left, m_top), (m_mid_x, m_bottom)], fill=fg, width=stroke)
    d.line([(m_mid_x, m_bottom), (m_right, m_top)], fill=fg, width=stroke)
    d.line([(m_right, m_top), (m_right, m_bottom)], fill=fg, width=stroke)

    arrow_y = (m_top + m_bottom) // 2
    arrow_end_x = s - int(s * 0.10)
    ah = int(s * 0.16)
    d.line(
        [(m_right - shaft // 2, arrow_y), (arrow_end_x - ah, arrow_y)],
        fill=fg, width=shaft,
    )
    d.polygon(
        [
            (arrow_end_x - ah, arrow_y - ah),
            (arrow_end_x, arrow_y),
            (arrow_end_x - ah, arrow_y + ah),
        ],
        fill=fg,
    )
    return bg


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(here, '_preview')
    os.makedirs(out_dir, exist_ok=True)
    sizes = [256, 128, 64, 48, 32, 16]
    images = []
    for sz in sizes:
        img = make_icon(sz)
        img.save(os.path.join(out_dir, f'icon_{sz}.png'))
        images.append(img)
    ico_path = os.path.join(here, '..', 'myrouter.ico')
    images[0].save(
        ico_path,
        format='ICO',
        sizes=[(s, s) for s in sizes],
        append_images=images[1:],
    )
    print(f'wrote {ico_path}')


if __name__ == '__main__':
    main()

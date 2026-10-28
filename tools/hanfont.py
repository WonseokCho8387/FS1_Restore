"""원작 비트맵 글꼴: 한글 8x4x4벌 조합형(.HAN, 16x16, 360자) + 영문(.ENG, 8x16, 256자)."""
import sys
from PIL import Image

CHO1 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 3, 3, 3, 1, 2, 4, 4, 4, 2, 1, 3, 0]   # 받침 없음 (중성 1..21)
CHO2 = [0, 5, 5, 5, 5, 5, 5, 5, 5, 6, 7, 7, 7, 6, 6, 7, 7, 7, 6, 6, 7, 5]   # 받침 있음
JONG = [0, 0, 2, 0, 2, 1, 2, 1, 2, 3, 0, 2, 1, 3, 3, 1, 2, 1, 3, 3, 1, 1]


def glyph(han, ch):
    """한글 음절 → 16x16 비트 행(int 16개)"""
    s = ord(ch) - 0xAC00
    cho, jung, jong = s // 588 + 1, (s % 588) // 28 + 1, s % 28
    cb = (CHO2 if jong else CHO1)[jung]
    jb = (0 if cho in (1, 16) else 1) + (2 if jong else 0)
    parts = [cb * 20 + cho, 160 + jb * 22 + jung]
    if jong:
        parts.append(248 + JONG[jung] * 28 + jong)
    rows = [0] * 16
    for p in parts:
        for r in range(16):
            rows[r] |= han[p * 32 + r * 2] << 8 | han[p * 32 + r * 2 + 1]
    return rows


def render(han, eng, text, scale=2):
    w = sum(16 if 0xAC00 <= ord(c) <= 0xD7A3 else 8 for c in text)
    im = Image.new("L", (w, 16))
    x = 0
    for c in text:
        if 0xAC00 <= ord(c) <= 0xD7A3:
            rows, cw = glyph(han, c), 16
        else:
            b = ord(c) if ord(c) < 256 else 63
            rows, cw = [eng[b * 16 + r] << 8 for r in range(16)], 8
        for r in range(16):
            for k in range(cw):
                if rows[r] >> (15 - k) & 1:
                    im.putpixel((x + k, r), 255)
        x += cw
    return im.resize((w * scale, 16 * scale), Image.NEAREST)


if __name__ == "__main__":
    iso = "../extracted/iso/"
    eng = open(iso + "NORMAL.ENG", "rb").read()
    out = Image.new("L", (900, 200))
    y = 0
    for f in ("IYG.HAN", "OKSAN.HAN"):
        han = open(iso + f, "rb").read()
        out.paste(render(han, eng, "그렇군 그건 그렇고 이제 너 이상 Lv 7 DAMAGE 48/ 48"), (0, y)); y += 36
    sheet = Image.new("L", (8 * 32, 16 * 8))
    for b in range(256):
        for r in range(16):
            for k in range(8):
                if eng[b * 16 + r] >> (7 - k) & 1:
                    sheet.putpixel((b % 32 * 8 + k, b // 32 * 16 + r), 255)
    out.paste(sheet.resize((512, 256 // 2 * 1), Image.NEAREST).crop((0, 0, 512, 128)), (0, y))
    out.save(sys.argv[1])

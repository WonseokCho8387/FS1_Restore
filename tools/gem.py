"""TGL 'Gem' 이미지(.GEM) 디코더 — FSMAIN.EXE 0x2BBE 루틴을 역분석해 재구현.

헤더(16B): 'Gem', version(2), planes, compress_mode, trans(u16, 0xFFFF=없음),
          width_bytes(u16), height(u16), ctrl_offset(u32)
+0x10 팔레트 24B (2색당 3B, 4bit RGB) / 이어서 NUL 종료 코멘트 / 이어서 패턴 스트림(planes B씩)
ctrl_offset 위치부터 명령 스트림. VRAM 순회는 '8픽셀 바이트 열' 단위의 열 우선(위→아래, 다음 열).
"""
import struct, sys
import numpy as np
from PIL import Image


def read_header(d):
    if d[:3] != b"Gem":
        raise ValueError("not a Gem file")
    ver, planes, mode, trans, wb, h, ctrl = struct.unpack_from("<BBBHHHI", d, 3)
    return dict(version=ver, planes=planes, mode=mode, trans=trans, wbytes=wb, height=h, ctrl=ctrl)


def read_palette(d):
    pal = []
    for k in range(8):
        b0, b1, b2 = d[0x10 + k * 3:0x13 + k * 3]
        pal.append((b0 & 15, b1 >> 4, b0 >> 4))
        pal.append((b2 >> 4, b2 & 15, b1 & 15))
    return [(r * 17, g * 17, b * 17) for r, g, b in pal]


def decode(d):
    hd = read_header(d)
    wb, h, np_ = hd["wbytes"], hd["height"], hd["planes"]
    planes = np.zeros((np_, h * wb), np.uint8)     # column-major index: col*h + row
    written = np.zeros(h * wb, np.uint8)            # 쓰인 픽셀 마스크(비트)
    p = 0x28
    while d[p] != 0:
        p += 1
    comment = d[0x28:p]
    lit_pos = p + 1
    trans = hd["trans"]
    lit = [0] * np_
    mask = 0xFF

    def fetch():
        nonlocal lit_pos, lit, mask
        if hd["mode"] != 0:
            return
        lit = list(d[lit_pos:lit_pos + np_])
        if trans == 0xFFFF:
            mask = 0xFF
        else:
            eq = 0xFF
            for i, v in enumerate(lit):
                eq &= v if trans >> i & 1 else ~v & 0xFF
            mask = ~eq & 0xFF
        lit_pos += np_

    pos = 0
    total = h * wb

    def put():
        if pos >= total:
            return
        for i in range(np_):
            planes[i, pos] = (int(planes[i, pos]) & ~mask & 0xFF) | (lit[i] & mask)
        written[pos] |= mask

    mark, armed = 0, True
    def move(n):
        nonlocal pos, mark, armed
        pos += n
        if not armed:
            mark, armed = pos, True

    fetch(); put()
    s = hd["ctrl"]
    while s < len(d):
        a = d[s]; s += 1
        if a == 0:
            if s >= len(d) or d[s] == 0:
                break
            fetch(); pos = mark; armed = False
            continue
        top = 0x80
        while not a & top:
            top >>= 1
        a &= top - 1
        if a == 0:
            n = d[s] << 8 | d[s + 1]; s += 2
        elif a & (top >> 1):
            n = (a ^ (top >> 1)) << 8 | d[s]; s += 1
        else:
            n = a
        if top == 0x80:
            move(n); put()
        elif top == 0x08:
            for i in range(n):
                if i:
                    fetch()
                move(1); put(); armed = False
            if s >= len(d) or d[s] == 0:
                break
            fetch(); pos = mark; armed = False
        else:
            step = 1 if top == 0x40 else 2 if top == 0x20 else 4
            for _ in range(n):
                move(step); put()

    # column-major → (h, wb) → 픽셀 전개
    idx = np.zeros((h, wb * 8), np.uint8)
    alpha = np.zeros((h, wb * 8), np.uint8)
    for i in range(np_):
        pl = planes[i].reshape(wb, h).T
        bits = np.unpackbits(pl[:, :, None], axis=2).reshape(h, wb * 8)
        idx |= bits << i
    wm = np.unpackbits(written.reshape(wb, h).T[:, :, None], axis=2).reshape(h, wb * 8)
    if trans != 0xFFFF:
        alpha = wm * 255
    else:
        alpha[:] = 255
    return hd, read_palette(d), idx, alpha, comment


def to_image(d):
    hd, pal, idx, alpha, _ = decode(d)
    rgb = np.array(pal, np.uint8)[idx]
    return Image.fromarray(np.dstack([rgb, alpha]), "RGBA")


if __name__ == "__main__":
    for f in sys.argv[1:-1]:
        d = open(f, "rb").read()
        img = to_image(d)
        img.save(sys.argv[-1] + "/" + f.replace("\\", "/").split("/")[-1].rsplit(".", 1)[0] + ".png")
        print(f, img.size, read_header(d))

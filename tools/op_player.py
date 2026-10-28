"""opening_timeline.json 레퍼런스 재생기 — Unity OpeningPlayer.cs와 같은 규칙.

VRAM = 64KB 바이트 주소 공간(1바이트 = 8픽셀) × 640px 피치 → 인덱스 버퍼 819.2줄.
blit: 이미지(640x400 인덱스)의 바이트 주소 src에서 rows×wb 바이트 블록을 VRAM dst로 (피치 80바이트, 64KB 랩).
"""
import json, os
import numpy as np
import gem, toy

VRAM_PIX = 0x10000 * 8


def load_image(iso, name):
    d = open(os.path.join(iso, name), "rb").read()
    if name.endswith(".TOY"):
        _, idx = toy.decode(d)
        return idx, None
    _, _, idx, alpha, _ = gem.decode(d)
    return idx, alpha


class Player:
    def __init__(self, timeline, images):
        self.tl = timeline
        self.images = images                 # name → (idx[h,w], alpha|None)
        self.vram = np.zeros(VRAM_PIX, np.uint8)
        self.oi = self.si = 0
        self.state = dict(start=0, lines=480, blank=False, pal="00" * 48)

    def _rows(self, addr, rows, wb):
        a = (addr + np.arange(rows)[:, None] * 80 + np.arange(wb)[None, :]) & 0xFFFF
        return (a[:, :, None] * 8 + np.arange(8)).reshape(rows, wb * 8)

    def apply(self, o):
        if o["op"] == "clear":
            a = (o["dst"] * 8 + np.arange(o["len"] * 8)) % VRAM_PIX
            self.vram[a] = 0
        elif o["op"] == "blit":
            idx, _ = self.images[o["img"]]
            flat = idx.reshape(-1)
            src = self._rows(o["src"], o["rows"], o["wb"])
            src = np.minimum(src, flat.size - 1)       # 이미지 끝을 넘는 읽기는 원본에서 다음 버퍼 = 미정의
            if o.get("double"):
                rows = np.repeat(np.arange(o["rows"]), 2)
                dst = self._rows(o["dst"], o["rows"] * 2, o["wb"])
                self.vram[dst] = flat[src[rows]]
            else:
                self.vram[self._rows(o["dst"], o["rows"], o["wb"])] = flat[src]
        elif o["op"] == "gem":
            idx, alpha = self.images[o["img"]]
            h, w = idx.shape
            dst = self._rows(o["dst"], h, w // 8)
            m = alpha > 0 if alpha is not None else np.ones_like(idx, bool)
            self.vram[dst[m]] = idx[m]

    def advance(self, t):
        """시각 t 프레임까지의 명령을 적용하고, 그 시점 화면(RGB)을 돌려준다."""
        ops, sts = self.tl["ops"], self.tl["states"]
        while self.oi < len(ops) and ops[self.oi]["t"] <= t:
            self.apply(ops[self.oi]); self.oi += 1
        while self.si < len(sts) and sts[self.si]["t"] <= t:
            self.state.update({k: v for k, v in sts[self.si].items() if k != "t"}); self.si += 1
        return self.render()

    def render(self):
        st = self.state
        lines = st["lines"]
        if st["blank"]:
            return np.zeros((lines, 640, 3), np.uint8)
        pal = np.frombuffer(bytes.fromhex(st["pal"]), np.uint8).reshape(16, 3)
        a = (st["start"] * 8 + np.arange(lines * 640)) % VRAM_PIX
        return pal[self.vram[a].reshape(lines, 640)]

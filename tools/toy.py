"""TGL 'TOY 01' 이미지(.TOY, 오프닝 컷) 디코더 — OP.EXE 0x0E8B 루틴을 역분석해 재구현.

헤더: 'TOY 01\\x1a\\0', u16 가로바이트-1, u16 세로-1, u16 바이트스트림 오프셋(+0x3E 기준)
+0x0E 팔레트 48B (색당 B,R,G 각 4bit) / +0x3E 비트스트림(MSB 우선) / +0x3E+ofs 바이트스트림
출력은 플레인별 버퍼(앞에 2줄 여백)에 행 우선으로 바이트 단위 기록.
  bit 0           : 바이트스트림에서 4플레인 바이트 리터럴 → 256항목 링 사전에 등록, 1바이트 기록
  bit 1, gamma=1  : 사전 인덱스(1B) + gamma 길이 → 그 색으로 길이만큼 채움
  bit 1, gamma=2~7: 기준 위치(위 1/2줄, 위줄±1, 왼쪽 1/2바이트)의 바이트 하나를 길이만큼 반복
"""
import struct
import numpy as np
from PIL import Image

BACK = {2: 0x50, 3: 0xA0, 4: 0x51, 5: 0xA1, 6: 1, 7: 2}


def decode(d):
    if d[:8] != b"TOY 01\x1a\x00":
        raise ValueError("not a TOY file")
    wb_1, h_1, ofs = struct.unpack_from("<HHH", d, 8)
    wb, h = wb_1 + 1, h_1 + 1
    # 팔레트는 색당 B,R,G 순서 (OP.EXE 0x05E1 DAC 출력 순서)
    pal = [(d[0x0F + i * 3] * 17, d[0x10 + i * 3] * 17, d[0x0E + i * 3] * 17) for i in range(16)]
    pitch = 0x50                                    # 루틴은 80바이트 피치 고정
    pad = pitch * 2
    end = pad + pitch * h
    planes = np.zeros((4, end), np.uint8)
    bits_pos, byte_pos = 0x3E, 0x3E + ofs
    dict_ = [bytes(4)] * 256
    ring = 0
    state = {"cur": 0, "left": 0}

    def bit():
        nonlocal bits_pos
        if state["left"] == 0:
            state["cur"] = d[bits_pos]; bits_pos += 1; state["left"] = 8
        state["left"] -= 1
        return state["cur"] >> state["left"] & 1

    def gamma():
        if bit():
            return 1
        n = 1
        while not bit():
            n += 1
        v = 1
        for _ in range(n):
            v = v << 1 | bit()
        return v

    def literal():
        nonlocal byte_pos
        v = d[byte_pos:byte_pos + 4]; byte_pos += 4
        return v

    first = literal()
    planes[:, :pad + 1] = np.frombuffer(first, np.uint8)[:, None]
    bp = pad + 1
    while bp < end:
        if not bit():
            v = literal()
            dict_[ring] = v; ring = (ring + 1) & 0xFF
            planes[:, bp] = np.frombuffer(v, np.uint8); bp += 1
            continue
        code = gamma()
        if code == 1:
            v = dict_[d[byte_pos]]; byte_pos += 1
            n = gamma()
            planes[:, bp:bp + n] = np.frombuffer(v, np.uint8)[:, None]
        else:
            src = bp - BACK.get(code, 0x50)
            n = gamma()
            planes[:, bp:bp + n] = planes[:, src:src + 1]
        bp += n

    planes = planes[:, pad:end].reshape(4, h, pitch)[:, :, :wb]
    idx = np.zeros((h, wb * 8), np.uint8)
    for i in range(4):
        idx |= np.unpackbits(planes[i][:, :, None], axis=2).reshape(h, wb * 8) << i
    return pal, idx


def to_image(d):
    pal, idx = decode(d)
    return Image.fromarray(np.array(pal, np.uint8)[idx], "RGB")

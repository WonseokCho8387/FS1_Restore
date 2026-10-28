"""OP.EXE 오프닝을 Unicorn으로 실행하며 연출 타임라인을 기록한다.

- 장면 함수(0x1F19가 순서대로 호출하는 22개)를 직접 호출
- 파일 로드/대기/음악 등 OS·하드웨어 의존 헬퍼는 스텁으로 대체해 이벤트로 기록
- 나머지(TOY 디코드, 블릿, 팔레트 페이드, 스크롤)는 원본 코드 그대로 실행하고
  VGA 포트 출력과 A000 세그먼트 접근을 가로채 화면 상태(4플레인 VRAM, DAC, CRTC)를 재현
- 대기(vsync) 시점마다 화면을 렌더해 프레임 콜백으로 넘김

사용: python op_emu.py <extracted/iso> <out_dir> [--frames]
"""
import json, os, struct, sys
import numpy as np
import toy
from unicorn import Uc, UC_ARCH_X86, UC_MODE_16, UC_HOOK_CODE, UC_HOOK_INSN, UC_HOOK_MEM_WRITE, \
    UC_HOOK_MEM_READ, UC_HOOK_INTR
from unicorn.x86_const import *

LOAD = 0x1000                       # 로드 세그먼트
DGROUP = 0x66C                      # 상대 DS
SENTINEL = 0xF000                   # 장면 함수 복귀 지점 세그먼트
SCENES = [0x0000, 0x0120, 0x027D, 0x0485, 0x07B4, 0x0A23, 0x0F68, 0x129B, 0x140A, 0x1B60, 0x1D1E,
          0x1E98, 0x2164, 0x2240, 0x23E9, 0x24CE, 0x2672, 0x28CC, 0x2AF6, 0x3021, 0x3109, 0x33B4]
SCENE_SEG = 0x232

# (상대 선형 주소) → (이름, 인자 수, 스텁 여부)
HELPERS = {
    0x0D8D: ("load", 1, True), 0x07BD: ("free", 1, True), 0x07D4: ("wait", 1, True),
    0x1ADF: ("playTrack", 1, True), 0x0BF8: ("stopMusic", 0, True), 0x0BE0: ("musicInit", 1, True),
    0x1502: ("cdStop", 0, True), 0x0161: ("screenOn", 0, True), 0x017B: ("screenOff", 0, True),   # int10 AX=12xx BL=36: AL=0 켬, AL=1 끔
    0x0E8B: ("decodeToy", 4, False), 0x1663: ("drawGem", 4, False), 0x18B0: ("gemPalette", 2, False),
    0x1263: ("blitFull", 2, False), 0x12B8: ("blitDouble", 2, False), 0x131C: ("blitHalf", 4, False),
    0x1382: ("blitRect", 5, False), 0x13EC: ("blitRect4k", 5, False), 0x01D8: ("clearPage", 0, False),
    0x03A8: ("drawPage", 1, False), 0x03C4: ("showPage", 1, False), 0x00FF: ("scroll", 2, False),
    0x016A: ("displayLines", 1, False), 0x05E1: ("setPalette", 1, False), 0x0637: ("fadeStep", 2, False),
    0x1E01: ("syncWait", 3, False), 0x1E47: ("syncCheck", 2, False), 0x2097: ("emsFrame", 1, False), 0x6350: ("memcpy", 5, False),
}


class Vga:
    def __init__(self):
        self.planes = np.zeros((4, 0x10000), np.uint8)
        self.latch = [0, 0, 0, 0]
        self.seq_idx = self.gc_idx = self.crtc_idx = 0
        self.seq = [0] * 8; self.seq[2] = 0xF
        self.gc = [0] * 9; self.gc[8] = 0xFF
        self.crtc = [0] * 0x19; self.crtc[0x18] = 0xFF; self.crtc[7] = 0x10 | 0x02; self.crtc[9] = 0x40
        self.crtc[0x12] = 0xDF
        self.dac = np.zeros((16, 3), np.uint8)
        self.dac_idx = 0; self.dac_sub = 0; self.dac_tmp = [0, 0, 0]
        self.dirty = True
        self.blank = False

    def out(self, port, val):
        if port == 0x3C4: self.seq_idx = val & 7
        elif port == 0x3C5: self.seq[self.seq_idx] = val
        elif port == 0x3CE: self.gc_idx = val & 0xF
        elif port == 0x3CF:
            if self.gc_idx < 9: self.gc[self.gc_idx] = val
        elif port == 0x3D4: self.crtc_idx = val & 0x1F
        elif port == 0x3D5:
            if self.crtc_idx < 0x19: self.crtc[self.crtc_idx] = val; self.dirty = True
        elif port == 0x3C8: self.dac_idx = val; self.dac_sub = 0
        elif port == 0x3C9:
            self.dac_tmp[self.dac_sub] = val & 0x3F; self.dac_sub += 1
            if self.dac_sub == 3:
                if self.dac_idx < 16: self.dac[self.dac_idx] = self.dac_tmp
                self.dac_idx = (self.dac_idx + 1) & 0xFF; self.dac_sub = 0; self.dirty = True

    def read(self, off):
        self.latch = [int(self.planes[p, off]) for p in range(4)]
        return self.latch[self.gc[4] & 3]

    def write(self, off, val):
        mode = self.gc[5] & 3
        rot = self.gc[3] & 7
        if rot and mode in (0, 3):
            val = ((val >> rot) | (val << (8 - rot))) & 0xFF
        for p in range(4):
            if not self.seq[2] >> p & 1:
                continue
            if mode == 1:
                self.planes[p, off] = self.latch[p]; continue
            m = self.gc[8]
            if mode == 0:
                v = (0xFF if self.gc[0] >> p & 1 else 0) if self.gc[1] >> p & 1 else val
            elif mode == 2:
                v = 0xFF if val >> p & 1 else 0
            else:                                       # mode 3: 데이터는 비트마스크, 색은 set/reset
                v = 0xFF if self.gc[0] >> p & 1 else 0
                m &= val
            fn = self.gc[3] >> 3 & 3
            if fn == 1: v &= self.latch[p]
            elif fn == 2: v |= self.latch[p]
            elif fn == 3: v ^= self.latch[p]
            self.planes[p, off] = (v & m) | (self.latch[p] & ~m & 0xFF)
        self.dirty = True

    def start(self):
        return self.crtc[0xC] << 8 | self.crtc[0xD]

    def line_compare(self):
        return self.crtc[0x18] | (self.crtc[7] >> 4 & 1) << 8 | (self.crtc[9] >> 6 & 1) << 9

    def lines(self):
        """CRTC 0x12 세로 표시 끝(+오버플로 비트) + 1. OP.EXE 0x0130이 399/479로 설정."""
        return (self.crtc[0x12] | (self.crtc[7] >> 1 & 1) << 8 | (self.crtc[7] >> 6 & 1) << 9) + 1

    def render(self, lines=None):
        lines = lines or self.lines()
        s, lc = self.start(), self.line_compare()
        rows = np.empty((lines, 80), np.int64)
        for y in range(lines):
            rows[y] = (s + y * 80 + np.arange(80)) & 0xFFFF if y <= lc else ((y - lc - 1) * 80 + np.arange(80)) & 0xFFFF
        idx = np.zeros((lines, 640), np.uint8)
        for p in range(4):
            idx |= np.unpackbits(self.planes[p][rows].astype(np.uint8)[:, :, None], axis=2).reshape(lines, 640) << p
        pal = (self.dac.astype(np.uint16) * 255 // 63).astype(np.uint8)
        return pal[idx]


class OpeningEmu:
    def __init__(self, iso, exe="OP.EXE", dgroup=DGROUP, helpers=None):
        self.iso = iso
        self.helpers = helpers or HELPERS
        raw = open(os.path.join(iso, exe), "rb").read()
        last, pages, nrel, hdr = struct.unpack_from("<HHHH", raw, 2)
        lfarlc = struct.unpack_from("<H", raw, 0x18)[0]
        size = (pages - 1) * 512 + (last or 512)
        image = bytearray(raw[hdr * 16:size])
        for k in range(nrel):
            off, seg = struct.unpack_from("<HH", raw, lfarlc + k * 4)
            a = seg * 16 + off
            struct.pack_into("<H", image, a, (struct.unpack_from("<H", image, a)[0] + LOAD) & 0xFFFF)
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_16)
        uc.mem_map(0, 0x100000)
        uc.mem_write(LOAD * 16, bytes(image))
        uc.mem_write(SENTINEL * 16, b"\xf4")
        self.ds = LOAD + dgroup
        self.vga = Vga()
        self.blocks = {}
        self.time = 0                       # vsync 프레임
        self.events = []
        self.files = {}                     # seg → 파일명
        self.decoded = {}                   # 디코드 대상 플레인 버퍼 seg → 이미지 파일명
        self.ems_src = {}                   # (핸들, 논리페이지) → (이미지, 이미지 내 바이트 오프셋)
        self.ops = []                       # Unity용 그리기 명령
        self.states = []                    # Unity용 프레임 상태(변화분)
        self.used = set()                   # 타임라인이 참조하는 이미지
        self.variants = {}                  # 이미지 이름 → 인덱스 배열(400x640). 버퍼 겹침으로 변형된 것은 'NAME~k' 
        self.frame_cb = None
        self.scene = -1
        self.ems_pages = {}                 # (핸들, 논리페이지) → 16KB
        self.ems_map = [None] * 4           # 물리 페이지 → (핸들, 논리페이지)
        self.init_globals()
        uc.hook_add(UC_HOOK_CODE, self.on_code)
        uc.hook_add(UC_HOOK_INSN, self.on_out, None, 1, 0, UC_X86_INS_OUT)
        uc.hook_add(UC_HOOK_INSN, self.on_in, None, 1, 0, UC_X86_INS_IN)
        uc.hook_add(UC_HOOK_MEM_WRITE, self.on_vram_w, None, 0xA0000, 0xAFFFF)
        uc.hook_add(UC_HOOK_MEM_READ, self.on_vram_r, None, 0xA0000, 0xAFFFF)
        uc.hook_add(UC_HOOK_INTR, self.on_intr)

    def init_globals(self):
        """OP.EXE 초기화 루틴이 하던 전역 변수 설정."""
        self.wr16(0xB71, 5)                 # 사운드 모드 5 = CD-DA (대기는 vsync 프레임 기준)
        self.wr16(0x38, 0xA000)
        self.wr16(0xB5D, self.alloc(0x40000))   # 장면 8은 TOY 2장을 +0x1F40 단락에 이어서 디코드
        self.wr16(0xB5F, self.alloc(0x40))
        self.wr16(0xA86, self.alloc(0x100))
        self.wr16(0xB63, 0xD000)            # EMS 페이지 프레임 세그먼트
        self.wr16(0xB65, 1)                 # EMS 핸들

    # --- 메모리 유틸
    def alloc(self, nbytes):
        """first-fit 힙 (0x3000~0x9F00 세그먼트). free 스텁과 짝."""
        need = (nbytes + 15) // 16 + 1
        seg = 0x3000
        for s, n in sorted(self.blocks.items()):
            if s - seg >= need:
                break
            seg = max(seg, s + n)
        if seg + need > 0x9F00:
            raise MemoryError("opening heap exhausted")
        self.blocks[seg] = need
        return seg

    def rd16(self, off, seg=None):
        return struct.unpack("<H", self.uc.mem_read(((seg or self.ds) * 16 + off) & 0xFFFFF, 2))[0]

    def wr16(self, off, v, seg=None):
        self.uc.mem_write(((seg or self.ds) * 16 + off) & 0xFFFFF, struct.pack("<H", v & 0xFFFF))

    def cstr(self, off):
        b = self.uc.mem_read(self.ds * 16 + off, 64)
        return bytes(b).split(b"\0")[0].decode("latin-1")

    # --- 하드웨어 훅
    def on_out(self, uc, port, size, value, ud):
        self.vga.out(port, value & 0xFF)
        if size == 2:
            self.vga.out(port + 1, value >> 8 & 0xFF)

    def on_in(self, uc, port, size, ud):
        if port == 0x3D5 and self.vga.crtc_idx < 0x19:
            return self.vga.crtc[self.vga.crtc_idx]
        return 0x08 if port == 0x3DA else 0

    def on_vram_w(self, uc, access, addr, size, value, ud):
        for i in range(size):
            self.vga.write((addr - 0xA0000 + i) & 0xFFFF, value >> (8 * i) & 0xFF)

    def on_vram_r(self, uc, access, addr, size, value, ud):
        out = bytes(self.vga.read((addr - 0xA0000 + i) & 0xFFFF) for i in range(size))
        uc.mem_write(addr, out)

    def on_intr(self, uc, intno, ud):
        ax = uc.reg_read(UC_X86_REG_AX); ah, al = ax >> 8, ax & 0xFF
        if intno == 0x67:                           # EMS (LIM 4.0) 최소 구현
            if ah == 0x41:
                uc.reg_write(UC_X86_REG_BX, 0xD000)
            elif ah == 0x42:
                uc.reg_write(UC_X86_REG_BX, 0x100); uc.reg_write(UC_X86_REG_DX, 0x100)
            elif ah == 0x43:
                uc.reg_write(UC_X86_REG_DX, 1)
            elif ah == 0x44:
                self.ems_switch(al, uc.reg_read(UC_X86_REG_DX), uc.reg_read(UC_X86_REG_BX))
            elif ah == 0x46:
                uc.reg_write(UC_X86_REG_AX, 0x40); return
            uc.reg_write(UC_X86_REG_AX, al)         # AH=0 성공
            return
        self.log("int", [hex(intno), hex(ah)])
        if intno == 0x21 and ah == 0x48:            # 메모리 할당
            uc.reg_write(UC_X86_REG_AX, self.alloc(uc.reg_read(UC_X86_REG_BX) * 16))
        flags = uc.reg_read(UC_X86_REG_EFLAGS)
        uc.reg_write(UC_X86_REG_EFLAGS, flags & ~1)

    def ems_switch(self, phys, handle, logical):
        base = 0xD0000 + phys * 0x4000
        old = self.ems_map[phys]
        if old is not None:
            self.ems_pages[old] = bytes(self.uc.mem_read(base, 0x4000))
        key = (handle, logical)
        self.uc.mem_write(base, self.ems_pages.get(key, bytes(0x4000)))
        self.ems_map[phys] = key

    # --- 타임라인 기록
    def draw_base(self):
        return 0 if self.rd16(0x38) == 0xA000 else 0x8000

    def image_at(self, seg):
        """블릿 소스 seg → (이미지, 바이트 오프셋). 디코드 대상 seg 이후로 떨어진 만큼 오프셋을 더한다.

        원본은 여러 장을 0x1F40 단락 간격으로 이어 디코드하지만 한 장은 0x1F68 단락이라
        다음 장이 앞 장 마지막 플레인 하단 8줄을 덮어쓴다. 그래서 그 시점 버퍼 내용을 그대로 떠서
        깨끗한 디코드와 다르면 변형 이미지로 등록한다.
        """
        best = max((t for t in self.decoded if t <= seg), default=None)
        if best is None:
            return None, 0
        return self.snapshot(best), (seg - best) * 16

    def snapshot(self, target):
        name = self.decoded[target]
        raw = np.frombuffer(bytes(self.uc.mem_read((target + 0xA) * 16, 4 * 0x7DA0)), np.uint8)
        planes = raw.reshape(4, 0x7DA0)[:, :32000].reshape(4, 400, 80)
        idx = np.zeros((400, 640), np.uint8)
        for p in range(4):
            idx |= np.unpackbits(planes[p][:, :, None], axis=2).reshape(400, 640) << p
        if name not in self.variants:               # 0번 = 깨끗한 디코드
            self.variants[name] = toy.decode(open(os.path.join(self.iso, name), "rb").read())[1]
        k = 0
        while True:
            key = name if k == 0 else f"{name}~{k}"
            have = self.variants.get(key)
            if have is None:
                self.variants[key] = idx; return key
            if np.array_equal(have, idx):
                return key
            k += 1

    def op(self, **kw):
        kw = dict(t=self.time, **kw)
        if "img" in kw:
            self.used.add(kw["img"])
        self.ops.append(kw)

    def record_state(self):
        v = self.vga
        st = dict(start=v.start(), lines=v.lines(), blank=v.blank,
                  pal="".join("%02X%02X%02X" % tuple(c * 255 // 63 for c in rgb) for rgb in v.dac.tolist()))
        last = self.states[-1] if self.states else {}
        diff = {k: x for k, x in st.items() if last.get("_" + k) != x}
        if diff:
            self.states.append(dict(t=self.time, **diff, **{"_" + k: x for k, x in st.items()}))

    # --- 헬퍼 훅
    def log(self, name, args):
        self.events.append(dict(t=self.time, scene=self.scene, op=name, args=args))

    def args(self, n):
        sp, ss = self.uc.reg_read(UC_X86_REG_SP), self.uc.reg_read(UC_X86_REG_SS)
        return [self.rd16(sp + 4 + 2 * i, ss) for i in range(n)]

    def ret(self, ax=0):
        uc = self.uc
        sp, ss = uc.reg_read(UC_X86_REG_SP), uc.reg_read(UC_X86_REG_SS)
        ip, cs = self.rd16(sp, ss), self.rd16(sp + 2, ss)
        uc.reg_write(UC_X86_REG_SP, sp + 4)
        uc.reg_write(UC_X86_REG_AX, ax)
        uc.reg_write(UC_X86_REG_CS, cs)
        uc.reg_write(UC_X86_REG_IP, ip)

    def on_code(self, uc, addr, size, ud):
        rel = addr - LOAD * 16
        h = self.helpers.get(rel)
        if not h:
            return
        name, n, stub = h
        a = self.args(n)
        if name == "load":
            fn = self.cstr(a[0]).split(":")[-1].upper()
            data = open(os.path.join(self.iso, fn), "rb").read()
            seg = self.alloc(len(data)); uc.mem_write(seg * 16, data)
            self.files[seg] = fn
            self.log("load", [fn]); return self.ret(seg)
        if name == "wait":
            n_ = max(a[0], 1)
            for _ in range(n_):
                self.record_state()
                self.time += 1
                if self.frame_cb and self.vga.dirty:
                    self.frame_cb(self.time, self.vga); self.vga.dirty = False
            # 연속 대기는 합쳐서 기록
            if self.events and self.events[-1]["op"] == "wait" and self.events[-1]["scene"] == self.scene:
                self.events[-1]["args"][0] += n_
            else:
                self.log("wait", [n_])
            return self.ret(0)
        if name == "free":
            self.blocks.pop(a[0], None); self.files.pop(a[0], None)
            return self.ret(0)
        if name == "decodeToy":                      # (TOY seg, 플레인 버퍼 seg, 0, 팔레트 seg)
            self.decoded = {t: f for t, f in self.decoded.items() if not a[1] <= t < a[1] + 0x1F40}
            self.decoded[a[1]] = self.files.get(a[0], hex(a[0]))
            self.log(name, [self.decoded[a[1]]])
            return
        if name == "drawGem":                        # (x바이트, y, seg, ...)
            f = self.files.get(a[2], hex(a[2]))
            self.op(op="gem", img=f, dst=self.draw_base() + a[1] * 80 + a[0])
            self.log(name, [f, a[0], a[1]])
            return
        if name == "clearPage":
            self.op(op="clear", dst=self.draw_base(), len=0x7D00)   # 0x01D8: 0x3E80 워드
        if name == "screenOff":
            self.vga.blank = True
        if name == "screenOn":
            self.vga.blank = False
        if name == "memcpy" and a[1] >= 0xD000:      # 디코드 버퍼 → EMS 프레임 복사 출처 추적
            phys = (a[1] - 0xD000) // 0x400
            img, off = self.image_at(a[3])
            if img and self.ems_map[phys] is not None:
                plane_off = off - 0xA0               # 플레인 버퍼는 앞 2줄 여백
                self.ems_src[self.ems_map[phys]] = (img, plane_off % (0x7DA * 16) + a[2] - a[0])
            return
        if name in ("blitFull", "blitDouble", "blitHalf", "blitRect", "blitRect4k"):
            base = self.draw_base()
            if name == "blitRect4k":                 # (EMS 프레임 seg, src, dst, rows, wbytes)
                img, off = self.ems_src.get(self.ems_map[0], (None, 0))
                self.op(op="blit", img=img, src=off + a[1], dst=base + a[2], rows=a[3], wb=a[4])
            else:
                img, off = self.image_at(a[0])
                off -= 0                              # seg = 디코드 대상 → 이미지 0행
                if name in ("blitFull", "blitDouble"):
                    self.op(op="blit", img=img, src=off, dst=base + a[1], rows=400, wb=80,
                            **({"double": True} if name == "blitDouble" else {}))
                else:
                    self.op(op="blit", img=img, src=off + a[1], dst=base + a[2], rows=a[3],
                            wb=40 if name == "blitHalf" else a[4])
            self.log(name, a)
            return
        if name in ("setPalette", "fadeStep") and a[0]:
            pal = list(uc.mem_read(self.ds * 16 + a[0], 48))
            if name == "setPalette" or not self.events or self.events[-1]["op"] != "fadeStep" \
                    or self.events[-1]["args"][0] != pal:
                self.log(name, [pal] + a[1:])
            return
        if name == "fadeStep":
            if not self.events or self.events[-1]["op"] != "fadeStep" or self.events[-1]["args"][0] != "black":
                self.log(name, ["black"] + a[1:])
            return
        if name in ("syncCheck",):
            return
        self.log(name, a)
        if stub:
            return self.ret(0)

    # --- 실행
    def run_scene(self, i):
        uc = self.uc
        self.scene = i
        ss, sp = self.ds, 0xFFF0
        uc.reg_write(UC_X86_REG_SS, ss); uc.reg_write(UC_X86_REG_SP, sp - 4)
        self.wr16(sp - 4, 0, ss); self.wr16(sp - 2, SENTINEL, ss)
        uc.reg_write(UC_X86_REG_DS, self.ds); uc.reg_write(UC_X86_REG_ES, self.ds)
        uc.reg_write(UC_X86_REG_CS, LOAD + SCENE_SEG); uc.reg_write(UC_X86_REG_IP, SCENES[i])
        self.log("scene", [i])
        uc.emu_start((LOAD + SCENE_SEG) * 16 + SCENES[i], SENTINEL * 16)

    def run(self, verbose=True):
        self.log("playTrack", ["main"])
        for i in range(len(SCENES)):
            self.run_scene(i)
            if verbose:
                print(f"scene {i:2d} done  t={self.time} frames  events={len(self.events)}", flush=True)
        self.record_state()
        # 원본 0x1F19: 마지막 장면 뒤 120프레임 대기 후 음악 정지
        self.time += 120

    def timeline(self):
        # Unity JsonUtility는 '필드 없음'과 기본값을 구분 못 하므로 상태는 변화 시점의 전체 스냅샷으로 낸다
        states = [dict(t=s["t"], **{k[1:]: v for k, v in s.items() if k.startswith("_")}) for s in self.states]
        scenes = [e["t"] for e in self.events if e["op"] == "scene"]
        return dict(fps=60, totalFrames=self.time, track="track02", scenes=scenes,
                    images=sorted(self.used), ops=self.ops, states=states)


def export(iso, out, video=True):
    """Unity용 오프닝 산출물: 타임라인 JSON, 인덱스 이미지(.bytes), 미리보기 MP4(+CD 트랙 2)."""
    import gem
    emu = OpeningEmu(iso)
    emu.run(verbose=False)
    tl = emu.timeline()
    idx_dir = os.path.join(out, "Indexed"); os.makedirs(idx_dir, exist_ok=True)
    meta = []
    for name in tl["images"]:
        if name.endswith(".GEM"):
            _, _, idx, alpha, _ = gem.decode(open(os.path.join(iso, name), "rb").read())
            idx = np.where(alpha > 0, idx, 0xFF).astype(np.uint8)      # 0xFF = 투명
        else:
            idx = emu.variants[name]
        h, w = idx.shape
        meta.append(dict(name=name, w=w, h=h, file=name.replace("~", "_") + ".bytes"))
        open(os.path.join(idx_dir, meta[-1]["file"]), "wb").write(idx.tobytes())
    tl["images"] = meta
    json.dump(tl, open(os.path.join(out, "opening_timeline.json"), "w"), separators=(",", ":"))
    if video:
        render_video(iso, tl, idx_dir, os.path.join(out, "opening_preview.mp4"))
    return tl


def render_video(iso, tl, idx_dir, path):
    """타임라인을 레퍼런스 재생기로 돌려 60fps MP4로 굽고 CD 트랙 2를 입힌다(참고용)."""
    import subprocess, imageio_ffmpeg
    import op_player
    imgs = {m["name"]: (np.frombuffer(open(os.path.join(idx_dir, m["file"]), "rb").read(), np.uint8)
                        .reshape(m["h"], m["w"]), None) for m in tl["images"]}
    for n in imgs:
        if n.endswith(".GEM"):
            idx = imgs[n][0]; imgs[n] = (np.where(idx == 0xFF, 0, idx), (idx != 0xFF).astype(np.uint8))
    p = op_player.Player(tl, imgs)
    wav = os.path.join(os.path.dirname(iso), "audio", "track02.wav")
    cmd = [imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
           "-s", "640x480", "-r", "60", "-i", "-", "-i", wav, "-map", "0:v", "-map", "1:a", "-shortest",
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-c:a", "aac", "-b:a", "160k", path]
    ff = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for t in range(tl["totalFrames"]):
        f = p.advance(t)
        if f.shape[0] < 480:                       # 400줄 모드는 480 캔버스 위쪽에 표시
            f = np.vstack([f, np.zeros((480 - f.shape[0], 640, 3), np.uint8)])
        ff.stdin.write(f.tobytes())
    ff.stdin.close(); ff.wait()


if __name__ == "__main__":
    tl = export(sys.argv[1], sys.argv[2], video="--no-video" not in sys.argv)
    print("frames", tl["totalFrames"], "ops", len(tl["ops"]), "states", len(tl["states"]), "images", len(tl["images"]))

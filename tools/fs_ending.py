"""FSMAIN.EXE 엔딩(0x1114F)을 Unicorn으로 실행해 연출 타임라인과 미리보기 영상을 만든다.

op_emu.OpeningEmu의 VGA/EMS 재현을 그대로 쓰고, FSMAIN 쪽 헬퍼만 다시 매핑한다.
- 0x3640 vsync 대기, 0x32D6 파일 로드, 0x2F16 해제, 0x37CA/0x37CB 마우스 커서: 스텁
- GEM 디코드(0x2BBE), 블록 복사·스크롤(0x187E/0x367E/0x3712/0x376E/0x36D8), 팔레트(0x12A6/0x12FC),
  텍스트 출력(0x298F, 영문 8x16 폰트는 EMS 논리 페이지 0x6F의 NORMAL.ENG): 원본 코드 그대로 실행

사용: python fs_ending.py <extracted/iso> <out_dir>
"""
import json, os, sys
import numpy as np
import op_emu
from op_emu import OpeningEmu

FS_DGROUP = 0x297C
ENDING = 0x1114F
FS_HELPERS = {
    0x3640: ("wait", 1, True), 0x32D6: ("load", 1, True), 0x2F16: ("free", 1, True),
    0x37CA: ("cursorOff", 0, True), 0x37CB: ("cursorOn", 0, True),
    0x2BBE: ("fsDrawGem", 4, False), 0x298F: ("print", 4, False),
    0x12A6: ("fsSetPalette", 1, False), 0x12FC: ("fsFade", 2, False),
    0x15A6: ("screenOn", 0, False), 0x15CC: ("screenOff", 0, False),
}
FONT_PAGE = 0x6F


class EndingEmu(OpeningEmu):
    def __init__(self, iso):
        super().__init__(iso, exe="FSMAIN.EXE", dgroup=FS_DGROUP, helpers=FS_HELPERS)
        self.texts = []                     # (프레임, 문자열)
        self.marks = []                     # (프레임, 이벤트)

    def init_globals(self):
        # FSMAIN 초기화(0x15B3)는 모드 12h 후 세로 표시 끝을 0x190으로 둔다
        self.vga.crtc[0x12] = 0x90; self.vga.crtc[7] |= 0x02
        self.wr16(0x51C, 80)                # VRAM 피치(바이트)
        self.wr16(0x51E, 0xA000)            # 그리기 페이지 세그먼트
        self.wr16(0x520, 0)                 # 표시 시작 오프셋
        self.wr16(0x84F4, 0xD000)           # EMS 페이지 프레임
        self.wr16(0x84F6, 1)                # EMS 핸들
        font = open(os.path.join(self.iso, "NORMAL.ENG"), "rb").read()
        self.font_page = font + bytes(0x4000 - len(font))
        self.ems_pages[(1, FONT_PAGE)] = self.font_page

    def ems_switch(self, phys, handle, logical):
        # 스크롤 임시 버퍼(물리 페이지 3)가 폰트 페이지 창을 같이 쓰므로, 폰트를 매핑할 때마다 원본으로 되돌린다
        # (원본 설치본은 FS.TBR에서 폰트를 읽는데 이 CD에는 없어 NORMAL.ENG로 대신한다)
        super().ems_switch(phys, handle, logical)
        if (handle, logical) == (1, FONT_PAGE):
            self.ems_pages[(1, FONT_PAGE)] = self.font_page
            self.uc.mem_write(0xD0000 + phys * 0x4000, self.font_page)

    def on_code(self, uc, addr, size, ud):
        rel = addr - op_emu.LOAD * 16
        h = self.helpers.get(rel)
        if h and h[0] == "print":
            a = self.args(4)
            text = self.cstr(a[2])
            self.texts.append(dict(t=self.time, text=text))
        elif h and h[0] in ("load", "fsDrawGem", "screenOn", "screenOff"):
            self.marks.append(dict(t=self.time, op=h[0]))
        if h and h[0] == "screenOff":
            self.vga.blank = True
        if h and h[0] == "screenOn":
            self.vga.blank = False
        return super().on_code(uc, addr, size, ud)

    def run(self):
        uc = self.uc
        ss, sp = self.ds, 0xFFF0
        from unicorn.x86_const import UC_X86_REG_SS, UC_X86_REG_SP, UC_X86_REG_DS, UC_X86_REG_ES, \
            UC_X86_REG_CS, UC_X86_REG_IP
        uc.reg_write(UC_X86_REG_SS, ss); uc.reg_write(UC_X86_REG_SP, sp - 4)
        self.wr16(sp - 4, 0, ss); self.wr16(sp - 2, op_emu.SENTINEL, ss)
        uc.reg_write(UC_X86_REG_DS, self.ds); uc.reg_write(UC_X86_REG_ES, self.ds)
        seg = op_emu.LOAD + (ENDING >> 4)
        uc.reg_write(UC_X86_REG_CS, seg); uc.reg_write(UC_X86_REG_IP, ENDING & 0xF)
        uc.emu_start(op_emu.LOAD * 16 + ENDING, op_emu.SENTINEL * 16)
        self.record_state()


def export(iso, out, video=True):
    os.makedirs(out, exist_ok=True)
    emu = EndingEmu(iso)
    frames = []
    if video:
        import subprocess, imageio_ffmpeg
        path = os.path.join(out, "ending_preview.mp4")
        ff = subprocess.Popen([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-loglevel", "error", "-f", "rawvideo",
                               "-pix_fmt", "rgb24", "-s", "640x480", "-r", "60", "-i", "-", "-c:v", "libx264",
                               "-pix_fmt", "yuv420p", "-crf", "20", path], stdin=subprocess.PIPE)
        last = [np.zeros((480, 640, 3), np.uint8)]

        def on_frame(t, vga):
            f = np.zeros((480, 640, 3), np.uint8) if vga.blank else vga.render()
            if f.shape[0] < 480:
                f = np.vstack([f, np.zeros((480 - f.shape[0], 640, 3), np.uint8)])
            last[0] = f

        emu.frame_cb = on_frame
        # dirty 프레임만 콜백되므로 매 프레임 직전 화면을 다시 써서 실시간 길이를 맞춘다
        orig = emu.record_state

        def every_frame():
            orig()
            if emu.vga.dirty:
                on_frame(emu.time, emu.vga); emu.vga.dirty = False
            ff.stdin.write(last[0].tobytes())
        emu.record_state = every_frame
    emu.run()
    if video:
        ff.stdin.close(); ff.wait()
    states = [dict(t=s["t"], **{k[1:]: v for k, v in s.items() if k.startswith("_")}) for s in emu.states]
    tl = dict(fps=60, totalFrames=emu.time, staff=emu.texts, marks=emu.marks, states=states)
    json.dump(tl, open(os.path.join(out, "ending_raw.json"), "w"), ensure_ascii=False, separators=(",", ":"))
    return tl


if __name__ == "__main__":
    tl = export(sys.argv[1], sys.argv[2])
    print("frames", tl["totalFrames"], "texts", len(tl["staff"]), tl["marks"][:6])

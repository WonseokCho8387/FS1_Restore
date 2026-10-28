"""추출된 ISO 파일을 Unity 친화 포맷(PNG/WAV/JSON)으로 변환해 UnityAssets/ 아래에 출력한다.

사용: python export_unity.py <extracted/iso> <extracted/audio> <out_dir>
"""
import glob, json, os, shutil, struct, sys, wave
from PIL import Image
import gem, toy

def chip_xy(i):
    """MAP 타일 인덱스 → MAPCHIP 시트 좌표 (FSMAIN.EXE 0x8304 루틴)."""
    r, c = divmod(i, 40)
    return (c % 20) * 32, r * 32 + (96 if c >= 20 else 0)

def export_gem(iso, out):
    os.makedirs(out, exist_ok=True)
    meta = {}
    for f in sorted(glob.glob(os.path.join(iso, "*.GEM"))):
        d = open(f, "rb").read()
        name = os.path.basename(f)[:-4]
        gem.to_image(d).save(os.path.join(out, name + ".png"))
        hd = gem.read_header(d)
        meta[name] = dict(width=hd["wbytes"] * 8, height=hd["height"],
                          transparentIndex=None if hd["trans"] == 0xFFFF else hd["trans"],
                          palette=["#%02X%02X%02X" % c for c in gem.read_palette(d)])
    json.dump(meta, open(os.path.join(out, "_gem_meta.json"), "w"), indent=1)
    return len(meta)

def export_tileset(sprites, out):
    """MAPCHIP/MAPCHIP2를 인덱스 순 32x32 타일 아틀라스(10열)로 재배치."""
    os.makedirs(out, exist_ok=True)
    for name in ("MAPCHIP", "MAPCHIP2"):
        src = Image.open(os.path.join(sprites, name + ".png"))
        atlas = Image.new("RGBA", (320, 32 * 12))
        for i in range(120):
            x, y = chip_xy(i)
            atlas.paste(src.crop((x, y, x + 32, y + 32)), ((i % 10) * 32, (i // 10) * 32))
        atlas.save(os.path.join(out, name + "_tiles.png"))

def export_maps(iso, sprites, out):
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "STAGE*.MAP"))):
        d = open(f, "rb").read()
        w, h = struct.unpack_from("<HH", d, 6)
        name = os.path.basename(f)[:-4]
        tiles = list(d[0x30:0x30 + w * h])
        # FSMAIN 0x811A·0x8246: [0x568](0부터)==13, 즉 STAGE14 만 c:mapchip2.gem 과 지형표 DS:0x1246
        chipset = "MAPCHIP2" if name == "STAGE14" else "MAPCHIP"
        # 헥스 격자: 짝수 행이 반 칸(16px) 오른쪽 (FSMAIN 0x9A5A 이웃 규칙, 'even-r' 오프셋)
        json.dump(dict(name=name, width=w, height=h, tileSize=32, layout="hex-even-r", tileset=chipset + "_tiles",
                       tiles=tiles), open(os.path.join(out, name + ".json"), "w"))
        src = Image.open(os.path.join(sprites, chipset + ".png"))
        img = Image.new("RGBA", (w * 32 + 16, h * 32))
        for k, t in enumerate(tiles):
            x, y = chip_xy(t)
            row = k // w
            img.paste(src.crop((x, y, x + 32, y + 32)), ((k % w) * 32 + (16 if row % 2 == 0 else 0), row * 32))
        img.save(os.path.join(out, name + "_preview.png"))

def export_sprites(iso, sprites, out):
    """SPZ(FSPR) → 프레임 조립 PNG 스트립 + JSON (FSMAIN.EXE 0x16BE9 루틴).

    프레임 = [칩 수, 앵커X, 앵커Y] + (x, y<<2 | chip>>8, chip&0xFF) × N.
    x/y/앵커는 640x400 화면의 16px 셀 단위, 칩은 같은 이름 GEM의 16x16 셀(가로 40개, 행 우선).
    """
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "*.SPZ"))):
        d = open(f, "rb").read()
        n = struct.unpack_from("<H", d, 4)[0]
        title = d[6:0x20].split(b"\0")[0].decode("latin-1")
        offs = list(struct.unpack_from(f"<{n}H", d, 0x20)) + [len(d)]
        frames = []
        for a, b in zip(offs, offs[1:]):
            parts = [dict(x=d[i] * 16, y=(d[i + 1] >> 2) * 16, chip=(d[i + 1] & 3) << 8 | d[i + 2])
                     for i in range(a + 3, b - 2, 3)]
            frames.append(dict(anchorX=d[a + 1] * 16, anchorY=d[a + 2] * 16, parts=parts))
        name = os.path.basename(f)[:-4]

        # 모든 프레임 공통 캔버스(합집합 bbox) → 같은 pivot으로 애니메이션 가능
        allp = [p for fr in frames for p in fr["parts"]]
        x0, y0 = min(p["x"] for p in allp), min(p["y"] for p in allp)
        x1, y1 = max(p["x"] for p in allp) + 16, max(p["y"] for p in allp) + 16
        cw, ch = x1 - x0, y1 - y0
        sheet = Image.open(os.path.join(sprites, name + ".png"))
        strip = Image.new("RGBA", (cw * len(frames), ch))
        for k, fr in enumerate(frames):
            for p in fr["parts"]:
                sx, sy = p["chip"] % 40 * 16, p["chip"] // 40 * 16
                cell = sheet.crop((sx, sy, sx + 16, sy + 16))
                strip.paste(cell, (k * cw + p["x"] - x0, p["y"] - y0), cell)
        strip.save(os.path.join(out, name + "_frames.png"))
        ax, ay = frames[0]["anchorX"], frames[0]["anchorY"]
        json.dump(dict(name=name, title=title, sheet=name, chipSize=16, chipsPerRow=40,
                       frameWidth=cw, frameHeight=ch, frameCount=len(frames),
                       # Unity Sprite pivot (0..1, 좌하단 원점). 앵커 셀의 아래쪽 = 발밑
                       pivotX=(ax - x0) / cw, pivotY=1 - (ay + 16 - y0) / ch,
                       screenOrigin=[x0, y0], frames=frames),
                  open(os.path.join(out, name + ".json"), "w"))

def export_text(iso, out):
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "*.MSG"))):
        txt = open(f, "rb").read().decode("cp949", "replace")
        open(os.path.join(out, os.path.basename(f)[:-4] + ".txt"), "w", encoding="utf-8").write(
            txt.replace("\x00", "\n"))

def export_pcm(iso, out):
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "*.PCM"))):
        d = open(f, "rb").read()
        rate = 1000000 // (256 - d[3])                      # SB DSP time constant
        n = struct.unpack_from("<I", d, 8)[0]
        with wave.open(os.path.join(out, os.path.basename(f)[:-4] + ".wav"), "wb") as w:
            w.setnchannels(1); w.setsampwidth(1); w.setframerate(rate)
            w.writeframes(d[13:13 + n])

def export_battle_maps(iso, sprites, out):
    """BMAP_?.MPZ(`MAP1`, u16 w=80, u16 h=15, 이름, +0x20 u16 칩 인덱스) + BMAP_?.GEM(16x16 칩, 가로 40개).

    FSMAIN.EXE 0x15BEC 루틴. 1280x240 횡스크롤 전투 배경. XMAP/MMAP/SAMP/MCP/OJZ는 게임에서 참조하지 않는 에디터 잔여물.
    """
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "BMAP_?.MPZ"))):
        d = open(f, "rb").read()
        w, h = struct.unpack_from("<HH", d, 4)
        tiles = list(struct.unpack_from(f"<{w * h}H", d, 0x20))
        name = os.path.basename(f)[:-4]
        sheet = Image.open(os.path.join(sprites, name + ".png"))
        per = sheet.width // 16
        img = Image.new("RGBA", (w * 16, h * 16))
        for k, t in enumerate(tiles):
            sx, sy = t % per * 16, t // per * 16
            img.paste(sheet.crop((sx, sy, sx + 16, sy + 16)), (k % w * 16, k // w * 16))
        img.save(os.path.join(out, name + ".png"))
        json.dump(dict(name=name, title=d[8:0x20].split(b"\0")[0].decode("latin-1").strip(),
                       width=w, height=h, tileSize=16, tileset=name, tilesPerRow=per, tiles=tiles),
                  open(os.path.join(out, name + ".json"), "w"))

def export_opening(iso, out):
    """오프닝 컷(OP.EXE가 읽는 FSC*.TOY) → 640x400 PNG, 그리고 OP.EXE 에뮬레이션으로 뽑은 연출 타임라인."""
    import op_emu
    os.makedirs(out, exist_ok=True)
    for f in sorted(glob.glob(os.path.join(iso, "*.TOY"))):
        toy.to_image(open(f, "rb").read()).save(os.path.join(out, os.path.basename(f)[:-4] + ".png"))
    op_emu.export(iso, out)

if __name__ == "__main__":
    iso, audio, out = sys.argv[1:4]
    spr = os.path.join(out, "Sprites")
    print("GEM", export_gem(iso, spr))
    export_tileset(spr, os.path.join(out, "Tilesets"))
    export_maps(iso, spr, os.path.join(out, "Maps"))
    export_sprites(iso, spr, os.path.join(out, "SpriteDefs"))
    export_battle_maps(iso, spr, os.path.join(out, "BattleMaps"))
    export_opening(iso, os.path.join(out, "Opening"))
    export_text(iso, os.path.join(out, "Text"))
    import story_export
    story_export.export_dialogue(iso, out)
    story_export.export_ending(iso, audio, out)
    import fs_battle_data
    os.makedirs(os.path.join(out, "Battle"), exist_ok=True)
    fs_battle_data.export(os.path.join(iso, "FSMAIN.EXE"), os.path.join(out, "Battle", "battle_data.json"))
    export_pcm(iso, os.path.join(out, "Audio/SFX"))
    bgm = os.path.join(out, "Audio/BGM"); os.makedirs(bgm, exist_ok=True)
    for f in glob.glob(os.path.join(audio, "*.wav")):
        shutil.copy(f, bgm)
    print("done")

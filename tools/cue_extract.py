"""CUE/BIN 분석 및 추출: 데이터 트랙(MODE1/2352)의 ISO9660 파일시스템과 오디오 트랙(WAV)을 뽑아낸다."""
import os, re, struct, sys, wave

SECTOR = 2352

def msf_to_lba(m, s, f):
    return (m * 60 + s) * 75 + f

def parse_cue(path):
    tracks = []
    for line in open(path, encoding="latin-1"):
        t = line.split()
        if not t:
            continue
        if t[0] == "TRACK":
            tracks.append({"no": int(t[1]), "mode": t[2]})
        elif t[0] == "INDEX" and t[1] == "01":
            tracks[-1]["lba"] = msf_to_lba(*map(int, t[2].split(":")))
    return tracks

class Mode1Image:
    def __init__(self, f, start_lba=0):
        self.f, self.start = f, start_lba
    def read(self, lba, count=1):
        out = bytearray()
        for i in range(count):
            self.f.seek((self.start + lba + i) * SECTOR + 16)
            out += self.f.read(2048)
        return bytes(out)

def walk(img, lba, size, prefix, out):
    data = img.read(lba, (size + 2047) // 2048)
    pos = 0
    while pos < len(data):
        rl = data[pos]
        if rl == 0:
            pos = (pos // 2048 + 1) * 2048
            continue
        rec = data[pos:pos + rl]
        ext = struct.unpack_from("<I", rec, 2)[0]
        ln = struct.unpack_from("<I", rec, 10)[0]
        flags = rec[25]
        nl = rec[32]
        name = rec[33:33 + nl]
        pos += rl
        if name in (b"\x00", b"\x01"):
            continue
        name = name.decode("latin-1").split(";")[0]
        p = prefix + "/" + name
        if flags & 2:
            walk(img, ext, ln, p, out)
        else:
            out.append((p, ext, ln))

def main(cue, outdir):
    tracks = parse_cue(cue)
    binpath = os.path.join(os.path.dirname(cue), re.search(r'FILE "(.+?)"', open(cue, encoding="latin-1").read()).group(1))
    total = os.path.getsize(binpath) // SECTOR
    for i, t in enumerate(tracks):
        t["end"] = tracks[i + 1]["lba"] if i + 1 < len(tracks) else total
    f = open(binpath, "rb")
    img = Mode1Image(f, tracks[0]["lba"])
    pvd = img.read(16)
    assert pvd[1:6] == b"CD001", "ISO9660 PVD 없음"
    print("Volume ID :", pvd[40:72].decode("latin-1").strip())
    print("System ID :", pvd[8:40].decode("latin-1").strip())
    print("Publisher :", pvd[318:446].decode("latin-1").strip())
    print("App ID    :", pvd[574:702].decode("latin-1").strip())
    print("Created   :", pvd[813:830].decode("latin-1"))
    root = pvd[156:190]
    files = []
    walk(img, struct.unpack_from("<I", root, 2)[0], struct.unpack_from("<I", root, 10)[0], "", files)
    fsdir = os.path.join(outdir, "iso")
    for p, ext, ln in files:
        dst = os.path.join(fsdir, p.lstrip("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, "wb") as o:
            o.write(img.read(ext, (ln + 2047) // 2048)[:ln])
        print(f"  {p:40s} lba={ext:6d} size={ln}")
    adir = os.path.join(outdir, "audio")
    os.makedirs(adir, exist_ok=True)
    for t in tracks:
        if t["mode"] != "AUDIO":
            continue
        f.seek(t["lba"] * SECTOR)
        raw = f.read((t["end"] - t["lba"]) * SECTOR)
        with wave.open(os.path.join(adir, f"track{t['no']:02d}.wav"), "wb") as w:
            w.setnchannels(2); w.setsampwidth(2); w.setframerate(44100)
            w.writeframes(raw)
        print(f"  track{t['no']:02d}.wav  {(t['end']-t['lba'])/75:.1f}s")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])

"""STAGExx.MSG 대화 스크립트 → JSON. 문법은 FSMAIN.EXE 0xE9C0 talk(이벤트, 유닛0, 유닛1, ...) 해석기 기준.

파일은 NUL로 끝나는 줄의 나열. '#X'(X=이벤트 문자) 뒤에 바로 첫 줄이 붙는다.
  <NN / >NN       왼쪽/오른쪽 초상화 = FACE{NN}.GEM.  <*d = 인자 d 유닛의 얼굴.  '<' 만 있으면 지움
                  (JSON: face=NN 또는 arg=d, 둘 다 -1이면 지움)
  ?               조건 끝(스킵 해제)            ?^  스킵 반전(else)
  ?r              50% 확률로 스킵               ?dm / ?df  인자 d가 남성/여성일 때만 (아니면 스킵)
  ?dNN            인자 d 유닛의 얼굴 번호가 NN일 때만
  q / a / e       블록 끝. q=창 닫고 화면 복구, a=초상화만 다시 그림, e=맵 화면 갱신
  그 외는 본문:   [w](창 지우기) [화자] [k](이 줄 뒤 키 대기 → 다음 페이지) 본문
     화자 d       인자 d 유닛의 캐릭터 이름 + 구분자, 본문은 16칸 들여씀
     화자 /d      인자 d(문자열)를 이름 자리에
     화자 .d      인자 d(문자열)를 회색 한 줄로 먼저 출력
STAGE5x.MSG는 이벤트 ID ≥ 0x100 (파일 번호 = ID/256, 블록 = ID%256)으로 열린다.
"""
import glob, json, os, sys


def parse_line(raw):
    s = raw.decode("cp949", "replace")
    c = s[:1]
    if c in ("<", ">"):
        side = "left" if c == "<" else "right"
        rest = s[1:].strip()
        if not rest:
            return dict(cmd="portrait", side=side, face=None)
        if rest[0] == "*":
            return dict(cmd="portrait", side=side, argFace=int(rest[1]))
        return dict(cmd="portrait", side=side, face=int(rest[:2]))
    if c == "?":
        r = s[1:].strip()
        if not r:
            return dict(cmd="endif")
        if r == "^":
            return dict(cmd="else")
        if r == "r":
            return dict(cmd="if", cond="random")
        arg = int(r[0])
        if r[1:2] == "m":
            return dict(cmd="if", cond="male", arg=arg)
        if r[1:2] == "f":
            return dict(cmd="if", cond="female", arg=arg)
        return dict(cmd="if", cond="face", arg=arg, face=int(r[1:3]))
    if c in ("q", "a", "e") and s[1:].strip() == "":
        return dict(cmd="end", mode=c)
    out = dict(cmd="text")
    i = 0
    if s[i:i + 1] == "w":
        out["clear"] = True; i += 1
    if i < len(s) and "." <= s[i] <= "9":
        if s[i] == ".":
            out["speaker"] = dict(kind="argLine", arg=int(s[i + 1])); i += 2
        elif s[i] == "/":
            out["speaker"] = dict(kind="argString", arg=int(s[i + 1])); i += 2
        else:
            out["speaker"] = dict(kind="unitName", arg=int(s[i])); i += 1
    if s[i:i + 1] == "k":
        out["wait"] = True; i += 1
    out["text"] = s[i:].rstrip()
    return out


FIELDS = dict(cmd="", side="", face=-1, arg=-1, cond="", mode="", speaker="", text="", clear=False, wait=False)


def flatten(c):
    """Unity JsonUtility용: 모든 필드를 명시하고(없음=-1/빈 문자열) 화자를 평탄화한다."""
    out = dict(FIELDS)
    for k, v in c.items():
        if k == "speaker":
            out["speaker"], out["arg"] = v["kind"], v["arg"]
        elif k == "argFace":
            out["arg"] = v
        elif v is not None:
            out[k] = v
    return out


def characters(exe_path):
    """FSMAIN DS:0x2CEE 이름 표. 인덱스 = 유닛 +0x0A, 얼굴 번호 = 인덱스-1."""
    import struct
    raw = open(exe_path, "rb").read(); ds = raw[0x3E00 + 0x297C0:]
    out = []
    for i in range(1, 40):
        p = struct.unpack_from("<H", ds, 0x2CEE + i * 2)[0]
        out.append(dict(id=i, face=i - 1, name=ds[p:ds.index(bytes(1), p)].decode("cp949").strip()))
    return out


def parse_file(path):
    d = open(path, "rb").read()
    blocks, cur = [], None
    for raw in d.split(b"\0"):
        if raw[:1] == b"#":
            cur = dict(id=raw[1:2].decode("latin-1"), commands=[])
            blocks.append(cur)
            raw = raw[2:]
            if not raw:
                continue
        if cur is None or not raw.strip():
            continue
        cur["commands"].append(flatten(parse_line(raw)))
    return blocks


def export(iso, out):
    os.makedirs(out, exist_ok=True)
    n = 0
    for f in sorted(glob.glob(os.path.join(iso, "STAGE*.MSG"))):
        name = os.path.basename(f)[:-4]
        blocks = parse_file(f)
        json.dump(dict(name=name, blocks=blocks), open(os.path.join(out, name + ".json"), "w", encoding="utf-8"),
                  ensure_ascii=False, indent=1)
        n += len(blocks)
    json.dump(dict(characters=characters(os.path.join(iso, "FSMAIN.EXE"))),
              open(os.path.join(out, "characters.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return n


if __name__ == "__main__":
    print("blocks", export(sys.argv[1], sys.argv[2]))

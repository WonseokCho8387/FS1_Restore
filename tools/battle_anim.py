"""전투 연출 애니메이션 추출 (FSMAIN.EXE).

전투 화면의 각 쪽은 기록(DS:0x9F90 + 쪽×0x36)을 갖고, 매 틱 클래스별 함수(far 표 DS:0x61A8 + 클래스×4)가
기록 +0x26(연출 상태)에 따라 프레임(+0x08)을 바꾼다. 상태 6 이상이 공격 연출이며 무기·공격 방식으로 고른다(0x183AD).
공격 연출 함수는 단계별로 프레임을 정하는 상태 기계라 코드 순서가 곧 시간 순서다. 여기서는 그 순서대로
  - 즉값 프레임 (mov [si+8], n)
  - 표 프레임 (mov ax,[bx+표] → [si+8], 반복 횟수는 이어지는 cmp [si+0x2A], n)
  - 계수 프레임 (mov ax,[si+0x2A]; add ax,k → [si+8])
  - 피해 계산 호출(0x17E01 = lcall 0x1735:0xAB1) 위치, 상대 쪽으로 달려가는지(DS:0x63AA 이동 보폭 참조)
를 모아 클립을 만든다. 정적 근사라 세부 타이밍·분기(예: 마법 이펙트)는 원작과 다를 수 있다.
"""
import json, struct, sys
import capstone

EXE = "../extracted/iso/FSMAIN.EXE"           # tools/ 기준
CLASS_FUNCS = 0x61A8          # far 포인터 × 42
STATE_SELECT = 0x18708        # 0x183AD 의 클래스별 case 표 (세그 0x1735)
STATE_SELECT_SEG = 0x1735
DAMAGE_CALL = "0x1735, 0xab1"
COMMON = dict(stance=0, hit=1, fall=2, down=3)   # 모든 SPZ 공통: 대기 0(상태 0), 피격 1(0x17EFC), 쓰러짐 2·3
SOUND_CALL = "0xcc8, 0x21"    # 효과음 0xCCA1(n): n<60 FSEFF{n}, 60~ MV{n-60}, 112~ FV{n-112} (DS:0x7FD 파일 목록)

# 공용 마법 연출 루틴: 시전자 SPZ 안의 이펙트 프레임을 별도 기록(DS:0x6118)으로 띄운다
SPELLS = {
    0x18CFB: dict(name="fire", frames=[4], move=True, sound=0),                          # 불덩이: 프레임 4, 보폭 3
    0x18F63: dict(name="ice", frames=[5, 6, 7, 8], move=True, sound=43),                 # 얼음: 5 + k%4
    0x191EF: dict(name="lightning", frames=[9, 10], move=True, sound=44),                # 번개: 9 + k%2
    0x19425: dict(name="burning", frames=[11, 12, 13, 14, 15, 16, 17], move=False, sound=9,
                  soundFrames=[11, 14], hits=2),                                          # 버닝: 상대 위 11 + k%7, 2연타
    0x19615: dict(name="firebird", launch=[18, 19, 20, 21, 22], frames=[23, 24, 25], move=True, sound=43),
}
SPELL_BY_ARG = {0x19A53: [0x18CFB, 0x18F63, 0x191EF]}   # 위저드 계열: 인자 0/1/2 → 불·얼음·번개

# 엘프 계열 정령 소환 (0x1EE42 → 0x1EB2D 분기, 인자 = 무기 플래그>>8 - 1). 각 정령은 자기 SPZ 를 읽어 화면 전체 연출을 한다.
# 여기서는 시전 프레임 8·9 뒤 정령 시트 전체를 상대 위에서 재생하는 근사로 옮긴다.
SPIRIT_DISPATCH = 0x1EB2D
SPIRITS = [
    dict(name="ifrit", routine="0x1e611", sheet="SpriteDefs/CHAR_98", sound=44),      # 이후리트 (push 0x62 → CHAR_98)
    dict(name="frigga", routine="0x1e05e", sheet="SpriteDefs/CHAR_97", sound=43),     # 후릿가 (CHAR_97)
    dict(name="jinn", routine="0x1d748", sheet="SpriteDefs/CHAR_95", sound=44),       # 진 (CHAR_95 'JINN')
    dict(name="daidan", routine="0x1dcf2", sheet="SpriteDefs/CHAR_96", sound=8),      # 다이단 (CHAR_96, 화면 흔들기 위주)
]
SPIRIT_BODY = [8, 9, 0]
HIT_SOUNDS = dict(miss=34, hit=28, critical=25)          # 0x288C3: 피해 0 / 보통 / 크리티컬


class Fs:
    def __init__(self, path):
        raw = open(path, "rb").read()
        self.img = raw[0x3E00:]
        self.code = self.img[:0x297C0]
        self.ds = self.img[0x297C0:]
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)

    def w(self, a):
        return struct.unpack_from("<h", self.ds, a)[0]

    def dis(self, start, size=0x600):
        out = []
        for i in self.md.disasm(self.code[start:start + size], start):
            out.append(i)
            if i.mnemonic == "retf":
                break
        return out


def imm(s):
    try:
        return int(s, 16) if s.startswith("0x") or s.startswith("-0x") else int(s)
    except ValueError:
        return None


def state_rules(fs, cls):
    """0x183AD 의 클래스별 case 를 조건 조합마다 실행해 연출 상태를 구한다.
    입력: mode(0 직접/1 간접), item(무기 번호), spirit(무기 플래그 0x20), vsPlayer(상대 클래스 < 18).
    반환: 규칙 목록 [{item(-1=그 외), mode, spirit, vsPlayer, state(-1=6·7 무작위)}]"""
    off = struct.unpack_from("<H", fs.code, STATE_SELECT + cls * 2)[0]
    a = STATE_SELECT_SEG * 16 + off
    ins = fs.dis(a, 0x100)
    idx = {i.address: k for k, i in enumerate(ins)}
    items = sorted({imm(i.op_str.split(",")[1].strip()) for i in ins if i.mnemonic == "cmp" and i.op_str.startswith("di,")} - {None})

    def run(mode, item, spirit, vs_player):
        state, flag, k, steps = None, None, 0, 0
        while k < len(ins) and steps < 200:
            i, steps = ins[k], steps + 1
            s = i.op_str
            if i.mnemonic == "jmp":
                t = imm(s)
                if t == 0x18704 or t not in idx:
                    break
                k = idx[t]; continue
            if i.mnemonic in ("ret", "retf"):
                break
            if i.mnemonic == "cmp" and s.startswith("di,"):
                flag = ("eq", item == imm(s.split(",")[1].strip()))
            elif i.mnemonic == "test" and "+ 0xc], 0x20" in s:
                flag = ("eq", not spirit)                         # ZF=1 이면 정령 무기 아님
            elif i.mnemonic == "cmp" and "+ 2], 0x12" in s:
                flag = ("lt", vs_player)                          # 상대 클래스 < 0x12
            elif i.mnemonic in ("je", "jne", "jl", "jge", "jle", "jg") and flag is not None:
                kind, v = flag
                take = {"je": v, "jne": not v, "jl": v, "jge": not v, "jle": v, "jg": not v}[i.mnemonic]
                t = imm(s)
                if take and t in idx:
                    k = idx[t]; continue
            elif "- 0x50f4]" in s and i.mnemonic == "add":
                state = mode + imm(s.split(",")[1].strip())
            elif "- 0x50f4]" in s and i.mnemonic == "mov":
                v = imm(s.split(",")[1].strip())
                state = -1 if v is None else v                    # ax(무작위 0/1 + 6)
            k += 1
        return 6 if state is None else state

    base = {(m, sp, vs): run(m, -999, sp, vs) for m in (0, 1) for sp in (False, True) for vs in (True, False)}
    items = [it for it in items if any(run(m, it, sp, vs) != base[(m, sp, vs)] for (m, sp, vs) in base)]
    rules = []
    for item in [-1] + items:
        for mode in (0, 1):
            for spirit in (False, True):
                for vs in (True, False):
                    rules.append(dict(item=item, mode=mode, spirit=int(spirit), vsPlayer=int(vs),
                                      state=run(mode, item if item >= 0 else -999, spirit, vs)))
    return rules


def state_handlers(fs, cls):
    off, seg = struct.unpack_from("<HH", fs.ds, CLASS_FUNCS + cls * 4)
    f = seg * 16 + off
    ins = fs.dis(f, 0x60)
    n, table = None, None
    for i in ins:
        if i.mnemonic == "cmp" and i.op_str.startswith("bx,"):
            n = imm(i.op_str.split(",")[1].strip())
        if i.mnemonic == "jmp" and "cs:[bx +" in i.op_str:
            table = imm(i.op_str.split("+")[1].strip(" ]"))
            break
    if table is None:
        return f, {}
    offs = struct.unpack_from(f"<{n + 1}H", fs.code, seg * 16 + table)
    return f, {s: seg * 16 + o for s, o in enumerate(offs)}


def anim_function(fs, handler):
    """상태 처리부가 부르는 near 연출 함수(push cs; call X)와 그 앞의 즉값 인자. 없으면 (None, None)"""
    ins = fs.dis(handler, 0x20)
    for k, (a, b) in enumerate(zip(ins, ins[1:])):
        if a.mnemonic == "push" and a.op_str == "cs" and b.mnemonic == "call":
            arg = next((imm(j.op_str) for j in reversed(ins[:k]) if j.mnemonic == "push" and imm(j.op_str) is not None), None)
            return imm(b.op_str), arg
        if b.mnemonic == "jmp":
            break
    return None, None


RECORD_BASE = "0x9f90"          # 전투 기록 배열


def clip(fs, func, arg=None, depth=0):
    """연출 함수에서 자기 몸 프레임·이펙트 프레임·피해 시점·달리기·효과음·공용 마법을 코드 순서로 모은다.
    기록을 가리키는 레지스터/지역 변수의 역할을 추적한다: [bp+6] 쪽 = 자기(own), [0x6118]/[+0x2E] = 이펙트(fx)."""
    ins = fs.dis(func, 0x900)
    role, src = {}, None
    frames, fx, fx_at, fx_move = [], [], None, False
    hit_at, run, sounds, spells_here = None, False, [], []

    def frame_values(k):
        i = ins[k]
        val = i.op_str.split(",")[1].strip()
        v = imm(val)
        if v is not None:
            return [v]
        count = next((imm(j.op_str.split(",")[1].strip()) for j in ins[k + 1:k + 10]
                      if j.mnemonic == "cmp" and "+ 0x2a]," in j.op_str and imm(j.op_str.split(",")[1].strip()) is not None), None)
        if val == "dx" and k >= 4 and ins[k - 2].mnemonic == "idiv" and ins[k - 1].op_str.startswith("dx,"):
            n = imm(ins[k - 4].op_str.split(",")[1].strip()) if ins[k - 4].op_str.startswith("bx,") else None
            base = imm(ins[k - 1].op_str.split(",")[1].strip())
            if n and base is not None and n <= 8:
                return [base + j for j in range(n)]
        prev = ins[k - 1]
        if val == "ax" and prev.mnemonic == "mov" and "[bx + 0x" in prev.op_str:            # 표 프레임
            t = imm(prev.op_str.split("+")[1].strip(" ]"))
            n = (count or 0) + 1
            if t is not None and 0 < n <= 16:
                return [fs.w(t + 2 * j) for j in range(n)]
        if val == "ax" and prev.mnemonic == "add" and prev.op_str.startswith("ax,"):          # 계수 + k
            base = imm(prev.op_str.split(",")[1].strip())
            if base is not None:
                return [base + j for j in range(max(1, count or 1))]
        return []

    def reg_of(op):
        return op[len("word ptr ["):].split("+")[0].split("]")[0].strip()

    idx_regs = set()                      # [bp+6](자기 쪽 번호)를 담은 레지스터
    for k, i in enumerate(ins):
        s_, m = i.op_str, i.mnemonic
        if m == "mov" and s_.endswith(", word ptr [bp + 6]") and s_.split(",")[0] in ("si", "di", "bx"):
            idx_regs.add(s_.split(",")[0])
        if m == "mov" and s_ in ("ax, si", "ax, di", "ax, bx"):
            src = "own" if s_.split(", ")[1] in idx_regs else "other"
        if m == "mov" and s_.startswith("ax, word ptr ["):
            src = "own" if s_.endswith("[bp + 6]") else "fx" if ("[0x6118]" in s_ or "+ 0x2e]" in s_) else "other"
        elif m == "mov" and s_.endswith(", ax") and src and k and ins[k - 1].op_str == "ax, " + RECORD_BASE:
            role[s_.split(",")[0].replace("word ptr ", "").strip()] = src
        elif m == "mov" and s_.startswith("bx, word ptr [bp - "):
            role["bx"] = role.get(s_.split(",")[1].replace("word ptr ", "").strip(), "other")
        elif m == "mov" and s_.split(",")[0] in ("si", "di", "bx") and not s_.endswith(", ax"):
            role[s_.split(",")[0]] = "other"
        if "0x63aa" in s_:
            run = True
        if m == "lcall" and s_ == DAMAGE_CALL and hit_at is None:
            hit_at = len(frames)
        if m == "lcall" and s_ == SOUND_CALL and k and imm(ins[k - 1].op_str) is not None:
            at, snd = len(frames), imm(ins[k - 1].op_str)
            if not any(x["at"] == at for x in sounds):          # 지형별 발소리 분기(0x33/0x38)는 첫 것만
                sounds.append(dict(at=at, sound=snd))
        if m == "call":
            t = imm(s_)
            if t in SPELLS or t in SPELL_BY_ARG:
                spells_here.append((len(frames), t))
        if m == "mov" and s_.startswith("word ptr [") and "+ 8], " in s_:
            r = role.get(reg_of(s_), "other")
            vals = frame_values(k)
            if r == "own":
                frames.extend(vals)
            elif r == "fx" and vals:
                if fx_at is None:
                    fx_at = len(frames)
                fx.extend(v for v in vals if v not in fx)
        if m == "add" and s_.startswith("word ptr [") and "+ 4], " in s_ and role.get(reg_of(s_)) == "fx":
            fx_move = True
    if any(j.mnemonic == "call" and imm(j.op_str) == SPIRIT_DISPATCH for j in ins):
        return dict(frames=list(SPIRIT_BODY), hitIndex=2, run=False, sounds=[], spell="spirit",
                    effectFrames=[], effectAt=-1, effectMove=False)
    if not frames and depth < 2:          # 얇은 분기 함수: 부르는 연출 함수로 내려간다
        callees = [imm(j.op_str) for j in ins if j.mnemonic == "call" and imm(j.op_str) not in SPELLS and imm(j.op_str) not in SPELL_BY_ARG]
        for t in callees:
            if t is None:
                continue
            c = clip(fs, t, arg, depth + 1)
            if len(c["frames"]) > 1:
                return c
    spell = ""
    if spells_here:
        at, t = spells_here[min(arg or 0, len(spells_here) - 1)] if len(spells_here) > 1 else spells_here[0]
        if t in SPELL_BY_ARG:
            t = SPELL_BY_ARG[t][min(arg or 0, 2)]
        spell = SPELLS[t]["name"]
        hit_at = at                                              # 피해는 마법 루틴 안에서
    elif fx and hit_at is not None and fx_at is not None and fx_at > hit_at:
        fx_at = hit_at
    has_fx = bool(fx) and not spell
    return dict(frames=frames, hitIndex=hit_at if hit_at is not None else max(0, len(frames) - 1), run=run,
                sounds=sounds, spell=spell, effectFrames=fx if has_fx else [],
                effectAt=fx_at if has_fx else -1, effectMove=fx_move and has_fx)


def extract(path, frame_counts):
    fs = Fs(path)
    out = []
    for cls in range(42):
        rules = state_rules(fs, cls)
        f, handlers = state_handlers(fs, cls)
        clips = {}
        states = {r["state"] for r in rules}
        if -1 in states:
            states |= {6, 7}
        for st in sorted(states - {-1}):
            h = handlers.get(st)
            fn, arg = anim_function(fs, h) if h else (None, None)
            if fn is None:
                continue
            c = clip(fs, fn, arg)
            n = frame_counts.get(cls)
            if n:
                keep = [k for k, x in enumerate(c["frames"]) if 0 <= x < n]
                c["hitIndex"] = sum(1 for k in keep if k < c["hitIndex"])
                if c["effectAt"] >= 0:
                    c["effectAt"] = sum(1 for k in keep if k < c["effectAt"])
                c["effectFrames"] = [x for x in c["effectFrames"] if 0 <= x < n]
                for snd in c["sounds"]:
                    snd["at"] = sum(1 for k in keep if k < snd["at"])
                c["frames"] = [c["frames"][k] for k in keep] or [COMMON["stance"]]
                c["hitIndex"] = min(c["hitIndex"], len(c["frames"]) - 1)
            c["function"] = hex(fn)
            clips[str(st)] = c
        out.append(dict(cls=cls, function=hex(f), rules=rules,
                        clips=[dict(state=int(k), **v) for k, v in clips.items()]))
    return out


if __name__ == "__main__":
    import glob, os
    counts = {}
    for f in glob.glob(os.path.join(sys.argv[1], "CHAR_*.json")):
        c = int(os.path.basename(f)[5:7]); counts[c] = json.load(open(f, encoding="utf-8"))["frameCount"]
    res = extract(EXE, counts)
    for r in res:
        print(r["cls"], counts.get(r["cls"]), sorted({x["state"] for x in r["rules"]}))
        for c in r["clips"]:
            fxs = ("fx@%d %s%s" % (c["effectAt"], c["effectFrames"], " move" if c["effectMove"] else "")) if c["effectAt"] >= 0 else ""
            print("   ", c["state"], c["frames"], "hit", c["hitIndex"], "run" if c["run"] else "", c["spell"], fxs,
                  [(x["at"], x["sound"]) for x in c["sounds"]])

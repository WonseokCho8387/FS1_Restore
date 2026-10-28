"""FSMAIN.EXE에서 talk(이벤트, 유닛...) (0xE9C0) 호출부를 모아 표로 만든다.

인자 중 0x8A92 + k*0x24 는 유닛 배열 k번 슬롯. 호출부가 속한 함수가 [0x568](현재 스테이지 번호, 0부터)을
상수와 비교하면 그 값+1을 스테이지 후보로 적는다. 정적 분석이라 런타임 계산 인자는 식 그대로 남는다.
"""
import json, re, sys
import capstone

TALK = 0xE9C0
UNIT_BASE, UNIT_SIZE = 0x8A92, 0x24


def callers(code, target):
    out = []
    for m in re.finditer(rb"\x9a(..)(..)", code, re.S):
        if int.from_bytes(m.group(2), "little") * 16 + int.from_bytes(m.group(1), "little") == target:
            out.append(m.start())
    return out


def func_start(code, a):
    for s in range(a, max(a - 0x2000, 0), -1):
        if code[s] == 0xC8 and code[s - 1] in (0xCB, 0xC3) or code[s:s + 3] == b"\x55\x8b\xec" and code[s - 1] in (0xCB, 0xC3):
            return s
    return None


def describe(op):
    op = op.replace("word ptr ", "")
    try:
        v = int(op, 16)
    except ValueError:
        return op
    if UNIT_BASE <= v < UNIT_BASE + UNIT_SIZE * 40 and (v - UNIT_BASE) % UNIT_SIZE == 0:
        return f"unit[{(v - UNIT_BASE) // UNIT_SIZE}]"
    return hex(v)


def scan(exe_path):
    raw = open(exe_path, "rb").read()
    code = raw[0x3E00:0x3E00 + 0x297C0]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    rows = []
    for a in callers(code, TALK):
        fs = func_start(code, a) or a - 0x200
        ins = list(md.disasm(code[fs:a + 5], fs))
        pushes, stages = [], set()
        for i in ins:
            if i.address >= a:
                break
            if i.mnemonic == "push":
                pushes.append(i.op_str)
            elif i.mnemonic in ("call", "lcall") or (i.mnemonic == "add" and i.op_str.startswith("sp")):
                pushes = []
            m = re.match(r"word ptr \[0x568\], (0x[0-9a-f]+|\d+)", i.op_str)
            if i.mnemonic == "cmp" and m:
                stages.add(int(m.group(1), 0) + 1)
        args = [describe(p) for p in reversed(pushes)]
        ev = args[0] if args else "?"
        try:
            v = int(ev, 16)
            event = chr(v) if v < 0x100 else f"STAGE5{v >> 8}#{chr(v & 0xFF)}"
        except ValueError:
            event = ev
        rows.append(dict(call=hex(a), function=hex(fs), event=event, args=args[1:],
                         stageHints=sorted(stages)))
    return rows


if __name__ == "__main__":
    rows = scan(sys.argv[1])
    json.dump(rows, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(len(rows))

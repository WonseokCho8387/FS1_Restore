"""스테이지 훅(DS:0x1356, 스테이지당 7개) 안의 '조건 → 행동'을 읽기 쉽게 펼친다(분석 보조).

각 훅 함수를 처음부터 retf 까지 훑으며 다음을 시간 순서대로 찍는다.
  - 비교: cmp [bp-2],xxyy (도착 좌표 x*100+y), cmp [si/di+0x0A] (이름 id), +0x0C(클래스), +0x20(ai), +0x12(HP), 전역 플래그 [0x84Ex]
  - 행동: talk(이벤트, 인자…), 유닛 생성(템플릿), 플래그 설정, 지형 변경, 음악
분기 구조는 풀지 않으므로 사람이 읽고 stages.json 이벤트로 옮긴다.
사용: python stage_events.py [스테이지번호...]
"""
import struct, sys
import capstone
import talk_stage
import fs_battle_data as fbd

HOOKS = ["init", "turnStart", "unitMoved", "unitAction2", "enemyTurn", "battle", "afterBattle"]


def main(stages):
    fs = fbd.Fs("../extracted/iso/FSMAIN.EXE")
    chars, classes = fbd.names(fs)
    tbl = talk_stage.hook_table(fs.img)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    for st in stages:
        for h in range(7):
            f = tbl[(st - 1) * 7 + h]
            ins = []
            for i in md.disasm(fs.code[f:f + 0x1400], f):
                ins.append(i)
                if i.mnemonic == "retf":
                    break
            out = []
            for k, i in enumerate(ins):
                s = i.op_str
                if i.mnemonic == "cmp":
                    v = s.split(",")[-1].strip()
                    try:
                        n = int(v, 16) if v.startswith("0x") else int(v)
                    except ValueError:
                        continue
                    if "[bp - 2]" in s and n > 100:
                        out.append("@(%d,%d)" % (n // 100, n % 100))
                    elif "+ 0xa]" in s:
                        out.append("name=%s" % chars.get(n, n))
                    elif "+ 0xc]" in s:
                        out.append("cls=%s" % (classes[n] if n < 42 else n))
                    elif "+ 0x20]" in s:
                        out.append("ai=%d" % n)
                    elif "+ 0x12]" in s:
                        out.append("hp=%d" % n)
                    elif "+ 2]" in s and n < 0x40:
                        out.append("slot=%d" % n)
                    elif "[0x84e" in s:
                        out.append("%s=%d" % (s.split(",")[0].replace("word ptr ", ""), n))
                elif i.mnemonic == "mov" and s.startswith("word ptr [0x84e") and "," in s:
                    out.append("SET %s" % s.replace("word ptr ", ""))
                elif i.mnemonic == "lcall" and s == "0xe57, 0x450":
                    ev = next((j.op_str for j in reversed(ins[max(0, k - 12):k]) if j.mnemonic == "push" and
                               j.op_str.startswith("0x") and int(j.op_str, 16) < 0x80), "?")
                    out.append("TALK '%s'" % (chr(int(ev, 16)) if ev != "?" else "?"))
                elif i.mnemonic == "lcall" and s == "0xe57, 0x1e7":
                    out.append("HAS_SLOT(%s)" % next((j.op_str for j in reversed(ins[k - 3:k]) if j.mnemonic == "push"), "?"))
                elif i.mnemonic == "lcall" and s == "0x928, 0x8a6":
                    t = next((int(j.op_str, 16) for j in ins[k - 6:k] if j.mnemonic == "push" and j.op_str.startswith("0x") and int(j.op_str, 16) > 0x2000), None)
                    if t:
                        out.append("CREATE %s/%s" % (chars.get(fs.w(t), "?"), classes[fs.w(t + 2)] if fs.w(t + 2) < 42 else fs.w(t + 2)))
                elif i.mnemonic == "lcall" and s == "0xe57, 0x272":
                    out.append("MUSIC")
                elif i.mnemonic == "lcall" and s == "0x9ec, 0x1409":
                    out.append("RETREAT")
                elif i.mnemonic == "mov" and "es:[bx + 0x" in s and s.startswith("byte ptr"):
                    off = int(s.split("+ ")[1].split("]")[0], 16) - 0x30
                    val = s.split(",")[1].strip()
                    loop = any(j.mnemonic == "add" and j.op_str.startswith("bx,") for j in ins[max(0, k - 3):k])
                    out.append("TILE(%d,%d)=%s%s" % (off % 80, off // 80, val, "+i" if loop else ""))
                elif i.mnemonic == "mov" and "+ 0x20]," in s and s.startswith("word ptr ["):
                    out.append("AI:=" + s.split(",")[1].strip())
                elif i.mnemonic == "mov" and "+ 0xc]," in s and s.startswith("word ptr ["):
                    out.append("CLS:=" + s.split(",")[1].strip())
            if out:
                print("stage %d %-12s %s" % (st, HOOKS[h], " ".join(out)))


if __name__ == "__main__":
    main([int(a) for a in sys.argv[1:]] or list(range(1, 15)))

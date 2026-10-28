"""talk() 호출부에 스테이지·훅 번호를 붙인다.

FSMAIN DS:0x1356 에는 스테이지마다 far 함수 포인터 7개(훅)가 14스테이지분 있다.
각 훅에서 직접 호출로 닿는 함수(깊이 4)에 그 스테이지를 표시하고, talk() 호출부가 속한 함수에 매긴다.
"""
import collections, json, struct, sys
import capstone

HOOK_TABLE = 0x1356
HOOKS_PER_STAGE = 7


def hook_table(img):
    tbl, a = [], 0x297C0 + HOOK_TABLE
    while True:
        off, seg = struct.unpack_from("<HH", img, a)
        if seg * 16 + off == 0:
            return tbl
        tbl.append(seg * 16 + off); a += 4


def annotate(exe_path, rows):
    raw = open(exe_path, "rb").read(); img = raw[0x3E00:]; code = img[:0x297C0]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)

    def callees(f):
        out = set()
        for i in md.disasm(code[f:f + 0x1500], f):
            if i.mnemonic in ("retf", "ret"):
                break
            try:
                if i.mnemonic == "lcall":
                    s, o = [int(x, 16) for x in i.op_str.split(",")]; out.add(s * 16 + o)
                elif i.mnemonic == "call":
                    out.add(int(i.op_str, 16))
            except ValueError:
                pass
        return out

    tbl = hook_table(img)
    reach = collections.defaultdict(set)
    for idx, h in enumerate(tbl):
        st, k = idx // HOOKS_PER_STAGE + 1, idx % HOOKS_PER_STAGE
        seen, frontier = {h}, [h]
        for _ in range(4):
            nxt = []
            for f in frontier:
                for c in callees(f):
                    if c >= 0x5000 and c not in seen:      # 공용 라이브러리 영역은 따라가지 않음
                        seen.add(c); nxt.append(c)
            frontier = nxt
        for f in seen:
            reach[f].add((st, k))
    for x in rows:
        hits = reach.get(int(x["function"], 16), set())
        x["stage"] = sorted({s for s, _ in hits})
        x["hook"] = sorted({k for _, k in hits})
    return rows, tbl


if __name__ == "__main__":
    rows = json.load(open(sys.argv[2], encoding="utf-8"))
    rows, tbl = annotate(sys.argv[1], rows)
    json.dump(rows, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(collections.Counter(len(x["stage"]) for x in rows))

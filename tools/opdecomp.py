"""OP.EXE 오프닝 구간을 '호출 + 인자' 의사코드로 펼쳐 보는 분석 보조 도구."""
import capstone, sys, collections
r = open('../extracted/iso/OP.EXE', 'rb').read()
img = r[0x1400:]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
NAMES = {}
for kv in sys.argv[3:]:
    k, v = kv.split('='); NAMES[int(k, 16)] = v

def run(a, b):
    stack = []
    for i in md.disasm(img[a:b], a):
        m, o = i.mnemonic, i.op_str
        if m == 'push':
            stack.append(o.replace('word ptr ', ''))
        elif m in ('lcall', 'call'):
            if m == 'lcall':
                seg, off = [int(x, 16) for x in o.split(',')]
                t = seg * 16 + off
            else:
                t = int(o, 16)
            args = list(reversed(stack[-6:]))
            print(f"{i.address:05x}  {NAMES.get(t, hex(t))}({', '.join(args)})")
            stack = []
        elif m in ('add',) and o.startswith('sp'):
            stack = []
        elif m.startswith('j') or m in ('ret', 'retf', 'leave', 'enter', 'loop'):
            print(f"{i.address:05x}    {m} {o}")
            stack = []
        elif m in ('mov', 'inc', 'dec', 'xor', 'cmp', 'imul', 'or', 'sub', 'test') and not o.startswith('cx') and 'bp - ' not in o[:12] or m == 'cmp':
            print(f"{i.address:05x}    {m} {o}")
        elif m == 'pop':
            pass

if __name__ == '__main__':
    run(int(sys.argv[1], 16), int(sys.argv[2], 16))

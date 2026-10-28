"""분석 보조: FSMAIN 코드 주소 범위를 디스어셈블(talk/생성 호출 표시)"""
import sys, capstone
import fs_battle_data as fbd
fs = fbd.Fs("../extracted/iso/FSMAIN.EXE")
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
NAMES = {"0xe57, 0x450": "TALK", "0x928, 0x8a6": "CREATE", "0xe57, 0x272": "MUSIC", "0x9ec, 0x1409": "RETREAT", "0xe57, 0x1e7": "HAS_SLOT"}
a, b = int(sys.argv[1], 16), int(sys.argv[2], 16)
for i in md.disasm(fs.code[a:b], a):
    note = NAMES.get(i.op_str, "") if i.mnemonic == "lcall" else ""
    print("%05x  %-6s %s %s" % (i.address, i.mnemonic, i.op_str, ("   ; " + note) if note else ""))

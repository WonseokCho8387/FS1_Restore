import capstone,sys
import os
_e=os.environ.get('EXE','FSMAIN.EXE'); _r=open('../extracted/iso/'+_e,'rb').read(); d=_r[int.from_bytes(_r[8:10],'little')*16:]
md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_16)
a=int(sys.argv[1],16); n=int(sys.argv[2],16)
for i in md.disasm(d[a:a+n],a):
    print(f"{i.address:06x}  {i.bytes.hex():14s} {i.mnemonic} {i.op_str}")

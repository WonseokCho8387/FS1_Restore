"""분석 보조: 대사 블록 내용 보기. python showblk.py 2 3 [블록...]"""
import json, sys
st = int(sys.argv[1])
d = json.load(open("../UnityAssets/Dialogue/STAGE%02d.json" % st, encoding="utf-8"))
blocks = d["blocks"] if "blocks" in d else [dict(id=k, **v) if isinstance(v, dict) else dict(id=k, cmds=v) for k, v in d.items()]
for b in blocks:
    if b["id"] in sys.argv[2:]:
        print("==", b["id"])
        print(json.dumps({k: v for k, v in b.items() if k != "id"}, ensure_ascii=False)[:1400])

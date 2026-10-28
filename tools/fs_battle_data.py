"""FSMAIN.EXE 전투 데이터 추출: 클래스·캐릭터 이름, 아이템 표, 스테이지별 유닛 배치.

유닛 구조체(0x24바이트, 배열 DS:0x8A92, 62칸. 0~17 아군 쪽, 18~ 적):
  +00 상태  +02 슬롯+1 (아군은 얼굴 번호+1)  +04 x  +06 y  +08 행동 상태
  +0A~+23 = 템플릿 26바이트 그대로:
    이름ID, 클래스, 레벨, 최대HP, HP, 경험치(적은 처치 보상), 공격력, 방어력, 지식력, 마법방어, 민첩성, AI/기타, 장비 아이템
    (지식력·마법방어·민첩 순서는 공격 계산 0x17E01과 크리티컬 판정 0x2885E에서 확인)
배치 로더 0xA0B9(템플릿 배열, 좌표 목록): 좌표 목록은 세 구간이며 각 구간은 x<0 로 끝난다.
  1) 기존 아군 0,1,2…번 슬롯의 시작 위치
  2) (x,y)는 현재 템플릿으로 유닛 생성, (0,0)이면 다음 템플릿으로 넘어감
  3) 각 (x,y)마다 다음 템플릿 하나씩
  유닛 생성 0x9B26: 템플릿 클래스 < 18 이면 0번 슬롯부터, 아니면 18번부터 빈 칸에 넣는다.
"""
import json, re, struct, sys
import capstone

LOADER = 0xA0B9
WAVES = {3: [0x32DE], 7: [0x3B90, 0x3BB4], 10: [0x43DE], 11: [0x46F6], 12: [0x4A04, 0x4A28]}   # 대본 증원 목록 주소
HOOK_TABLE = 0x1356
FIELDS = ["nameId", "class", "level", "maxHp", "hp", "exp", "attack", "defense", "knowledge", "magicDefense",
          "agility", "ai", "item"]
TERRAINS = ["통행불가(산·벽·건물)", "평지", "길·다리", "숲", "바위·황무지", "마을", "물가", "물", "성 바닥", "성문·계단"]
STATS = ["maxHp", "attack", "defense", "knowledge", "magicDefense", "agility"]


class Fs:
    def __init__(self, path):
        raw = open(path, "rb").read()
        self.img = raw[0x3E00:]
        self.code = self.img[:0x297C0]
        self.ds = self.img[0x297C0:]

    def w(self, a):
        return struct.unpack_from("<h", self.ds, a)[0]

    def s(self, p):
        return self.ds[p:self.ds.index(b"\0", p)].decode("cp949", "replace").strip()


def names(fs):
    chars = {i: fs.s(struct.unpack_from("<H", fs.ds, 0x2CEE + 2 * i)[0]) for i in range(1, 40)}
    classes = [fs.s(p) for p in struct.unpack_from("<42H", fs.ds, 0x4BC)]
    return chars, classes


def classes_table(fs, class_names):
    """클래스별 이동 비용(DS:0x0E72, 지형 10종, 99=불가) + 성장 확률(DS:0x651A, 플레이어 클래스 0~17)."""
    out = []
    for c, name in enumerate(class_names):
        e = dict(id=c, name=name, moveCost=list(struct.unpack_from("<10h", fs.ds, 0x0E72 + c * 20)),
                 female=(c // 3 * 3) in (6, 9, 15))
        # 성장 확률(%) 순서 = STATS. 적 전용 클래스(18~)는 없음
        e["growth"] = list(struct.unpack_from("<6h", fs.ds, 0x651A + c * 12)) if c < 18 else []
        out.append(e)
    return out


def terrain_table(fs):
    defense = struct.unpack_from("<10h", fs.ds, 0x04A8)         # 피해 배율(%) — 지형 0은 전투에서 1로 취급
    return [dict(id=t, name=TERRAINS[t], damagePercent=defense[t]) for t in range(10)],         dict(MAPCHIP=list(struct.unpack_from("<120h", fs.ds, 0x1156)),
             MAPCHIP2=list(struct.unpack_from("<120h", fs.ds, 0x1246)))


ITEM_FLAGS = {0x1: "direct", 0x2: "indirect", 0x4: "special", 0x8: "heal", 0x10: "innate", 0x20: "spirit",
              0x40: "classChange", 0x80: "revive"}


def items_table(fs, class_names):
    """아이템 레코드 16바이트 @DS:0x230B: 이름, 가격, 물리(직접,간접), 마력(직접,간접), 플래그, 장비 마스크."""
    out = []
    for i in range(1, 80):
        name_p, price, pd, pi, md, mi, flags, mask = struct.unpack_from("<HhhhhhHH", fs.ds, 0x230B + i * 16)
        if mask == 0x8000:
            equip = ["*"]
        else:
            base = 0 if mask & 0x4000 else 9
            equip = [class_names[base + b] for b in range(9) if mask >> b & 1 and base + b < len(class_names)]
        out.append(dict(id=i, name=fs.s(name_p), price=price, physical=[pd, pi], magic=[md, mi], flags=flags,
                        flagNames=[n for b, n in ITEM_FLAGS.items() if flags & b],
                        weakenStat={0x100: "defense", 0x200: "knowledge", 0x300: "magicDefense",
                                    0x400: "attack"}.get(flags & 0x700, "") if flags & 0x20 else "",
                        equipMask=mask, equipClasses=equip))
    return out


SHOP = 0xC86C        # 마을 방문(대화+상점): (유닛, 마을 좌표[x*100+y,…,-1], 마을별 대화 이벤트, 마을별 4칸 판매 목록)


def hook_table(fs):
    tbl, a = [], 0x297C0 + HOOK_TABLE
    while struct.unpack_from("<I", fs.img, a)[0]:
        off, seg = struct.unpack_from("<HH", fs.img, a); tbl.append(seg * 16 + off); a += 4
    return tbl


def shops(fs, item_names):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    tbl = hook_table(fs)
    out = []
    for m in re.finditer(rb"\x9a(..)(..)", fs.code, re.S):
        if int.from_bytes(m.group(2), "little") * 16 + int.from_bytes(m.group(1), "little") != SHOP:
            continue
        c = m.start()
        imm = [int(i.op_str, 16) for i in md.disasm(fs.code[c - 30:c], c - 30)
               if i.mnemonic == "push" and i.op_str.startswith("0x") and int(i.op_str, 16) > 0x100][-3:]
        items_p, events_p, pos_p = imm            # push 순서: 판매목록, 이벤트, 좌표
        fn = max((h for h in tbl if h <= c), default=None)
        stage = tbl.index(fn) // 7 + 1 if fn is not None and c - fn < 0x400 else None
        villages, k = [], 0
        while fs.w(pos_p + 2 * k) >= 0:
            v = fs.w(pos_p + 2 * k)
            ids = [fs.w(items_p + 2 * (k * 4 + j)) for j in range(4)]
            ids = ids[:ids.index(0)] if 0 in ids else ids
            villages.append(dict(x=v // 100, y=v % 100, event=chr(fs.w(events_p + 2 * k)),
                                 items=[dict(id=i, name=item_names[i - 1]) for i in ids if 0 < i <= len(item_names)]))
            k += 1
        out.append(dict(stage=str(stage), call=hex(c), villages=villages))
    return out


CHEST_LOADER = 0xB4A8     # 0xB4A8(목록): 10바이트 (상태, x, y, 금, 아이템) × n, x<0 끝 → DS:0x841C (20칸)


def chests(fs, item_names):
    """스테이지 준비 훅이 넘기는 보물 상자 목록. 상태 1 = 숨김(옆에 오면 발견), 2 = 처음부터 보임, 3 = 열림(0xB51E)"""
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    tbl = hook_table(fs)
    out = []
    for m in re.finditer(rb"\x9a(..)(..)", fs.code, re.S):
        if int.from_bytes(m.group(2), "little") * 16 + int.from_bytes(m.group(1), "little") != CHEST_LOADER:
            continue
        c = m.start()
        imm = [int(i.op_str, 16) for i in md.disasm(fs.code[c - 6:c], c - 6) if i.mnemonic == "push" and i.op_str.startswith("0x")]
        fn = max((h for h in tbl if h <= c), default=None)
        stage = tbl.index(fn) // 7 + 1 if fn is not None and c - fn < 0x400 else None
        a, lst = imm[-1], []
        while fs.w(a + 2) >= 0:
            st, x, y, gold, item = [fs.w(a + 2 * k) for k in range(5)]
            lst.append(dict(state=st, x=x, y=y, gold=gold, item=item, itemName=item_names[item - 1] if 0 < item <= len(item_names) else ""))
            a += 10
        out.append(dict(stage=str(stage), chests=lst))
    return out


UNIT_CREATE = 0x928 * 16 + 0x8A6   # 0x9B26: 템플릿으로 유닛 생성(빈 슬롯; 클래스 < 18 이면 아군 0번부터)


def recruits(fs, chars, classes):
    """스테이지 대본(훅)이 아군 템플릿으로 유닛을 만드는 곳 = 동료 합류. 합류 순서가 곧 아군 슬롯(=얼굴 번호)"""
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    md.skipdata = True
    tbl = hook_table(fs)
    starts = sorted((a, i // 7 + 1, i % 7) for i, a in enumerate(tbl))
    out = {}
    L = list(md.disasm(fs.code[0x10F00:0x15C00], 0x10F00))
    for k, i in enumerate(L):
        if i.mnemonic != "lcall" or i.op_str != "0x928, 0x8a6":
            continue
        own = max((st for st in starts if st[0] <= i.address), default=None)
        imms = [int(j.op_str, 16) for j in L[k - 6:k] if j.mnemonic == "push" and j.op_str.startswith("0x") and int(j.op_str, 16) > 0x2000]
        if not imms or own is None:
            continue
        t = imms[0]
        tp = dict(zip(FIELDS, struct.unpack_from("<13h", fs.ds, t)))
        if tp["class"] >= 18:
            continue
        tp.update(cls=tp["class"], template=hex(t), name=chars.get(tp["nameId"], "?"), className=classes[tp["class"]],
                  hook=own[2], call=hex(i.address))
        out.setdefault(str(own[1]), []).append(tp)
    return [dict(stage=k, units=v) for k, v in sorted(out.items(), key=lambda kv: int(kv[0]))]


ITEM_EFFECTS = [  # 0xC44B 특수 아이템(플래그 4) / 0xC641 회복(플래그 8) / 0xC70F 전직(플래그 0x40)
    dict(item=37, effect="none", note="드카티의 열쇠: 사용 불가(이벤트용)"),
    dict(item=38, effect="sleepFullHeal", note="사랑의 꿈: 상태값 0이면 잠들며 HP 완전 회복(상태 1), 아니면 효과 없음"),
    dict(item=39, effect="loseGold", note="빨간 날개: 금 500~1000(100 단위 무작위) 감소, '좋은 일을 한 것 같다'"),
    dict(item=49, effect="magicDefense+5"), dict(item=50, effect="agility+5"), dict(item=51, effect="knowledge+5"),
    dict(item=56, effect="attack+5"), dict(item=57, effect="defense+5"), dict(item=68, effect="maxHp+10"),
    dict(item=0, effect="heal", note="회복량 = 가격/2. 예외: 에릭사(23)·환약(65)=999(완전), 위스키(62)=20. 최대HP 상한"),
]
CLASS_CHANGE = [  # 레벨 10 이상에서 장비하면 클래스+1, 레벨 1, 경험치 0, 보너스(0xC761), 각 능력치 상한 45
    dict(item=44, fromClass=1, toClass=2, bonus=dict(attack=8, defense=8)),
    dict(item=43, fromClass=4, toClass=5, bonus=dict(knowledge=8, magicDefense=8)),
    dict(item=45, fromClass=7, toClass=8, bonus=dict(knowledge=4, magicDefense=4)),
    dict(item=46, fromClass=10, toClass=11, bonus=dict(attack=8, defense=8, knowledge=8, magicDefense=8)),
    dict(item=47, fromClass=13, toClass=14, bonus=dict(attack=8, defense=8)),
    dict(item=48, fromClass=16, toClass=17, bonus=dict(knowledge=8, magicDefense=8)),
]
# 2연타 공격 연출(0x19425·0x1C06F): 피해 계산(0x17E01)과 적용을 두 번 한다(크리티컬도 따로).
# 결과 메시지 숫자는 '첫 피해 + 두 번째 피해/2'로 표시되는데, 공격자 클래스가 0·3·5일 때만 (0x17C22). HP는 두 번 모두 깎임.
TWO_HIT = [
    dict(classes=[0], item=0x19, note="로드 + 성검 (연출 상태 8)"),
    dict(classes=[3, 5, 33], item=0x23, note="위저드·소사라(적 소사라 포함) + 버닝 롯 (연출 상태 9). 적 소사라는 메시지에 마지막 피해만 표시"),
]

AI_MODES = [  # 유닛 +0x20 (템플릿 ai), 적 턴 0xB313 → 스테이지 훅 4 → 0xAA98. 구현: Scripts/FarlandAI.cs
    dict(ai=1, behavior="hold", note="이동 없이 제자리 공격(0xA39E). 보스형. 대본이 첫 교전 때 대사와 함께 2로 바꿈"),
    dict(ai=2, behavior="hold", note="ai 1과 같은 동작(대사를 이미 한 상태)"),
    dict(ai=3, behavior="hold", note="이동 없이 제자리 공격"),
    dict(ai=4, behavior="flee", note="보이는 아군에게서 가장 먼 도달 칸으로. 공격 가능한 대상이 있으면 공격이 우선"),
    dict(ai=5, behavior="dormant", note="적 턴 루프(0xB313)가 건너뜀. 대본이 7로 바꿔 깨움(5·11스테이지)"),
    dict(ai=6, behavior="summoner", note="제자리 공격, 대상이 없으면 소환(0x9F8F): 3회 × 25%, 클래스별 표 summonTable에서 6칸 중 하나"),
    dict(ai=9, behavior="chaseHero", note="아크를 경유지로 삼아 접근"),
    dict(ai=15, behavior="summoner", note="ai 6과 같으나 20칸 안에 아군이 있을 때만 소환(0x9F4C)"),
    dict(ai=16, behavior="ambush", note="아군이 5칸 안에 들어오면 ai 7로 바뀜. 8·11스테이지는 훅이 AI를 부르지 않아 대본 트리거로만 깨어남"),
    dict(ai=17, behavior="chaseNearestAlly", note="20칸 안의 첫 아군을 경유지로"),
    dict(ai=18, behavior="summoner", note="ai 15와 같음"),
    dict(ai=-1, behavior="advance", note="그 외(7, 8, 10, 11 …): 대본 경유지로 접근, 공격 가능한 대상이 있으면 공격"),
]

# 음악 번호 n = CD 트랙 n+1 (Audio/BGM/track{n+1:02d}.wav). 함수 0xE7E2(n), 현재 곡 DS:0x1544
MUSIC = dict(
    stageBgmAddr="DS:0x1516",
    events=[
        dict(event="stageStart", note="stageBgm[스테이지-1]"),
        dict(event="bossEngaged", music=7, stages=[1, 3, 4, 5, 6, 7, 8, 10],
             note="훅 5: 보스가 처음 공격받아 ai 1→2 가 될 때(이미 그 곡이면 생략)"),
        dict(event="bossEngaged", music=6, stages=[2, 7, 9, 11, 12, 13],
             note="같은 조건의 다른 보스 곡. 7스테이지는 삿큐버스 강화 때 6, 9·11은 대본 이벤트(훅 2)에서도 6"),
        dict(event="bossDefeated", music=16, note="훅 6: ai 2 보스의 HP가 0 → 0xB2C9(ai 1·2 외 적 전원 철수) 후 16. 1스테이지는 16 대신 스테이지 곡으로 돌아감"),
        dict(event="stage13Phase2", music=17, note="13스테이지 2차 배치(13-b) 시작"),
        dict(event="ending", music=12, note="에필로그 뒤 엔딩"),
    ],
)

# 스테이지 훅 4(적 유닛마다 호출)와 관련 훅의 행군·전환 규칙. 좌표는 원작 (x, y). 구현: Scripts/FarlandStageAI.cs
STAGE_AI = [
    dict(stage=1, rules=[]),
    dict(stage=2, rules=[
        dict(kind="skip", cls=60, note="구출 NPC(클래스 60)는 행동하지 않음"),
        dict(kind="march", ai=8, waypoint=[25, 13], requireTile=[34, 20, 0x6C],
             switchTo=9, when="x < 32", note="다리가 놓인 뒤(34,20 칩 0x6C) 경유지로, x<32가 되면 아크 추적"),
        dict(kind="tileChange", hook="turnStart", trigger="4번째 동료(고달) 합류",
             tiles=[[33, 20, 0x0C], [34, 20, 0x6C]], note="물가·물 → 다리(지형 2)"),
    ]),
    dict(stage=3, rules=[
        dict(kind="march", ai=8, waypoint=[13, 18], switchTo=11, when="x < 22"),
        dict(kind="march", ai=11, waypoint=[3, 26], switchTo=9, when="dist(3,26) < 4"),
    ]),
    dict(stage=4, rules=[
        dict(kind="note", note="ai 16(매복) 유닛이 행동할 때 플래그를 켜서, 교전 시 한 번 대사 0x35"),
    ]),
    dict(stage=5, rules=[
        dict(kind="march", ai=8, waypoint=[14, 25], switchTo=17, when="x >= 12"),
        dict(kind="march", ai=-1, waypoint=[16, 9], altWaypoint=[26, 3], requireTile=[26, 3, 2],
             note="통로가 열린 뒤(26,3 칩 2) 나머지는 y>12면 (16,9), 아니면 (26,3)으로. 열리기 전엔 경유지 없음"),
        dict(kind="tileChange", hook="unitMoved", trigger="유닛이 (29,3)에 도착",
             tiles=[[26, 3, 2], [27, 3, 2], [28, 3, 2]], wake=dict(fromAi=5, toAi=7, exceptClass=23),
             note="통로 개방, 휴면(ai 5) 적 중 스켈톤 외 전부 깨어남"),
    ]),
    dict(stage=7, rules=[
        dict(kind="wake", hook="battleEvent", cls=25, fromAi=15, toAi=2, note="삿큐버스 첫 교전 시 대사 0x32"),
        dict(kind="buff", cls=25, set=dict(defense=23, magicDefense=32, ai=15), note="이벤트 후 삿큐버스 강화"),
    ]),
    dict(stage=8, rules=[
        dict(kind="skip", ai=16, note="매복 유닛은 AI를 부르지 않음"),
        dict(kind="wake", hook="unitMoved→turnStart", trigger="아군이 ai 16 적의 4칸 안에 들어감",
             fromAi=16, toAi=9, note="다음 아군 턴 시작에 대사 0x32 후 모두 아크 추적"),
    ]),
    dict(stage=9, rules=[
        dict(kind="reinforce", cls=38, actions=4, note="흑기사가 공격받지 않고 4번 행동하면(두 번 행동이라 2턴) 대사 0x42 후 reinforcements 의 빈 칸에 증원. 흑기사가 공격받으면(훅 5) 횟수 초기화"),
    ]),
    dict(stage=11, rules=[
        dict(kind="march", ai=8, waypoint=[11, 28], switchTo=9, when="dist < 3"),
        dict(kind="march", ai=10, waypoint=[8, 40], switchTo=7, when="dist < 3"),
        dict(kind="march", ai=11, waypoint=[35, 40], switchTo=7, when="dist < 3"),
        dict(kind="skip", ai=16, note="매복 유닛은 AI를 부르지 않음"),
        dict(kind="cutscene", hook="unitMoved", trigger="스위치 (8,38)·(35,38) 두 곳에 아군이 모두 섬(한 명이 도착할 때 반대쪽 확인)",
             tiles=[[6, 39, 0x6F], [7, 39, 0x70], [8, 39, 0x70], [9, 39, 0x70], [10, 39, 0x70],
                    [33, 39, 0x6F], [34, 39, 0x70], [35, 39, 0x70], [36, 39, 0x70], [37, 39, 0x70]],
             wake=dict(fromAi=[16, 5], toAi=7),
             trap=dict(unit="반대쪽 스위치의 아군", moveTo=[22, 47], attacker="첫 고렘(클래스 26), 이번 한 번 공격력 999"),
             corridor=[[14, 39, 0x4A], [13, 40, 0x70], [14, 40, 0x70], [14, 41, 0x4A], [13, 42, 0x70], [14, 42, 0x70],
                       [14, 43, 0x4A], [13, 44, 0x70], [14, 44, 0x70], [30, 39, 0x6E], [29, 40, 0x70], [30, 40, 0x70],
                       [30, 41, 0x6E], [29, 42, 0x70], [30, 42, 0x70], [30, 43, 0x6E], [29, 44, 0x70], [30, 44, 0x70]],
             note="대사 0x41 → 39행 벽이 열림(0x42·0x43) → 매복·휴면 적 기동 → (34,40) 적 대사 0x44 → 함정: 반대쪽 아군이 "
                  "(22,47)로 떨어짐(0x45~0x47) → 고렘 공격(0x4F) → 통로 타일 변경(corridor)"),
    ]),
    dict(stage=13, rules=[
        dict(kind="switches", note="적 하나가 행동할 때마다 switches 갱신(0x14FA7): 발판(칩 0x48) 위에 유닛이 있으면 "
                                   "연결 칸을 칩 0x24(통행불가)로, 비면 칩 0x6E(성 바닥)로"),
    ]),
]


def stage_ai(fs, chars, classes):
    cls_name = lambda c: classes[c] if 0 <= c < len(classes) else "NPC(특수)"
    # 0x9F8F: DS:0x1C86 + 클래스×12, 6칸(0=실패). 18 미만 클래스 자리는 다른 데이터와 겹쳐 쓰이지 않음
    table = []
    for c in range(18, 41):
        v = list(struct.unpack_from("<6h", fs.ds, 0x1C86 + c * 12))
        if any(v):
            table.append(dict(summoner=c, summonerName=cls_name(c), summons=v,
                              summonNames=[cls_name(k) if k else "" for k in v]))
    # 소환된 임시 유닛(슬롯 0)을 훅 4가 받아, 스테이지 템플릿 목록에서 같은 클래스의 첫 항목으로 생성(0x8A6), 행동 완료 상태
    tpl_tables = {4: 0x3474, 5: 0x3656, 6: 0x3858, 7: 0x3A3E, 8: 0x3D64, 9: 0x40BA, 10: 0x42DA,
                  11: 0x45BE, 12: 0x48E6, 13: 0x4CB0, 14: 0x4EFA}

    def tpl(a):
        t = dict(zip(FIELDS, struct.unpack_from("<13h", fs.ds, a)))
        # cls: Unity JsonUtility용(C#에서 class는 예약어라 FarlandRules.Stats.cls)
        return dict(template=hex(a), name=chars.get(t["nameId"], "?"), className=cls_name(t["class"]), cls=t["class"], **t)

    out = []
    for r in STAGE_AI + [dict(stage=s, rules=[]) for s in (6, 10, 12, 14)]:
        e = dict(r, stage=r["stage"], summonTemplates=[])
        a = tpl_tables.get(r["stage"])
        while a and fs.w(a):
            e["summonTemplates"].append(tpl(a)); a += 26
        out.append(e)
    out.sort(key=lambda e: e["stage"])
    rein, a = [], 0x41A4                                   # 0x9ECC(목록): (x, y, 템플릿) — 빈 칸에만 생성
    while fs.w(a + 4):
        rein.append(dict(x=fs.w(a), y=fs.w(a + 2), **tpl(fs.w(a + 4) & 0xFFFF))); a += 6
    next(e for e in out if e["stage"] == 9)["reinforcements"] = rein
    # 대본 증원 목록(0x9ECC 인자): 스테이지 훅이 턴·도착 조건에 따라 부른다. id = 목록 주소
    for st, addrs in WAVES.items():
        e = next(e for e in out if e["stage"] == st)
        e["waves"] = []
        for w in addrs:
            sp, a = [], w
            while fs.w(a + 4):
                sp.append(dict(x=fs.w(a), y=fs.w(a + 2), **tpl(fs.w(a + 4) & 0xFFFF))); a += 6
            e["waves"].append(dict(id=hex(w), spawns=sp))
    # 9스테이지 문 앞 연출: 템플릿표 0x3E1A 의 클래스 36 유닛이 나타나 클래스 39(0x4A5A 표의 이름)로 정체를 드러낸다
    e9 = next(e for e in out if e["stage"] == 9)
    a = 0x3E1A
    while fs.w(a + 2) != 0x24: a += 26
    b = 0x4A5A
    while fs.w(b + 2) != 0x27: b += 26
    e9["templates"] = [dict(tpl(a), id="disguise", revealClass=0x27, revealName=fs.w(b)), dict(tpl(0x3DE6), id="blackKnight")]
    sw, a = [], 0x6016                                     # (발판 x, y, 연결 칸 x, y), x<0 끝
    while fs.w(a) >= 0:
        sw.append(dict(plateX=fs.w(a), plateY=fs.w(a + 2), x=fs.w(a + 4), y=fs.w(a + 6))); a += 8
    next(e for e in out if e["stage"] == 13)["switches"] = sw
    return table, out


def placements(fs, templates, positions):
    out, a, t = [], positions, templates

    def tpl(addr):
        return dict(zip(FIELDS, struct.unpack_from("<13h", fs.ds, addr)))

    party = []
    while fs.w(a) >= 0:
        party.append(dict(slot=len(party), x=fs.w(a), y=fs.w(a + 2))); a += 4
    a += 4
    while fs.w(a) >= 0:
        if fs.w(a) == 0:
            t += 26
        else:
            out.append(dict(x=fs.w(a), y=fs.w(a + 2), template=hex(t), **tpl(t)))
        a += 4
    a += 4
    while True:
        t += 26
        if fs.w(a) < 0:
            break
        out.append(dict(x=fs.w(a), y=fs.w(a + 2), template=hex(t), **tpl(t)))
        a += 4
    return party, out


def stage_loads(fs):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    tbl, a = [], 0x297C0 + HOOK_TABLE
    while struct.unpack_from("<I", fs.img, a)[0]:
        off, seg = struct.unpack_from("<HH", fs.img, a); tbl.append(seg * 16 + off); a += 4
    init_hooks = {tbl[k * 7]: k + 1 for k in range(len(tbl) // 7)}
    loads = []
    for m in re.finditer(rb"\x9a(..)(..)", fs.code, re.S):
        if int.from_bytes(m.group(2), "little") * 16 + int.from_bytes(m.group(1), "little") != LOADER:
            continue
        c = m.start()
        pushes = [i.op_str for i in md.disasm(fs.code[c - 12:c], c - 12) if i.mnemonic == "push"]
        imm = [int(p, 16) for p in pushes if p.startswith("0x")][-2:]
        fn = max((h for h in init_hooks if h <= c), default=None)
        stage = init_hooks.get(fn) if fn is not None and c - fn < 0x80 else None
        loads.append(dict(call=hex(c), stage=stage, positions=imm[0], templates=imm[1]))
    return loads


def export(path, out_path):
    fs = Fs(path)
    chars, classes = names(fs)
    loads = stage_loads(fs)
    special = {"0x10ce6": "prologue", "0x11737": "epilogue", "0x11773": "epilogue-2", "0x151d3": "13-b"}
    stages = []
    for ld in loads:
        party, units = placements(fs, ld["templates"], ld["positions"])
        for u in units:
            u["name"] = chars.get(u["nameId"], "?")
            u["className"] = classes[u["class"]] if 0 <= u["class"] < len(classes) else "NPC(특수)"
            u["side"] = "ally" if u["class"] < 18 else "enemy"
            u["cls"] = u["class"]                     # Unity JsonUtility용(FarlandRules.Stats.cls)
        stages.append(dict(stage=str(ld["stage"] or special.get(ld["call"], ld["call"])), call=ld["call"],
                           partyStart=party, units=units))
    terrains, chip_terrain = terrain_table(fs)
    summon_table, stage_rules = stage_ai(fs, chars, classes)
    # Unity JsonUtility가 읽을 수 있게 사전 대신 배열만 쓴다
    data = dict(unitFields=FIELDS, growthOrder=STATS, classes=classes_table(fs, classes),
                characters=[dict(id=i, name=n) for i, n in chars.items()], terrains=terrains,
                chipTerrainMAPCHIP=chip_terrain["MAPCHIP"], chipTerrainMAPCHIP2=chip_terrain["MAPCHIP2"],
                items=items_table(fs, classes), stages=stages,
                shops=shops(fs, [it["name"] for it in items_table(fs, classes)]),
                chests=chests(fs, [it["name"] for it in items_table(fs, classes)]),
                recruits=recruits(fs, chars, classes),
                itemEffects=ITEM_EFFECTS, classChanges=CLASS_CHANGE, aiModes=AI_MODES,
                summonTable=summon_table, stageAI=stage_rules,
                music=dict(MUSIC, stageBgm=list(struct.unpack_from("<14h", fs.ds, 0x1516))),
                twoHitAttacks=TWO_HIT)
    json.dump(data, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return data


if __name__ == "__main__":
    d = export(sys.argv[1], sys.argv[2])
    for s in d["stages"]:
        print(s["stage"], len(s["partyStart"]), len(s["units"]),
              sorted({(u["name"], u["className"]) for u in s["units"]})[:6])

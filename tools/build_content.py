"""UnityAssets/ 추출물을 Unity 프로젝트의 기본 콘텐츠 팩(StreamingAssets/Content/base)으로 만든다.

사용: python build_content.py <UnityAssets> <Unity 프로젝트 경로>

팩 구조(같은 상대 경로의 파일을 우선순위가 높은 팩이 덮어쓴다):
  pack.json              id·이름·우선순위
  Config/*.json          게임 설정(스테이지 흐름·외형 매핑·UI 문자열) — 원작에 없는, 이 프로젝트가 정한 데이터
  Battle/battle_data.json, Maps/*.json, Dialogue/*.json   원작 추출 데이터
  Sprites/*.png, Tilesets/*.png, Audio/BGM/*.ogg          리소스
"""
import glob, json, os, shutil, subprocess, sys

import imageio_ffmpeg


def dump(obj, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    json.dump(obj, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)


def copy_glob(src, pattern, dst):
    os.makedirs(dst, exist_ok=True)
    n = 0
    for f in glob.glob(os.path.join(src, pattern)):
        shutil.copy2(f, dst); n += 1
    return n


def to_ogg(src_dir, dst_dir):
    os.makedirs(dst_dir, exist_ok=True)
    ff = imageio_ffmpeg.get_ffmpeg_exe()
    for f in sorted(glob.glob(os.path.join(src_dir, "*.wav"))):
        out = os.path.join(dst_dir, os.path.basename(f)[:-4] + ".ogg")
        if os.path.exists(out) and os.path.getmtime(out) >= os.path.getmtime(f):
            continue
        subprocess.run([ff, "-y", "-loglevel", "error", "-i", f, "-c:a", "libvorbis", "-q:a", "4", out], check=True)


# 원작 1스테이지 흐름 (FSMAIN 훅 분석 결과). 다른 스테이지도 같은 형식으로 추가하면 된다.
#   args: "unit:N" 아군/적 슬롯 N(배치 순서), "boss" 보스, "attacker"/"defender" 교전 당사자, "mover" 이동한 유닛, "str:텍스트"
STAGES = [
    dict(id="1", title="제1장", map="Maps/STAGE01.tmj", dialogue="Dialogue/STAGE01.json", placement="1",
         music=2, bossMusic=7, startGold=1000, warp=True,           # 0x10FF4: [0x52A] = 1000. WARP 은 14스테이지만 불가(0x9C18)
         # 프롤로그(0x10BED): 1스테이지 맵에 프롤로그 배치(멜·훼리오·흑기사)를 놓고 대사 0~2
         prologue=dict(placement="prologue", events=[
             dict(trigger="start", talk="0", args=["unit:4", "unit:3"]),
             dict(trigger="start", talk="1", args=["unit:4", "unit:3"]),
             dict(trigger="start", talk="2", args=["unit:4", "unit:3", "unit:18", "unit:19"]),
         ]),
         events=[
             dict(trigger="start", talk="3", args=["unit:0", "unit:1"], cls=-1, ai=-1),
             dict(trigger="start", talk="4", args=["unit:0", "unit:1", "unit:2"]),
             dict(trigger="bossEngaged", talk="5", args=["boss", "attacker"]),
             dict(trigger="bossDefeated", talk="6", args=["boss", "unit:0", "unit:1"], music=2, retreat=True),
             dict(trigger="reach", x=17, y=25, talk="z", args=["mover", "unit:0", "unit:1", "unit:2"], victory=True),
         ],
         defeat=dict(unitSlot=0), next="2"),
]


def T(*cells):
    """지형 변경 목록 (x, y, 맵칩) → 평탄한 int 배열"""
    out = []
    for x, y, c in cells:
        out += [x, y, c]
    return out


def E(trigger, talk, args=(), **kw):
    """스테이지 이벤트. talk_calls.json 의 레지스터 인자는 si=죽거나 공격받은 쪽(boss), di·[bp+6]=상대(attacker)로 옮겼다"""
    e = dict(trigger=trigger, talk=talk, args=list(args), cls=-1, ai=-1, requireItem=0, x=-1, y=-1, slot=-1, setAi=-1, removeCls=-1, name=0)
    e.update(kw)
    return e


# 2~14스테이지 (FSMAIN 스테이지 훅 분석). 음악 = DS:0x1516, 보스 교전 곡 6/7(훅 5), 클리어 = 훅 6 'z' → [0x8A90]=1
STAGE_BGM = [2, 5, 4, 8, 2, 3, 8, 3, 5, 4, 2, 5, 10, 11]
BOSS_MUSIC = {2: 6, 3: 7, 4: 7, 5: 7, 6: 7, 7: 7, 8: 7, 9: 6, 10: 7, 11: 6, 12: 6, 13: 6, 14: 7}
STAGE_SCRIPT = {
    # 이벤트 근거는 tools/stage_events.py 출력(훅별 조건→대사·합류). 격파 위치(x,y)는 DS 상수(마을을 지키던 적 자리)
    2: dict(start=[E("start", "5", ["unit:0", "unit:1", "unit:2"]),
                   E("moved", "6", ["mover"], slot=3)],                                                    # 고달이 처음 움직일 때
            engaged=[],                                                                                   # 드카티에게 다가가기('2')는 FarlandStageScript
            clear=[E("defeated", "0", ["unit:1", "unit:3"], x=25, y=13, recruit=[4]),                     # (25,13) 마을 해방 → 고달
                   E("defeated", "1", ["unit:18", "attacker", "unit:0", "unit:1", "unit:2"], ai=2, retreat=True),
                   E("defeated", "4", ["boss", "unit:2"], cls=60),                                        # 드카티(NPC)를 진정시킴
                   E("defeated", "z", ["attacker", "boss", "unit:1"], cls=60, recruit=[5], victory=True)]),
    3: dict(start=[E("start", "2", ["unit:0", "unit:4"])],
            engaged=[E("bossEngaged", "0", ["boss", "attacker"])],
            clear=[E("defeated", "1", ["unit:0", "unit:5", "unit:1"], x=3, y=26, recruit=[6],               # (3,26) → 디노
                     tiles=T((32, 32, 0x18), (33, 32, 0x18), (32, 33, 0x18))),
                   E("turn", "4", ["name:18"], turn=5),                                                   # [0x84E4]==4 → 5턴째
                   E("bossDefeated", "z", ["boss", "unit:1", "unit:0"], retreat=True, victory=True)]),
    4: dict(start=[E("start", "9", ["unit:0", "unit:4", "unit:1"])],
            engaged=[E("battle", "5", ["attacker", "boss"], ai=16)],                                     # 매복 첫 접촉
            clear=[                                                                                       # (17,44) 리사 합류는 FarlandStageScript
                   E("defeated", "2", ["boss"], ai=4, once=False),                                        # 요정을 붙잡을 때마다
                   E("defeated", "3", ["boss"], ai=4, allGone=True, modifyAi=15, setDefense=18, setMagicDefense=20),  # 요정이 다 잡히면 보스 무적 해제(0x12F33)
                   E("defeated", "0", ["boss"], ai=15, retreat=True, recruit=[8]),                        # 다크 옐후 → 루시다
                   E("defeated", "z", ["unit:7", "unit:6", "unit:4", "unit:0"], ai=15, victory=True)]),
    5: dict(start=[E("start", "9", ["unit:0", "unit:7", "unit:4", "unit:1"]),
                   ],                                                                                     # (29,3) 통로·신비한 나무는 FarlandStageScript
            engaged=[E("bossEngaged", "3", ["boss", "attacker"])],
            clear=[E("defeated", "4", ["attacker", "unit:8"], x=46, y=4, recruit=[9]),                    # (46,4) → 이완
                   E("bossDefeated", "z", ["boss", "unit:0"], retreat=True, victory=True)]),
    6: dict(start=[E("start", "9", ["unit:0", "unit:2", "unit:1"]),
                   ],                                                                                     # 아리나 구출·간조는 FarlandStageScript
            engaged=[],                                                                                   # 잠든 고렘('C'·'7')은 FarlandStageScript
            clear=[E("defeated", "8", ["attacker", "unit:9", "unit:4"], x=12, y=36, recruit=[10]),        # (12,36) → 미리아
                   E("bossDefeated", "z", ["attacker", "unit:10", "unit:0", "unit:4"], retreat=True, victory=True)]),
    7: dict(start=[E("start", "9", ["unit:0", "unit:10"])],
            engaged=[],                                                                                   # 삿큐버스·시민은 FarlandStageScript
            clear=[
                   E("bossDefeated", "z", ["attacker", "boss", "unit:12", "unit:0", "unit:10"], retreat=True, recruit=[13], victory=True)]),
    8: dict(start=[E("start", "9", ["unit:0", "unit:1"])],
            engaged=[E("bossEngaged", "0", ["attacker", "boss"])],
            clear=[E("defeated", "1", ["attacker"], x=9, y=49),
                   E("bossDefeated", "z", ["attacker", "unit:13", "unit:0"], retreat=True, recruit=[14], victory=True)]),
    9: dict(start=[E("start", "9", ["unit:0", "unit:4", "unit:10", "unit:1"]),
                   ],                                                                                     # 스위치·성문 연출은 FarlandStageScript
            engaged=[],
            clear=[E("defeated", "6", ["unit:2", "attacker", "unit:0"], x=6, y=6, recruit=[15]),           # (6,6) → 멜
                   E("defeated", "7", ["attacker", "boss", "unit:0"], x=28, y=16),
                   E("defeated", "z", ["unit:0", "boss", "unit:4", "unit:2", "attacker"], cls=38, retreat=True, victory=True)]),
    10: dict(start=[E("start", "9", ["unit:0", "unit:4", "unit:3", "unit:10"]),
                    E("turn", "1", ["name:20"], turn=3)],                                                 # [0x84E4]==2
             engaged=[E("bossEngaged", "0", ["attacker", "boss"])],
             clear=[E("bossDefeated", "z", ["attacker", "unit:0", "unit:4", "unit:1"], retreat=True, victory=True)]),
    11: dict(start=[E("start", "2", ["unit:4", "unit:10", "unit:0"]),
                    E("reach", "J", ["mover"], x=11, y=44, group="j"), E("reach", "J", ["mover"], x=12, y=44, group="j"),
                    E("reach", "J", ["mover"], x=31, y=44, group="j"), E("reach", "J", ["mover"], x=32, y=44, group="j"),
                    ],                                                                                    # 'I'·'K'~'0'·봉인의 문은 FarlandStageScript
             engaged=[],
             clear=[E("bossDefeated", "3", ["boss"], retreat=True),
                    ]),
    12: dict(start=[E("start", "9", ["unit:0", "unit:1", "unit:4"])],
             engaged=[E("bossEngaged", "0", ["unit:0", "boss", "unit:5"])],
             clear=[E("bossDefeated", "z", ["unit:0", "boss", "unit:5"], retreat=True, victory=True)]),
    13: dict(start=[E("start", "0", ["str:", "unit:0", "unit:1", "unit:4"]), E("start", "1", ["str:", "unit:0", "unit:1", "unit:4"]),
                    E("reach", "7", ["mover"], x=18, y=15, tiles=T((19, 39, 0x6F), *[(x, y, 0x70) for y in range(40, 48) for x in (18, 19)])),
                    E("reach", "", [], x=42, y=14, tiles=T((40, 13, 0x4B), (41, 13, 2))),
                    E("reach", "", [], x=6, y=6, tiles=T((6, 6, 0x55))),                                # 숨은 마을이 나타남
                    E("reach", "", [], x=17, y=36, tiles=T((1, 35, 0x71), (1, 37, 0x71), (1, 43, 0x71),
                                                          *[(x, y, 0x70) for y in range(35, 44) for x in range(2, 19 if y % 2 else 18)]))],
             engaged=[E("battle", "2", ["boss", "unit:0", "attacker"], cls=39), E("battle", "3", ["unit:0", "boss", "unit:18"], cls=40)],
             clear=[E("defeated", "5", ["unit:0", "boss", "unit:4"], cls=39,                          # 다크 프린세스
                      tiles=T((21, 21, 0x71), *[(x, 21, 0x70) for x in range(22, 28)])),
                    E("defeated", "6", ["unit:0", "boss", "attacker"], cls=40, recruit=[16], victory=True)]),   # 다크 퀸 → 훼리오
    14: dict(start=[E("start", "0", ["unit:0", "unit:15", "str:", "unit:10", "unit:7", "unit:5", "unit:4"])],
             engaged=[],
             clear=[E("bossDefeated", "1", ["boss", "boss"], victory=True)]),
}


def resolve_spawns():
    """spawn 의 템플릿 주소를 FSMAIN 에서 읽어 능력치로 바꾼다"""
    import fs_battle_data as fbd
    fs = fbd.Fs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "extracted", "iso", "FSMAIN.EXE"))
    for sc in STAGE_SCRIPT.values():
        for e in sc["start"] + sc["engaged"] + sc["clear"]:
            for sp in e.get("spawn", []):
                t = sp.pop("template")
                vals = dict(zip(fbd.FIELDS, struct.unpack_from("<13h", fs.ds, t)))
                vals["cls"] = vals["class"]
                sp.update(vals)


import struct
resolve_spawns()
for n in range(2, 15):
    sc = STAGE_SCRIPT[n]
    clear = sc["clear"]
    STAGES.append(dict(
        id=str(n), title="제%d장" % n, map="Maps/STAGE%02d.tmj" % n, dialogue="Dialogue/STAGE%02d.json" % n,
        placement="13-b" if n == 13 else str(n),                                              # 13: 1차(대사뿐) 뒤 2차 배치로 바로
        music=17 if n == 13 else STAGE_BGM[n - 1], bossMusic=BOSS_MUSIC[n], warp=n != 14,
        events=sc["start"] + sc["engaged"] + clear, defeat=dict(unitSlot=0),
        next=str(n + 1) if n < 14 else ""))

VISUALS = dict(
    tileSize=32, tileColumns=10,
    mapUnitSheet="Sprites/MAPCHAR.png", mapUnitSize=32, mapUnitColumns=20,
    actedChipOffset=42, actedMaxClass=17,                        # FSMAIN 0x65AF: 아군(클래스<18)이 행동했거나 HP 0이면 +42(회색)
    classChip=[dict(cls=60, chip=12), dict(cls=61, chip=15)],     # 구출 NPC
    facePattern="Sprites/FACE{0:00}.png",
    # ICONCHIP: 32×32 아이콘 가로 12개(다음 칸은 절반 크기판). 상자 닫힘 4, 열림 6 (0x6907)
    iconSheet="Sprites/ICONCHIP.png", iconSize=32, iconColumns=12, chestClosedIcon=4, chestOpenIcon=6,
    warpChips=[0x55, 0x6B],
    # 유닛 오른쪽 아래 작은 글자(ICONCHIP 18 'E', 20 'F', 22 'D')와 커서(16 흰 모서리). 색은 원작 도움말 HELP03
    sideMarks=dict(enemy=18, ally=20, rescue=22, cursor=16, enemyColor="#FF5040", allyColor="#50A0FF", rescueColor="#FFC830"),                                       # 마을 칩: 그 위에서 WARP(0x9C36) — 다른 빈 마을로 이동
    fonts=["Malgun Gothic", "맑은 고딕", "Gulim", "Arial"],
    # 원작 비트맵 글꼴(한글 8x4x4벌 조합형 + 영문 8x16). 없으면 fonts 의 OS 글꼴을 쓴다
    bitmapFont=dict(han="Fonts/IYG.HAN", eng="Fonts/NORMAL.ENG"),
    # 원작 창(WINCHIP: 반투명 바둑판 무늬, WINCHIP2: 불투명). 9조각: border = 왼, 아래, 오른, 위(원작 픽셀)
    windows=dict(normal=dict(image="UI/window.png", border=[7, 9, 9, 6]),
                 solid=dict(image="UI/window_solid.png", border=[7, 9, 9, 6]),
                 menu=dict(image="UI/window_menu.png", border=[7, 9, 9, 6])),
    cursorImage="Sprites/MCSRFONT.png", cursorSize=24,             # 마우스 커서(0 화살표, 5·6 대사 넘김 표시)
    hpGauge=dict(fill="#66BB22", lost="#DD2244", back="#222222"),
    colors=dict(move="#3A78FF70", attack="#FF3A3A70", cursor="#FFFFFFB0", ally="#4FA3FF", enemy="#FF5A5A",
                window="#101830E6", windowBorder="#C8B070"),
)

STRINGS = dict(
    title="파랜드 스토리", pressStart="클릭하거나 Enter 를 누르면 시작합니다",
    playerPhase="아군 턴", enemyPhase="적 턴", endTurn="턴 종료", wait="대기", cancel="취소",
    attackDirect="직접 공격", attackIndirect="간접 공격", victory="승리!", defeat="패배...",
    damage="{0}에게 {1}의 DAMAGE", critical="크리티컬!", noDamage="DAMAGE를 입힐 수 없다", miss="빗나감",
    levelUp="{0}의 레벨이 올랐다!", fainted="{0}은(는) 쓰러졌다", defeated="{0}을(를) 쓰러뜨렸다",
    hp="HP", lv="LV", atk="공격력", def_="방어력", know="지식력", mdef="마법 방어", agi="민첩성", exp="경험치",
    shop="쇼핑", items="도구", buy="사다", sell="팔다", close="닫기", equip="장비하다", use="사용하다", unequip="떼어 내다",
    gold="소지한 금 {0} GOLD", price="{0} GOLD", bought="{0}을(를) 샀습니다.", sold="{0}은(는) {1} GOLD가 됩니다.",
    notEnoughGold="금이 부족합니다.", bagFull="이제 더 이상 가질 수 없습니다.", cantSell="이것은 팔 수 없습니다.",
    cantUse="그것은 사용할 수 없습니다.", noTarget="사용할 상대가 주위에 없습니다.", levelShort="레벨이 부족합니다.",
    equipped="{0}을(를) 장비 하겠습니다.", unequipped="{0}을(를) 떼어 냈습니다.", healed="{0}의 체력이 {1} 회복되었다.",
    fullyHealed="{0}의 체력이 완전히 회복되었다.", nothing="아무 일도 일어나지 않다.", classChanged="{0}은(는) {1}(으)로 클래스 체인지 했다.",
    goldLost="금이 조금 없어졌다 ! 좋은 일을 한 것 같은 예감이 들었다.", statUp="{0}의 {1}이(가) 올랐다.", slept="{0}은(는) 행복스러운 얼굴로 잠들어 버렸다.",
    woke="{0} 정신차리다.", chooseTarget="누구에게 사용합니까?", villager="주민", bagTitle="소지하고 있는 아이템 ({0}/48)", equippedMark="[장비]",
    warp="WARP", command="명령", cmdNone="아무것도 안하다", cmdGather="모두 모여라 !", view="보다",
    chestFound="보물 상자를 발견했다 !", chestGold="보물 상자 안에서 {0} GOLD 를 발견했다.", chestItem="보물 상자 안에서 {0}을(를) 발견했다 !",
    chestFull="그러나 더 이상 가질 수 없어서 포기했다", chestEmpty="보물 상자 안은 텅 비었다 !", warpPick="이동할 마을을 고르십시오",
    menuStart="처음부터 플레이한다", menuOpening="오프닝을 본다", menuEnding="엔딩을 본다", menuContinue="게임을 로드한다",
    saveTitle="저장할 곳을 고르십시오", loadTitle="이어할 기록을 고르십시오", emptySlot="― 비어 있음 ―", saveSlot="{0:00}  {1}",
    saved="{0}번에 저장했습니다.", noSave="저장하지 않음", noSaves="저장된 기록이 없습니다",
    saveGame="저장", inBattleMark="(전투 중 {0}턴)", overwriteAsk="{0}번 기록을 덮어쓸까요?", overwrite="덮어쓰기", song=" 격려의 노래를 연주하다", skip="클릭·Enter: 건너뛰기", backToTitle="클릭하면 타이틀로 돌아갑니다",
    help="좌클릭: 선택·이동·공격   우클릭: 취소 / 빈 곳은 메뉴(턴 종료·저장)   WASD: 화면 이동   휠: 확대   Space: 턴 종료   F5: 저장   B: 전투 연출   F1: 이 설명", controls="조작 설명",
)

# 전투 연출 화면. 클래스별 클립은 tools/battle_anim.py 가 FSMAIN 의 클래스별 연출 함수에서 뽑는다(정적 근사).
BATTLE_SCENE = dict(
    enabled=True, screenWidth=640, screenHeight=400, frameTime=0.09, runSpeed=240,
    allyFeet=[448, 272], enemyFeet=[192, 272], meleeGap=56, backgroundY=32,      # 원작: 띠 y 32~272, 아래 창 y 284~352
    sheetPattern="SpriteDefs/CHAR_{0:00}", background="BattleMaps/BMAP_{0}.png",
    # 지형(0~9) → 배경 글자 (FSMAIN 0x16504: 지형-1 → A..H, 성문은 H, 물은 상대가 물이 아니면 물가 F)
    terrainLetters=["A", "A", "B", "C", "D", "E", "F", "G", "H", "H"],
    stageLetters=[dict(stage=14, letter="K", demonKingLetter="L")],
    common=dict(hit=1, fall=2, down=3, stance=0),             # 상태 0(대기)=프레임 0 (0x1CAA0 등)
    # 효과음 번호 → 파일 (FSMAIN DS:0x7FD 목록: 0~59 FSEFF, 60~111 MV, 112~ FV. 없는 번호는 FSEFF00)
    sounds=[dict(start=0, pattern="Audio/SFX/FSEFF{0:00}.wav"), dict(start=60, pattern="Audio/SFX/MV{0:00}.wav"),
            dict(start=112, pattern="Audio/SFX/FV{0:00}.wav")],
    fallbackSound="Audio/SFX/FSEFF00.wav", soundVolume=0.8,
    hitSounds=None, spells=None, projectileSpeed=320, bodyHeight=40,
    spiritSpells=["ifrit", "frigga", "jinn", "daidan"],                 # 무기 플래그>>8 - 1 순서
)

# 시스템 대사 파일(talk 번호 0x1xx → STAGE51 아이템 설명, 0x2xx → STAGE52 명령·상점 인사)
SYSTEM_TALK = dict(itemInfo="Dialogue/STAGE51.json", system="Dialogue/STAGE52.json",
                   commandBlocks=["0", "1"], shopGreeting="9", shopMusic=[15, 18], chestKeyItem=37, chestKeyTalk="1")

# 게임 흐름: 오프닝 영상 → 타이틀 → (프롤로그) 스테이지 … → endingAfterStage 승리 시 에필로그 대사 → 엔딩 영상
# 영상은 op_emu/fs_ending 에뮬레이션으로 뽑은 원작 화면 녹화(음악 포함). 같은 경로의 mp4 로 교체할 수 있다.
# 에필로그(0x116B8): STAGE99 대사를 코드 순서로. unit:N = 동료 N번(얼굴 N, 이름 id N+1). 조건 분기 대사는 첫 것만.
EPILOGUE = dict(dialogue="Dialogue/STAGE99.json", background="Sprites/END2.png", talks=[
    dict(talk="@", args=[]), dict(talk="0", args=["unit:7", "unit:6"]), dict(talk="a", args=["unit:13", "str:할아버지"]),
    dict(talk="1", args=["unit:11", "unit:10"]), dict(talk="2", args=["unit:11"]), dict(talk="3", args=["unit:10", "unit:11"]),
    dict(talk="4", args=["unit:9"]), dict(talk="b", args=["unit:12", "unit:5"]), dict(talk="c", args=["unit:8", "str:마을 사람", "str:상인"]),
    dict(talk="g", args=["unit:3"]), dict(talk="h", args=["unit:4"]), dict(talk="d", args=["unit:14"]), dict(talk="e", args=["unit:2", "unit:14"]),
    dict(talk="f", args=["unit:14"]), dict(talk="x", args=["unit:0", "unit:15", "unit:1", "str:모하드"]),
    dict(talk="y", args=["unit:0", "unit:15", "unit:1"]), dict(talk="z", args=["unit:0", "unit:15", "unit:1"]),
])
FLOW = dict(opening="Movies/opening.mp4", ending="Movies/ending.mp4", endingAfterStage="14", epilogue=EPILOGUE)

GAME = dict(flow=FLOW, system=SYSTEM_TALK, startStage="1", musicFile="Audio/BGM/track{0:00}.ogg", musicTrackOffset=1,
            titleImage="Sprites/TITLE.png", charactersFile="Dialogue/characters.json",
            battleData="Battle/battle_data.json", enemyStepDelay=0.25, rngSeed=0)


def make_ui(iso_sprites, iso, base):
    """원작 창 조각(WINCHIP/WINCHIP2)을 9조각 이미지로 다시 짜고, 글꼴 파일을 복사한다"""
    from PIL import Image
    os.makedirs(os.path.join(base, "UI"), exist_ok=True)

    def compose(src, recolor=None):
        im = Image.open(os.path.join(iso_sprites, src)).convert("RGBA")
        full = Image.new("RGBA", (26, 60)); full.paste(im, (0, 0))
        cols = [(0, 7), (7, 9), (17, 26)]                 # 왼쪽 7, 가운데 2(바둑판 주기), 오른쪽 9(그림자 포함)
        rows = [(4, 10), (10, 12), (48, 57)]
        out = Image.new("RGBA", (18, 17))
        oy = 0
        for y0, y1 in rows:
            ox = 0
            for x0, x1 in cols:
                out.paste(full.crop((x0, y0, x1, y1)), (ox, oy)); ox += x1 - x0
            oy += y1 - y0
        if recolor:
            px = out.load()
            for y in range(out.height):
                for x in range(out.width):
                    if px[x, y][:3] == (51, 102, 187) and px[x, y][3]:
                        px[x, y] = recolor + (255,)
        return out
    compose("WINCHIP.png").save(os.path.join(base, "UI", "window.png"))
    compose("WINCHIP2.png").save(os.path.join(base, "UI", "window_solid.png"))
    compose("WINCHIP2.png", (68, 51, 136)).save(os.path.join(base, "UI", "window_menu.png"))   # 타이틀 메뉴(보라)
    os.makedirs(os.path.join(base, "Fonts"), exist_ok=True)
    for f in ("IYG.HAN", "OKSAN.HAN", "NORMAL.ENG"):
        shutil.copy2(os.path.join(iso, f), os.path.join(base, "Fonts", f))


def split_sheets(base):
    """한 장에 묶인 스프라이트 시트를 칸마다 낱장으로 나눈다(게임은 낱장을 우선 쓴다).
    Unity 에디터 메뉴 Farland/리소스 와 같은 규칙: 시트 'A/B.png' 의 i번 칸 → 'A/B/iii.png', 빈 칸은 만들지 않음"""
    from PIL import Image
    v = VISUALS
    sheets = [(v["mapUnitSheet"], v["mapUnitSize"], v["mapUnitSize"], None), (v["iconSheet"], v["iconSize"], v["iconSize"], None),
              (v["cursorImage"], v["cursorSize"], v["cursorSize"], None)]
    sheets += [("Tilesets/" + f, v["tileSize"], v["tileSize"], None) for f in os.listdir(os.path.join(base, "Tilesets")) if f.endswith(".png")]
    for f in glob.glob(os.path.join(base, "SpriteDefs", "*.json")):
        d = json.load(open(f, encoding="utf-8"))
        sheets.append(("SpriteDefs/" + os.path.basename(f)[:-5] + "_frames.png", d["frameWidth"], d["frameHeight"], d["frameCount"]))
    n = 0
    for rel, w, h, count in sheets:
        src = os.path.join(base, rel)
        if not os.path.exists(src):
            continue
        im = Image.open(src).convert("RGBA")
        cols, rows = im.width // w, im.height // h
        out = src[:-4]
        shutil.rmtree(out, ignore_errors=True)
        os.makedirs(out)
        for i in range(min(count or cols * rows, cols * rows)):
            c = im.crop((i % cols * w, i // cols * h, i % cols * w + w, i // cols * h + h))
            if c.getbbox() or rel.startswith("Tilesets/"):                  # 지형은 빈 칩도(Tiled 번호 = 맵칩 번호)
                c.save(os.path.join(out, "%03d.png" % i)); n += 1
    return n


def make_editable(base):
    """사람이 고치기 쉬운 형식을 만든다.
    - Maps/STAGExx.tmj + Tilesets/*.tsj : Tiled(https://www.mapeditor.org) 맵. 육각(짝수 행이 반 칸 오른쪽), 타일 번호 = 맵칩 번호
    - Dialogue/STAGExx.tsv : 대사 표(블록, 줄 번호, 말하는 얼굴, 원문, 고친 대사). '고친 대사' 칸이 비어 있지 않으면 게임이 그 문장을 쓴다
    - Config/glossary.json : 고유명사 일괄 바꾸기(이름·클래스·아이템·대사 전체)"""
    import csv
    # 타일셋: 낱장 이미지 모음
    for ts in ("MAPCHIP_tiles", "MAPCHIP2_tiles"):
        d = os.path.join(base, "Tilesets", ts)
        if not os.path.isdir(d):
            continue
        ids = sorted(int(f[:-4]) for f in os.listdir(d) if f.endswith(".png"))
        dump(dict(type="tileset", version="1.10", tiledversion="1.10.2", name=ts, tilewidth=32, tileheight=32, columns=0, margin=0, spacing=0,
                  tilecount=len(ids), grid=dict(orientation="orthogonal", width=32, height=32),
                  tiles=[dict(id=i, image="%s/%03d.png" % (ts, i), imagewidth=32, imageheight=32) for i in ids]),
             os.path.join(base, "Tilesets", ts + ".tsj"))
    # 맵: 육각 엇갈림(y축, 짝수 행), 행 간격 32 = (tileheight + hexsidelength) / 2
    for f in glob.glob(os.path.join(base, "Maps", "*.json")):
        m = json.load(open(f, encoding="utf-8"))
        tmj = dict(type="map", version="1.10", tiledversion="1.10.2", orientation="hexagonal", renderorder="right-down",
                   staggeraxis="y", staggerindex="even", hexsidelength=32, width=m["width"], height=m["height"],
                   tilewidth=32, tileheight=32, infinite=False, nextlayerid=2, nextobjectid=1,
                   tilesets=[dict(firstgid=1, source="../Tilesets/%s.tsj" % m["tileset"])],
                   layers=[dict(id=1, name="terrain", type="tilelayer", visible=True, opacity=1, x=0, y=0,
                                width=m["width"], height=m["height"], data=[t + 1 for t in m["tiles"]])])
        dump(tmj, f[:-5] + ".tmj")
        os.remove(f)                                                       # 맵은 .tmj 하나만 둔다(고칠 곳을 헷갈리지 않게)
    # 대사 표
    chars = json.load(open(os.path.join(base, "Dialogue", "characters.json"), encoding="utf-8"))["characters"]
    face_name = {c["face"]: c["name"] for c in chars}
    for f in glob.glob(os.path.join(base, "Dialogue", "STAGE*.json")):
        sc = json.load(open(f, encoding="utf-8"))
        with open(f[:-5] + ".tsv", "w", encoding="utf-8", newline="") as out:
            w = csv.writer(out, delimiter="\t", lineterminator="\n")
            w.writerow(["블록", "줄", "얼굴", "원문", "고친 대사"])
            for b in sc["blocks"]:
                face = ""
                for i, c in enumerate(b["commands"]):
                    if c["cmd"] == "portrait":
                        face = face_name.get(c["face"], "") if c["face"] >= 0 else "(인자 %d)" % c["arg"]
                    if c["cmd"] == "text" and c["text"]:
                        w.writerow([b["id"], i, face, c["text"], ""])
    # 용어집: 처음엔 '그대로'(바꿀 말 = 원래 말). to 만 고치면 모든 화면·대사에 반영
    bd = json.load(open(os.path.join(base, "Battle", "battle_data.json"), encoding="utf-8"))
    terms, seen = [], set()
    for kind, names in (("인물", [c["name"] for c in chars]), ("클래스", [c["name"] for c in bd["classes"]]),
                        ("아이템", [i["name"] for i in bd["items"] if i.get("name")])):
        for n in names:
            n = n.strip()
            if n and n not in seen:
                seen.add(n); terms.append(dict(kind=kind, **{"from": n, "to": n}))
    dump(dict(note="from 을 to 로 바꿉니다(긴 말부터). 대사·이름표·클래스·아이템 이름 모두에 적용", terms=terms),
         os.path.join(base, "Config", "glossary.json"))


def build(assets, project):
    base = os.path.join(project, "Assets", "StreamingAssets", "Content", "base")
    for sub in ("Config", "Battle", "Maps", "Dialogue", "Sprites", "Tilesets"):
        shutil.rmtree(os.path.join(base, sub), ignore_errors=True)
    dump(dict(id="base", name="Farland Story (원작 추출)", priority=0,
              description="CD 이미지에서 추출한 기본 리소스. 다른 팩이 같은 경로의 파일로 덮어쓸 수 있다."),
         os.path.join(base, "pack.json"))
    dump(dict(stages=STAGES), os.path.join(base, "Config", "stages.json"))
    dump(VISUALS, os.path.join(base, "Config", "visuals.json"))
    dump(STRINGS, os.path.join(base, "Config", "strings.json"))
    dump(GAME, os.path.join(base, "Config", "game.json"))
    import battle_anim
    counts = {}
    for f in glob.glob(os.path.join(assets, "SpriteDefs", "CHAR_*.json")):
        counts[int(os.path.basename(f)[5:7])] = json.load(open(f, encoding="utf-8"))["frameCount"]
    spells = [dict(v, routine=hex(k)) for k, v in battle_anim.SPELLS.items()]
    for sp in battle_anim.SPIRITS:
        spells.append(dict(sp, frames=[], move=False))                 # frames 비움 = 정령 시트 전체
    for sp in spells:
        sp.setdefault("launch", []); sp.setdefault("soundFrames", []); sp.setdefault("hits", 1); sp.setdefault("sheet", "")
    dump(dict(BATTLE_SCENE, hitSounds=battle_anim.HIT_SOUNDS, spells=spells, classes=battle_anim.extract(os.path.join(os.path.dirname(__file__), battle_anim.EXE), counts)),
         os.path.join(base, "Config", "battle_scene.json"))
    shutil.rmtree(os.path.join(base, "SpriteDefs"), ignore_errors=True)
    shutil.rmtree(os.path.join(base, "BattleMaps"), ignore_errors=True)
    copy_glob(os.path.join(assets, "SpriteDefs"), "CHAR_*", os.path.join(base, "SpriteDefs"))
    copy_glob(os.path.join(assets, "BattleMaps"), "*.png", os.path.join(base, "BattleMaps"))
    copy_glob(os.path.join(assets, "Audio", "SFX"), "*.wav", os.path.join(base, "Audio", "SFX"))
    os.makedirs(os.path.join(base, "Movies"), exist_ok=True)
    shutil.copy2(os.path.join(assets, "Opening", "opening_preview.mp4"), os.path.join(base, "Movies", "opening.mp4"))
    shutil.copy2(os.path.join(assets, "Ending", "ending_preview.mp4"), os.path.join(base, "Movies", "ending.mp4"))
    copy_glob(os.path.join(assets, "Battle"), "battle_data.json", os.path.join(base, "Battle"))
    copy_glob(os.path.join(assets, "Maps"), "*.json", os.path.join(base, "Maps"))
    copy_glob(os.path.join(assets, "Dialogue"), "*.json", os.path.join(base, "Dialogue"))
    copy_glob(os.path.join(assets, "Sprites"), "*.png", os.path.join(base, "Sprites"))
    copy_glob(os.path.join(assets, "Tilesets"), "*.png", os.path.join(base, "Tilesets"))
    to_ogg(os.path.join(assets, "Audio", "BGM"), os.path.join(base, "Audio", "BGM"))
    print("낱장", split_sheets(base))
    make_editable(base)
    make_ui(os.path.join(assets, "Sprites"), os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "extracted", "iso"), base)

    # 교체 예시 팩(비활성화된 상태로 둔다: pack.json 의 enabled=false)
    ex = os.path.join(project, "Assets", "StreamingAssets", "Content", "example_override")
    dump(dict(id="example_override", name="교체 예시", priority=10, enabled=False,
              description="enabled 를 true 로 바꾸면 이 팩의 파일이 base 를 덮어쓴다. "
                          "같은 상대 경로에 PNG·OGG·JSON 을 넣으면 된다."), os.path.join(ex, "pack.json"))
    dump(dict(STRINGS, title="FARLAND STORY (교체 예시)"), os.path.join(ex, "Config", "strings.json"))


if __name__ == "__main__":
    build(sys.argv[1], sys.argv[2])
    print("ok")

using System.Linq;
using UnityEngine;

// 원작 FSMAIN.EXE 스테이지 훅(DS:0x1356) 중 stages.json 의 단순 이벤트로는 옮기기 어려운 대본 분기를 그대로 옮긴 것.
// 동료 합류 여부·누가 움직였나·맵칩 상태·턴 카운터처럼 조건이 얽힌 부분만 여기서 처리하고, 대사 내용은 Dialogue/STAGExx.json 이다.
// 좌표는 원작 (x, y), 대사 블록 id 는 원작 talk 번호의 글자(0x31 → '1'). 근거 주소는 각 분기 주석.
public static class FarlandStageScript
{
    /// 대본이 게임에 요청하는 동작(BattleState 가 구현)
    public interface IHost : FarlandStageAI.IStageMap
    {
        int TurnIndex { get; }                              // 원작 [0x84E4]: 첫 아군 턴 0
        int[] Vars { get; }                                 // 원작 [0x84E6]·[0x84E8]·[0x84EA]·[0x84EC] (저장됨)
        FarlandAI.Unit Slot(int index);                     // 유닛 배열 칸(0~17 아군 = 얼굴 번호, 18~ 적)
        bool HasMember(int nameId);                         // 0xE57:0x1E7 — 이 동료가 합류해 있나
        void Talk(string block, params object[] args);      // 인자: 유닛 또는 문자열
        void Message(string text);
        void Sound(int n);
        void Music(int n);
        int StageMusic { get; }
        void Wave(string id, int stage);                    // 0x9ECC 증원 목록(battle_data stageAI.waves)
        FarlandAI.Unit Create(string templateId, int stage, Vector2Int at);   // stageAI.templates
        void Recruit(int nameId, Vector2Int at);            // 동료 합류(0x928:0x8A6 아군 템플릿)
        void Remove(FarlandAI.Unit u);                      // 0x65A:0xB7F(유닛, 8) — 쓰러뜨리지 않고 맵에서 뺌
        void Retreat();
        void Victory();
        bool GiveItem(int id);                              // 0xB3C:0x364 — 가방이 가득이면 false
        bool BagFull { get; }
        bool TakeItem(int id);
        int Random(int n);
        void SwapMap(string mapFile);                       // 0x65A:0x1A17 — 맵칩을 다른 맵 파일 것으로 바꿈
        FarlandStageAI.State StageState { get; }
        int IndexOf(FarlandAI.Unit u);                      // 유닛 배열 칸 번호
        string NameOf(FarlandAI.Unit u);
        void Reveal(FarlandAI.Unit u, string templateId, int stage);   // 템플릿의 revealClass·revealName 으로 바꿈
    }

    static Vector2Int P(int x, int y) => new Vector2Int(x, y);

    static FarlandAI.Unit Find(IHost h, System.Func<FarlandAI.Unit, bool> f) =>
        h.Units.FirstOrDefault(u => u.stats.hp > 0 && !u.IsPlayerSide && f(u));

    // ---- 훅 1: 아군 턴 시작 ----
    public static void TurnStart(int stage, IHost h)
    {
        var v = h.Vars;
        switch (stage)
        {
            case 3:     // 0x129C3: 아군 둘 이상이 y>20 으로 내려오면 증원 시작, 이후 4턴마다(최대 16턴). 처음엔 (0,14)에 가장 가까운 아군이 '3'
                if (v[0] == 0 && h.Units.Count(u => u.IsPlayerSide && u.stats.hp > 0 && u.pos.y > 20) >= 2) v[0] = 1;
                if (v[0] > 0 && v[0] < 17 && v[0]++ % 4 == 1) h.Wave("0x32de", 3);
                if (v[0] == 2)
                {
                    var near = h.Units.Where(u => u.IsPlayerSide && u.stats.hp > 0).OrderBy(u => FarlandRules.HexDistance(u.pos, P(0, 14))).FirstOrDefault();
                    if (near != null) h.Talk("3", near);
                }
                break;
            case 7:     // 0x137DE: 아군 셋 이상이 x>27 이면 증원 시작. 4턴마다 옐후·하아피 / 쟉크오랜턴 무리가 번갈아
                if (v[0] == 0 && h.Units.Count(u => u.IsPlayerSide && u.stats.hp > 0 && u.pos.x > 27) >= 3) v[0]++;
                if (v[0] > 0 && v[0] < 17 && v[0]++ % 4 == 1) h.Wave(v[0] / 4 % 2 != 0 ? "0x3b90" : "0x3bb4", 7);
                break;
            case 8:     // 0x13AFE: 매복을 건드린 다음 턴 — 건드린 아군에게 닌자 '2' (AI 전환은 이어서 FarlandStageAI.OnTurnStart)
                if (h.StageState.flag == 1 && h.StageState.triggerUnit != null) h.Talk("2", h.StageState.triggerUnit, "인자");
                break;
            case 10:    // 0x14255: 3턴째 스켈톤 증원(대사 '1' 은 stages.json turn 이벤트)
                if (h.TurnIndex == 2) h.Wave("0x43de", 10);
                break;
            case 12:    // 0x14E61: 11~13턴째 하아피 증원, 보스와 교전한 뒤 10턴 동안 닌자 증원
                if (h.TurnIndex >= 10 && h.TurnIndex < 13) h.Wave("0x4a04", 12);
                if (v[0] > 0 && v[0] <= 10) { v[0]++; h.Wave("0x4a28", 12); }
                break;
        }
    }

    // ---- 훅 2: 아군 이동 직후 ----
    public static void UnitMoved(int stage, IHost h, FarlandAI.Unit u)
    {
        var v = h.Vars;
        var at = u.pos;
        switch (stage)
        {
            case 5:
                // 0x13025: (29,3) 의 막힌 통로. 이완(이름 9)이 합류해 있을 때만 — 이완이 오면 열고, 다른 아군이면 '0'
                if (at == P(29, 3) && h.Chip(P(26, 3)) != 2 && h.HasMember(9))
                {
                    if (u.stats.nameId == 9)
                    {
                        h.Talk("1", u);
                        h.Sound(7);
                        for (int i = 0; i < 3; i++) h.SetChip(P(26 + i, 3), 2);
                        var buddy = h.Slot(h.Random(8));
                        h.Talk("2", u, buddy);
                        foreach (var o in h.Units)
                            if (!o.IsPlayerSide && o.stats.ai == 5 && o.stats.cls != 23) o.stats.ai = 7;
                    }
                    else h.Talk("0", u);
                }
                // 0x1313E: 칩 0x26(신비한 나무) — 만진 아군의 공격·방어·지식·마법방어·민첩 +1, 나무는 사라진다
                if (h.Chip(at) == 0x26)
                {
                    h.Talk("5", u);
                    h.Message(h.NameOf(u) + Particle(u.stats.nameId) + " 나무 줄기를 만져보다");
                    h.SetChip(at, 1);
                    h.Sound(0x2c);
                    var s = u.stats; s.attack++; s.defense++; s.magicDefense++; s.knowledge++; s.agility++;
                    h.Talk("6", u);
                }
                break;
            case 6:
            {
                // 0x1343B: 바위 틈의 아리나(구출 NPC 61). (32,25)에 미리아가 아닌 아군 → '2'(처음)·'1', 미리아가 붙으면 '4'·'3' 후 아리나·파미라 합류 '5'
                var npc = h.HasMember(11) ? null : h.Units.FirstOrDefault(o => o.stats.cls == 61 && o.stats.hp > 0);
                if (npc != null && at == P(32, 25) && u.stats.nameId != 10)
                {
                    h.Talk(npc.stats.exp == 0 ? "2" : "1", u, npc);
                    npc.stats.exp = 0;
                    break;
                }
                if (npc != null && u.stats.nameId == 10 && FarlandRules.HexDistance(u.pos, npc.pos) == 1)
                {
                    h.Talk(npc.stats.exp == 0 ? "4" : "3", u, npc);
                    var p = npc.pos;
                    h.Remove(npc);
                    h.Recruit(11, p);
                    h.Recruit(12, P(p.x + 2, p.y));
                    h.Talk("5", u, h.Slot(10), h.Slot(4), h.Slot(0), h.Slot(11));
                    break;
                }
                // 0x13525: (45,9) — 아리나가 있으면 간조 조개 껍질로 물이 빠진다(STAGE62 맵)
                if (at == P(45, 9) && h.HasMember(11) && v[0] == 0)
                {
                    v[0] = 1;
                    h.Sound(0x37);
                    h.Message("간조 조개 껍질을 발견했다");
                    h.Talk("A", u, h.Slot(10), h.Slot(9));
                    h.SwapMap("Maps/STAGE62.tmj");
                    h.Sound(7);
                    h.Talk("B", h.Slot(10), h.Slot(0));
                }
                break;
            }
            case 9:
                // 0x13E58: (42,2) 스위치 — 위쪽 다리와 x 12~13 통로가 열린다
                if (at == P(42, 2) && h.Chip(P(38, 3)) != 0x70)
                {
                    h.Talk("A", u);
                    h.Sound(0xe);
                    for (int x = 38; x <= 40; x++) h.SetChip(P(x, 3), 0x70);
                    for (int y = 4; y <= 8; y++) for (int x = 12; x <= 13; x++) h.SetChip(P(x, y), 0x70);
                    h.SetChip(P(13, 3), 0x4a);
                    h.Sound(6);
                }
                // 0x13ED8: (10,6) 스위치
                if (at == P(10, 6) && h.Chip(P(9, 15)) != 0x70)
                {
                    h.SetChip(P(11, 3), 0x6e); h.SetChip(P(9, 15), 0x6e);
                    h.Sound(0x33);
                }
                // 0x13F0C: 성문 (26,14)·(27,14). 소녀(클래스 36) 경비가 남아 있으면 '0', 없으면 문이 열리고 흑기사 등장 연출
                if ((at == P(26, 14) || at == P(27, 14)) && h.Chip(P(27, 13)) == 0x24)
                {
                    if (Find(h, o => o.stats.cls == 36) != null) { h.Talk("0", u); break; }
                    h.Talk("1", u, "소녀");
                    h.SetChip(P(27, 13), 0x70);
                    h.Talk("8", h.Slot(0), h.Slot(18), h.Slot(4), h.Slot(10));
                    var girl = h.Create("disguise", 9, P(27, 8));
                    h.Talk("2", "소녀", h.Slot(0), h.Slot(18));
                    if (girl != null) h.Reveal(girl, "disguise", 9);
                    var bk = h.Create("blackKnight", 9, P(27, 9));
                    h.Talk("3", girl, h.Slot(0), h.Slot(18), bk);
                    h.Talk("4", girl, h.Slot(0), h.Slot(18), bk);
                    h.Retreat();
                    if (h.Slot(18) is FarlandAI.Unit captive) h.Remove(captive);
                    h.Talk("5", h.Slot(0), bk);
                    h.Music(6);
                }
                break;
            case 11:
                // 0x148BD: 아래쪽 스위치 (5,44)·(38,44). 밟을 때마다 'A', 양쪽에 아군이 있으면 45행 다리가 놓이고 'I'
                if (v[1] == 0 && (at == P(5, 44) || at == P(38, 44)))
                {
                    h.Sound(0xd);
                    h.Talk("A", u);
                    var other = h.At(at == P(5, 44) ? P(38, 44) : P(5, 44));
                    if (other != null && other.IsPlayerSide)
                    {
                        v[1] = 1;
                        h.SetChip(P(2, 45), 0x71); for (int x = 3; x < 9; x++) h.SetChip(P(x, 45), 0x70);
                        h.SetChip(P(35, 45), 0x20); for (int x = 36; x < 42; x++) h.SetChip(P(x, 45), 0x70);
                        h.Sound(6);
                        h.Talk("I", h.Slot(4));
                    }
                }
                // 0x149BE: (22,51) 고렘의 방 — 고렘이 사라지고 함정에 떨어졌던 동료가 돌아온다('L'·'M'·'N'), 마족 증원 '0'
                if (v[3] == 0 && at == P(22, 51))
                {
                    v[3] = 1;
                    h.Talk("K", u);
                    h.SetChip(P(20, 45), 0x6e); for (int x = 21; x < 25; x++) h.SetChip(P(x, 45), 0x70);
                    foreach (var g in h.Units.Where(o => !o.IsPlayerSide && o.stats.cls == 26 && o.stats.hp > 0).ToList()) h.Remove(g);
                    h.Sound(6);
                    h.Talk("L", u);
                    var fallen = h.At(P(22, 47));
                    h.Talk("M", fallen, u);
                    h.Talk("N", fallen, u, h.Slot(12));
                    h.Retreat();
                    h.Wave("0x46f6", 11);
                    h.Sound(0x2c);
                    h.Talk("0", u, h.Slot(12), h.Slot(4));
                    h.Music(6);
                }
                // 0x14B7C: (22,39) 봉인의 문 — 열쇠(아이템 37)가 있으면 열쇠를 잃고 보물(25)을 얻어 클리어
                if (at == P(22, 39))
                {
                    bool key = h.TakeItem(37);
                    var guard = h.Units.FirstOrDefault(o => o.stats.cls == 22);
                    bool first = guard == null || guard.stats.exp == 0;
                    h.Talk(first ? "5" : "4", h.Slot(0), h.Slot(4), h.Slot(key ? 7 : 1));
                    if (guard != null) guard.stats.exp = 0;
                    if (key)
                    {
                        h.Sound(0xf);
                        h.Message("열쇠를 잃었다...");
                        h.GiveItem(25);
                        h.Talk("z", h.Slot(0), h.Slot(4));
                        h.Victory();
                    }
                }
                break;
        }
    }

    // ---- 훅 5 앞부분: 공격하기 전에 막는 경우 ----
    /// true 면 교전하지 않는다(대사만). 6스테이지: 아리나가 없으면 잠든 고렘을 칠 수 없다('C', 0x136B8)
    public static bool BeforeBattle(int stage, IHost h, FarlandAI.Unit attacker, FarlandAI.Unit defender)
    {
        // 2스테이지(0x12816): 날뛰는 드카티(ai 1)에게는 아크만 다가갈 수 있다. 다른 아군이면 '2' 후 교전 없음
        if (stage == 2 && attacker.IsPlayerSide && defender.stats.ai == 1 && attacker.stats.nameId != 1)
        {
            h.Talk("2", attacker, defender);
            return true;
        }
        if (stage == 6 && defender.stats.cls == 26 && defender.stats.ai == 5 && !h.HasMember(11))
        {
            h.Talk("C", h.Slot(9));
            return true;
        }
        return false;
    }

    // ---- 훅 5: 교전 ----
    public static void Battle(int stage, IHost h, FarlandAI.Unit attacker, FarlandAI.Unit defender)
    {
        var d = defender.stats;
        switch (stage)
        {
            case 2:
                // 0x127E1: 드카티(유닛 18)가 아직 진정되지 않았을 때 아리시아가 아크를 회복하면 드카티가 끼어든다 '3'
                // 원작은 유닛 칸 18 의 클래스로 본다. 여기서는 배치 순서가 달라 '진정된 드카티(클래스 60)가 맵에 있다'로 판정
                var dk = h.Units.FirstOrDefault(o => o.stats.cls == 60 && o.stats.hp > 0);
                if (h.Vars[3] == 0 && dk != null &&
                    attacker.stats.nameId == 3 && defender.stats.nameId == 1 && d.hp > 0)
                {
                    h.Talk("3", dk, attacker, defender);
                    h.Vars[3] = 1;
                }
                if (d.ai == 1 && attacker.stats.nameId == 1) { d.cls = 12; h.Music(6); }      // 아크가 맞서면 바사카로
                break;
            case 6:
                if (d.cls == 61) { d.cls = 15; d.exp = 0; h.Talk("6", defender); }      // 0x13691: 적이 아리나를 치면 왈큐레로 맞선다
                if (d.cls == 26 && d.ai == 5 && h.HasMember(11)) { h.Talk("7", attacker, h.Slot(10)); d.ai = 2; }   // 고렘이 깨어나 보스로
                if (d.ai == 2) h.Music(7);                                                // 0x136EB
                break;
            case 7:
            {
                // 0x138D7: 삿큐버스(25)에게 홀린 시민(이름 27). 삿큐버스의 exp 칸을 '시민을 친 적 있음' 표시로 쓴다
                var succ = h.Units.FirstOrDefault(o => !o.IsPlayerSide && o.stats.cls == 25);
                if (d.nameId == 27 && succ != null && succ.stats.exp == 0) h.Talk("0", attacker);
                if (d.cls == 25 && d.ai == 5)
                {
                    h.Talk("1", attacker, defender);
                    var next = succ != null ? h.Slot(h.IndexOf(succ) + 1) : null;
                    if (next != null) d.nameId = next.stats.nameId;                    // 정체를 드러냄(이름이 바뀜)
                    h.Music(7);
                }
                if (d.cls == 25 && d.ai == 15) { h.Talk("2", attacker, defender); d.ai = 2; }
                if (d.ai == 2) h.Music(6);                                                // 0x13955
                break;
            }
        }
    }

    // ---- 훅 6: 교전이 끝난 뒤(쓰러짐 처리 다음) ----
    public static void AfterBattle(int stage, IHost h, FarlandAI.Unit attacker, FarlandAI.Unit defender)
    {
        var d = defender.stats;
        switch (stage)
        {
            case 4:
                // 0x12E4F: (17,44) 를 지키던 적을 쓰러뜨리면 리사 합류 '1', 요정(ai 4)의 방어·마법방어 14
                if (defender.pos == P(17, 44) && !h.HasMember(7) && d.hp <= 0)
                {
                    h.Recruit(7, defender.pos);
                    h.Talk("1", attacker, h.Slot(6));
                    foreach (var o in h.Units) if (!o.IsPlayerSide && o.stats.hp > 0 && o.stats.ai == 4) { o.stats.defense = 14; o.stats.magicDefense = 14; }
                }
                if (d.ai == 15 && d.defense == 999) h.Talk("4", defender);        // 0x12F44: 요정이 남아 무적인 다크 옐후
                break;
            case 5:
                if (h.Vars[0] == 0 && d.ai == 5 && d.cls == 23) { h.Talk("7", attacker); h.Vars[0] = 1; }   // 0x1337A: 잠든 스켈톤을 건드림
                break;
            case 7:
            {
                var succ = h.Units.FirstOrDefault(o => !o.IsPlayerSide && o.stats.cls == 25);
                if (d.nameId == 27 && succ != null)                                    // 0x1398D: 시민을 친 뒤
                {
                    h.Talk(succ.stats.exp == 0 ? "3" : "4", attacker);
                    succ.stats.exp = 1;
                }
                if (d.cls == 25 && d.ai == 5)                                            // 0x139BE: 잠에서 깬 삿큐버스 — 시민이 사라지고 본모습으로
                {
                    h.Music(h.StageMusic);
                    h.Talk("5", attacker, defender);
                    foreach (var c in h.Units.Where(o => !o.IsPlayerSide && o.stats.hp > 0 && o.stats.nameId == 27).ToList()) h.Remove(c);
                    h.Talk("6", attacker, defender);
                    foreach (var o in h.Units.Where(o => !o.IsPlayerSide && o.stats.cls == 25)) { o.stats.defense = 23; o.stats.magicDefense = 32; o.stats.ai = 15; }
                }
                break;
            }
            case 8:
                // 0x13D35: (9,49) 마을을 지키던 적(ai 6)을 쓰러뜨림 — 노인이 메두사킬러를 준다(가방이 가득이면 '3')
                if (defender.pos == P(9, 49) && d.ai == 6 && d.hp <= 0) GiveMedusaKiller(h, attacker, true);
                break;
            case 9:
                // 0x14196: (28,16) 를 지키던 적 — 소녀(클래스 36)가 '7' 후 사라진다
                if (defender.pos == P(28, 16) && d.ai == 3 && d.hp <= 0)
                {
                    var girl = h.Units.FirstOrDefault(o => !o.IsPlayerSide && o.stats.cls == 36);
                    if (girl != null) { h.Talk("7", attacker, girl, h.Slot(0)); h.Remove(girl); }
                }
                break;
        }
    }

    // ---- 훅 3: 마을에서 쇼핑하기 전 ----
    public static void Village(int stage, IHost h, FarlandAI.Unit u)
    {
        if (stage == 8 && h.Vars[2] == 0 && u.pos == P(9, 49)) GiveMedusaKiller(h, u, false);   // 0x13B9E
    }

    static void GiveMedusaKiller(IHost h, FarlandAI.Unit u, bool rescued)
    {
        if (rescued) h.Talk("1", u, "노인");
        if (h.Vars[2] != 0) return;
        if (h.BagFull) { h.Talk("3", "노인"); return; }                       // '소지품이 잔뜩 있잖아'
        if (!rescued) h.Talk("4", "노인");
        h.GiveItem(66);
        h.Sound(0x37);
        h.Message("메두사킬러를 손에 넣었다");
        h.Vars[2] = 1;
        h.Talk("5", "노인");
    }

    // ---- 훅 4 일부: 9스테이지 흑기사 4번째 행동 → 'B' 후 증원(FarlandStageAI.Waypoint 가 신호) ----
    public static void BlackKnightCall(IHost h, FarlandAI.Unit bk) => h.Talk("B", bk);

    /// 12스테이지: 보스와 처음 교전하면 닌자 증원 카운터를 켠다(0x14F45)
    public static void BossEngaged(int stage, IHost h) { if (stage == 12 && h.Vars[0] == 0) h.Vars[0] = 1; }

    /// 원작 0x1318A: 이름 4·9·13·15 는 받침이 있어 '은', 나머지 '는'
    public static string Particle(int nameId) => nameId == 4 || nameId == 9 || nameId == 13 || nameId == 15 ? "은" : "는";
}

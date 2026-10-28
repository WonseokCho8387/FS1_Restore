using System.Collections.Generic;
using UnityEngine;

// 원작 FSMAIN.EXE 스테이지 대본(훅 테이블 DS:0x1356, 스테이지당 7개) 중 적 행동에 관한 부분 포팅.
// 데이터는 battle_data.json 의 stageAI(소환 템플릿·증원·발판), 규칙 설명은 stageAI[].rules.
// 좌표는 원작 (x, y). stage 는 1부터(원작 [0x568] + 1).
public static class FarlandStageAI
{
    public interface IStageMap : FarlandAI.IBoard
    {
        int Chip(Vector2Int p);                    // 맵칩 번호(0~119)
        void SetChip(Vector2Int p, int chip);      // 타일 교체(지형도 칩을 따라 바뀜)
    }

    /// 스테이지 진행 플래그(원작 0x84E6/0x84E8 등). 스테이지 시작마다 새로 만든다.
    public class State
    {
        public int flag;                           // 0x84E6
        public FarlandAI.Unit triggerUnit;         // 0x84E8 (8스테이지: 매복을 건드린 아군)
        public bool reinforce;                     // 9스테이지 증원 신호: 대사 0x42 → Reinforcements() 생성 → false
        public FarlandAI.Unit trapped;             // 11스테이지 함정에 떨어진 아군: 호출 측이 고렘(공격력 999) 공격 연출 후 null
        public bool switchTalked;                  // 11스테이지: 스위치 하나만 밟았을 때 대사 'A' 는 한 번
        public readonly int[] vars = new int[4];   // FarlandStageScript 의 [0x84E6]~[0x84EC]
        /// 이 클래스가 직접 진행하는 연출의 대사(블록 id, 인자). 호출 측이 꺼내 재생한다
        public readonly System.Collections.Generic.List<(string block, FarlandAI.Unit[] args)> talks =
            new System.Collections.Generic.List<(string, FarlandAI.Unit[])>();
    }

    static readonly Vector2Int None = new Vector2Int(-1, -1);
    static Vector2Int P(int x, int y) => new Vector2Int(x, y);

    // ---- 훅 4: 적 유닛 하나의 행동 ----
    /// 경유지를 정해 FarlandAI.PlanTurn 을 부르고, 이동 뒤 AI 전환까지 한다. false 면 이 유닛은 이번에 아무것도 안 함.
    /// 반환된 plan 대로 이동·공격한 다음 AfterMove 를 불러야 한다.
    public static bool Waypoint(int stage, FarlandAI.Unit u, IStageMap m, State st, out Vector2Int? waypoint)
    {
        waypoint = null;
        var s = u.stats;
        switch (stage)
        {
            case 2:
                if (s.cls == 60) return false;                                           // 구출 NPC
                if (s.ai == 8 && m.Chip(P(34, 20)) == 0x6C) waypoint = P(25, 13);        // 다리가 놓인 뒤 (DS:0x5EC8 = 2513)
                break;
            case 3:
                if (s.ai == 8) waypoint = P(13, 18);
                else if (s.ai == 11) waypoint = P(3, 26);
                break;
            case 5:
                if (s.ai == 8) waypoint = P(14, 25);
                else if (m.Chip(P(26, 3)) == 2) waypoint = u.pos.y > 12 ? P(16, 9) : P(26, 3);  // 통로 개방 뒤
                break;
            case 8:
                if (s.ai == 16) return false;                                            // 매복은 대본 트리거로만 깨어남
                break;
            case 9:
                if (s.cls == 38 && ++st.flag == 4) st.reinforce = true;                  // 흑기사 4번째 행동(두 번 행동하므로 2턴째)
                break;
            case 11:
                if (s.ai == 8) waypoint = P(11, 28);
                else if (s.ai == 10) waypoint = P(8, 40);
                else if (s.ai == 11) waypoint = P(35, 40);
                else if (s.ai == 16) return false;
                break;
        }
        return true;
    }

    /// 훅 4의 이동 뒤 AI 전환
    public static void AfterMove(int stage, FarlandAI.Unit u)
    {
        var s = u.stats;
        switch (stage)
        {
            case 2: if (s.ai == 8 && u.pos.x < 32) s.ai = 9; break;
            case 3:
                if (s.ai == 8) { if (u.pos.x < 22) s.ai = 11; }
                else if (s.ai == 11 && FarlandRules.HexDistance(u.pos, P(3, 26)) < 4) s.ai = 9;
                break;
            case 5: if (s.ai == 8 && u.pos.x >= 12) s.ai = 17; break;
            case 11:
                if (s.ai == 8 && FarlandRules.HexDistance(u.pos, P(11, 28)) < 3) s.ai = 9;
                else if (s.ai == 10 && FarlandRules.HexDistance(u.pos, P(8, 40)) < 3) s.ai = 7;
                else if (s.ai == 11 && FarlandRules.HexDistance(u.pos, P(35, 40)) < 3) s.ai = 7;
                break;
        }
    }

    // ---- 소환 생성 (훅 4가 슬롯 0 임시 유닛을 받았을 때, 4~14스테이지) ----
    /// 스테이지 템플릿 목록에서 같은 클래스의 첫 항목. 없으면 null(소환 실패). 생성된 유닛은 이번 턴 행동 완료 상태.
    public static FarlandRules.Stats SummonTemplate(FarlandRules.Data d, int stage, int cls)
    {
        foreach (var e in d.stageAI)
            if (e.stage == stage && e.summonTemplates != null)
                foreach (var t in e.summonTemplates)
                    if (t.cls == cls) return t.Clone();
        return null;
    }

    // ---- 9스테이지 증원 (0x9ECC) ----
    /// 목록의 빈 칸에만 생성. 흑기사가 공격받으면(훅 5) st.flag = 0 으로 되돌아가 다시 4회를 센다.
    public static IEnumerable<FarlandRules.Spawn> Reinforcements(FarlandRules.Data d, IStageMap m)
    {
        foreach (var e in d.stageAI)
            if (e.stage == 9 && e.reinforcements != null)
                foreach (var r in e.reinforcements)
                    if (m.At(P(r.x, r.y)) == null) yield return r;
    }

    public static void OnAttacked(int stage, FarlandAI.Unit defender, State st)
    {
        if (stage == 9 && defender.stats.cls == 38) st.flag = 0;
    }

    // ---- 13스테이지 발판 (0x14FA7, 적 하나가 행동할 때마다) ----
    /// 발판(칩 0x48) 위에 유닛이 있으면 연결 칸을 칩 0x24(통행불가)로, 비면 칩 0x6E(성 바닥)로. 바뀐 게 있으면 true(효과음 15).
    public static bool UpdateSwitches(FarlandRules.Data d, IStageMap m)
    {
        bool changed = false;
        foreach (var e in d.stageAI)
        {
            if (e.stage != 13 || e.switches == null) continue;
            foreach (var w in e.switches)
            {
                var plate = P(w.plateX, w.plateY); var door = P(w.x, w.y);
                if (m.Chip(plate) != 0x48) continue;
                if (m.At(plate) != null)
                {
                    if (m.Chip(door) != 0x24 && m.At(door) == null) { m.SetChip(door, 0x24); changed = true; }
                }
                else if (m.Chip(door) != 0x6E) { m.SetChip(door, 0x6E); changed = true; }
            }
        }
        return changed;
    }

    // ---- 이동·턴 시작 트리거 (훅 1·2) ----
    /// 아군 턴 시작(훅 1). 2스테이지: 4번째 동료(고달)가 합류해 있으면 다리를 놓는다.
    /// 8스테이지: 매복을 건드렸으면 모든 ai 16 적을 9(아크 추적)로.
    public static void OnTurnStart(int stage, IStageMap m, State st, bool godalJoined)
    {
        if (stage == 2 && godalJoined && m.Chip(P(34, 20)) != 0x6C)
        {
            m.SetChip(P(33, 20), 0x0C);
            m.SetChip(P(34, 20), 0x6C);
        }
        if (stage == 8 && st.flag == 1)
        {
            st.flag = 2;                                                                  // 대사 0x32 (triggerUnit)
            foreach (var o in m.Units) if (!o.IsPlayerSide && o.stats.hp > 0 && o.stats.ai == 16) o.stats.ai = 9;
        }
    }

    /// 유닛 이동 직후(훅 2).
    public static void OnUnitMoved(int stage, FarlandAI.Unit u, IStageMap m, State st)
    {
        switch (stage)
        {
            case 8:                                                                       // 아군이 매복 4칸 안
                if (st.flag == 0 && u.IsPlayerSide)
                    foreach (var o in m.Units)
                        if (!o.IsPlayerSide && o.stats.hp > 0 && o.stats.ai == 16 && FarlandRules.HexDistance(u.pos, o.pos) < 4)
                        { st.flag = 1; st.triggerUnit = u; break; }
                break;
            case 11:                                                                      // 스위치 두 곳에 아군 → 함정 연출
                if (st.flag == 0 && (u.pos == P(8, 38) || u.pos == P(35, 38)))
                {
                    var other = m.At(u.pos == P(8, 38) ? P(35, 38) : P(8, 38));     // 0x1118 − 좌표 = 반대쪽 스위치
                    if (other == null || !other.IsPlayerSide)
                    {
                        if (!st.switchTalked) { st.switchTalked = true; st.talks.Add(("A", new[] { u })); }   // '이 스위치를 밟으면…'
                        break;
                    }
                    st.flag = 1;
                    var enemy = m.At(P(34, 40));
                    st.talks.Add(("B", new[] { other }));
                    st.talks.Add(("C", new[] { u }));
                    st.talks.Add(("D", new[] { u, enemy }));
                    st.talks.Add(("E", new[] { u }));
                    st.talks.Add(("F", new[] { u }));
                    st.talks.Add(("G", new[] { other }));
                    st.talks.Add(("O", new[] { other }));
                    st.talks.Add(("H", new[] { u }));
                    // 대사 0x41 → 39행 벽이 열림 → 대사 0x42·0x43
                    m.SetChip(P(6, 39), 0x6F); m.SetChip(P(33, 39), 0x6F);
                    for (int i = 0; i < 4; i++) { m.SetChip(P(7 + i, 39), 0x70); m.SetChip(P(34 + i, 39), 0x70); }
                    foreach (var o in m.Units)
                        if (!o.IsPlayerSide && o.stats.hp > 0 && (o.stats.ai == 16 || o.stats.ai == 5)) o.stats.ai = 7;
                    // (34,40) 적 대사 0x44 → 반대쪽 아군이 (22,47)로 떨어짐(0x45~0x47) → 첫 고렘이 공격력 999로 한 번 공격(0x4F)
                    st.trapped = other;
                    other.pos = P(22, 47);
                    // 통로: 39·41·43행 x=14 벽, x=30 바닥 / 40·42·44행 x=13·14, 29·30 바닥
                    for (int k = 0; k < 3; k++)
                    {
                        m.SetChip(P(14, 39 + 2 * k), 0x4A); m.SetChip(P(13, 40 + 2 * k), 0x70); m.SetChip(P(14, 40 + 2 * k), 0x70);
                        m.SetChip(P(30, 39 + 2 * k), 0x6E); m.SetChip(P(29, 40 + 2 * k), 0x70); m.SetChip(P(30, 40 + 2 * k), 0x70);
                    }
                }
                break;
        }
    }

    // ---- 음악 (0xE7E2, 번호 n = CD 트랙 n+1) ----
    /// 스테이지 기본 곡 (DS:0x1516)
    public static int StageMusic(int[] stageBgm, int stage) => stageBgm[stage - 1];

    /// 보스가 처음 공격받아 ai 2가 될 때 곡: 2·9·11·12·13과 7스테이지 삿큐버스(25)는 6, 나머지 7. 보스(ai 2) 격파 시 16
    public static int BossMusic(int stage, int bossClass) =>
        stage == 2 || stage == 9 || stage == 11 || stage == 12 || stage == 13 || (stage == 7 && bossClass == 25) ? 6 : 7;
    public const int VictoryMusic = 16, Stage13Phase2Music = 17, EndingMusic = 12;

    // ---- 교전 대사로 AI 전환 (훅 5: 공격자, 대상 — 0xB0B4·0xE348) ----
    /// 1·3·5·8·10·12스테이지: ai 1 보스가 처음 교전하면 대사 후 2. 7스테이지: 삿큐버스 ai 15 → 2. 13스테이지: 다크 프린세스 → 2.
    public static void OnBattle(int stage, FarlandAI.Unit defender)
    {
        var s = defender.stats;
        if (s.ai == 1 && (stage == 1 || stage == 3 || stage == 5 || stage == 8 || stage == 10 || stage == 12)) s.ai = 2;
        else if (stage == 7 && s.cls == 25 && s.ai == 15) s.ai = 2;
        else if (stage == 13 && s.cls == 39 && s.ai != 2) s.ai = 2;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

// 원작 FSMAIN.EXE 적 턴(0xAA98) 포팅. 좌표는 원작 (x, y) 헥스 even-r 오프셋.
// 적 턴(0xB313): 슬롯 18~61 순서로, 비었거나 HP 0이거나 ai 5이거나 이미 행동한 유닛은 건너뛴다.
// 스테이지 훅 4(FarlandStageAI)가 유닛마다 경유지를 정해 PlanTurn 을 부르고, 대본이 경유지·AI 값을 바꾼다.
public static class FarlandAI
{
    public interface IBoard
    {
        int Width { get; }
        int Height { get; }
        int Terrain(Vector2Int p);                 // 0~9 (FarlandRules.ChipTerrain)
        Unit At(Vector2Int p);                     // 칸의 유닛, 없으면 null
        IEnumerable<Unit> Units { get; }
    }

    public class Unit
    {
        public Vector2Int pos;
        public FarlandRules.Stats stats;
        public bool IsPlayerSide => stats.cls < 18;
    }

    public class Plan
    {
        public Vector2Int moveTo;
        public Unit attackTarget;                  // null 이면 공격 없음
        public int mode;                           // 0 직접, 1 간접
        public bool summon;                        // ai 6/15/18: 칠 대상이 없음 → Summons() 로 소환 시도(0x9F8F)
    }

    const int Search = 20;                         // 탐색 창 ±20, 후보 최대 9 (원작 그대로)
    const int MaxCandidates = 9;

    /// 이 유닛이 적 턴에 행동하는가 (0xB313). ai 5 는 휴면
    public static bool Acts(Unit u) => u.stats.hp > 0 && u.stats.ai != 5;

    /// 흑기사·다크 프린세스·다크 퀸·마왕(38~41)은 한 턴에 두 번 행동 (0xB38A)
    public static bool ActsTwice(Unit u) => u.stats.cls >= 38 && u.stats.cls <= 41;

    // ---- 이동 범위 (0x9599): 이동력 20, 칸 진입 비용 = 클래스×지형 표 ----
    /// 도달 가능한 칸 → 남은 이동력.
    /// - 다른 유닛(같은 편 포함)이 있는 칸은 들어가지도 지나가지도 못한다.
    /// - 지배 영역(0x9282): 출발 칸이 아닌 칸이 살아 있는 상대편 유닛과 이웃하면 그 칸에서 멈춘다(더 나아가지 못함).
    public static Dictionary<Vector2Int, int> Reach(FarlandRules.Data d, IBoard b, Unit u)
    {
        var left = new Dictionary<Vector2Int, int> { [u.pos] = FarlandRules.MovePoints };
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(u.pos);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (p != u.pos && InZoc(b, u, p)) continue;
            for (int dir = 0; dir < 6; dir++)
            {
                var n = FarlandRules.Neighbor(p, dir);
                if (n.x < 0 || n.y < 0 || n.x >= b.Width || n.y >= b.Height) continue;
                int cost = FarlandRules.MoveCost(d, u.stats.cls, b.Terrain(n));
                int rest = left[p] - cost;
                if (cost >= FarlandRules.Impassable || rest < 0 || b.At(n) != null) continue;
                if (left.TryGetValue(n, out int old) && old >= rest) continue;
                left[n] = rest;
                queue.Enqueue(n);
            }
        }
        return left;
    }

    // ---- 공격 위치 (0xA171) ----
    /// 목표를 칠 수 있는 칸. 방식별로 목표 주위 링(반경 = 사거리)에서 피해 배율이 가장 낮은 지형,
    /// 동률이면 남은 이동력이 많은 칸. 방식 선택은 반격을 피하는 쪽 우선.
    public static bool AttackCell(FarlandRules.Data d, IBoard b, Unit u, Unit target,
                                  Dictionary<Vector2Int, int> reach, out Vector2Int cell, out int mode)
    {
        var best = new[] { 999, 999 };
        var bestRest = new[] { -1, -1 };
        var pos = new Vector2Int[2];
        var weapon = Item(d, u.stats.item);
        for (int m = 0; m < 2; m++)
        {
            if (!FarlandRules.CanAttack(weapon, m)) continue;
            foreach (var c in Ring(target.pos, FarlandRules.RangeOf(u.stats.cls, m)))
            {
                if (!reach.TryGetValue(c, out int rest) || b.Terrain(c) == 0) continue;
                if (b.At(c) != null && c != u.pos) continue;
                int score = d.terrains[b.Terrain(c)].damagePercent;
                if (score < best[m] || (score == best[m] && rest > bestRest[m]))
                { best[m] = score; bestRest[m] = rest; pos[m] = c; }
            }
        }
        cell = default; mode = 0;
        if (best[0] == 999 && best[1] == 999) return false;
        var tw = Item(d, target.stats.item);
        if (best[0] != 999 && !FarlandRules.CanAttack(tw, 0)) mode = 0;                 // 반격 못 받는 직접
        else if (best[0] == 999 || best[1] < best[0]) mode = 1;                          // 직접 불가 또는 간접 쪽 지형이 더 좋음
        else if (best[1] != 999 && !FarlandRules.CanAttack(tw, 1)) mode = 1;             // 반격 못 받는 간접
        else mode = 0;
        cell = pos[mode];
        return true;
    }

    // ---- 제자리 공격 (0xA39E) ----
    /// 이동 없이 사거리 링 안의 아군 중 피해 배율이 가장 높은 지형에 선 대상(동률이면 50%).
    public static Unit InPlaceTarget(FarlandRules.Data d, IBoard b, Unit u, System.Random rng, out int mode)
    {
        Unit pick = null; int pickScore = -1; mode = 0;
        var weapon = Item(d, u.stats.item);
        for (int m = 0; m < 2; m++)
        {
            if (!FarlandRules.CanAttack(weapon, m)) continue;
            foreach (var c in Ring(u.pos, FarlandRules.RangeOf(u.stats.cls, m)))
            {
                var t = Inside(b, c) ? b.At(c) : null;
                if (t == null || !t.IsPlayerSide || t.stats.hp <= 0) continue;
                int score = d.terrains[b.Terrain(c)].damagePercent;
                if (score > pickScore || (score == pickScore && rng.Next(2) == 1))
                { pick = t; pickScore = score; mode = m; }
            }
        }
        return pick;
    }

    // ---- 한 유닛의 턴 (0xAA98) ----
    /// waypoint: 스테이지 대본이 준 행군 목표(없으면 null). 반환값대로 이동 후 공격한다.
    public static Plan PlanTurn(FarlandRules.Data d, IBoard b, Unit u, Vector2Int? waypoint, System.Random rng)
    {
        var plan = new Plan { moveTo = u.pos };
        int ai = u.stats.ai;

        // 16 매복: 아군이 5칸 안이면 7(접근)로 바뀐다. 그 전에는 아무것도 하지 않는다.
        if (ai == 16)
        {
            foreach (var o in b.Units)
                if (o.IsPlayerSide && FarlandRules.HexDistance(o.pos, u.pos) < 5) { u.stats.ai = ai = 7; break; }
            if (ai == 16) return plan;
        }

        // 1·2·3·6·15·18: 이동하지 않고 제자리 공격. 대상이 없으면 6·15·18은 소환 시도
        if (ai == 1 || ai == 2 || ai == 3 || ai == 6 || ai == 15 || ai == 18)
        {
            plan.attackTarget = InPlaceTarget(d, b, u, rng, out plan.mode);
            plan.summon = plan.attackTarget == null && (ai == 6 || ai == 15 || ai == 18);
            return plan;
        }

        // 17: 20칸 안의 첫 아군을, 9: 아크(유닛 0)를 목표로
        if (ai == 17)
            foreach (var o in b.Units)
                if (o.IsPlayerSide && FarlandRules.HexDistance(o.pos, u.pos) < 20) { waypoint = o.pos; break; }
        if (ai == 9)
            foreach (var o in b.Units)
                if (o.IsPlayerSide && o.stats.nameId == 1) { waypoint = o.pos; break; }

        var reach = Reach(d, b, u);

        // 경유지 접근 (0xA58C): 목표와 헥스 거리가 가장 짧은 도달 칸.
        // 동률이면 이동 비용이 싼 지형, |x-y| 작은 칸, 남은 이동력 많은 칸 순 (원작 비교 그대로)
        var candidates = new List<(Vector2Int pos, Unit unit, int terrain)>();
        if (waypoint.HasValue)
        {
            int bestD = FarlandRules.HexDistance(u.pos, waypoint.Value), bestCost = 99, bestXY = 99, bestRest = -1;
            foreach (var kv in reach)
            {
                var c = kv.Key;
                if (b.Terrain(c) == 0 || (b.At(c) != null && c != u.pos)) continue;
                int dist = FarlandRules.HexDistance(c, waypoint.Value);
                int cost = FarlandRules.MoveCost(d, u.stats.cls, b.Terrain(c));
                int xy = Math.Abs(c.x - c.y);
                bool better = dist < bestD || (dist == bestD && (cost < bestCost ||
                              (cost == bestCost && (xy < bestXY || (xy == bestXY && kv.Value > bestRest)))));
                if (better) { bestD = dist; bestCost = cost; bestXY = xy; bestRest = kv.Value; plan.moveTo = c; }
            }
        }

        // 후보 수집: 탐색 창(±20)을 위→아래, 왼→오른 순으로 훑어 마을(지형 5)과 유닛이 있는 도달 칸, 최대 9개
        for (int y = u.pos.y - Search; y < u.pos.y + Search && candidates.Count < MaxCandidates; y++)
            for (int x = u.pos.x - Search; x < u.pos.x + Search && candidates.Count < MaxCandidates; x++)
            {
                var c = new Vector2Int(x, y);
                if (!Inside(b, c)) continue;
                var o = b.At(c);
                // 원작은 이동 탐색이 표시한 칸만 본다. 유닛이 선 칸은 이웃이 도달 가능하면 표시된 것으로 본다(근사)
                bool reachable = reach.ContainsKey(c) || (o != null && NextToReach(c, reach));
                if (!reachable) continue;
                int t = b.Terrain(c);
                if (t == 5 || (o != null && o.IsPlayerSide && o.stats.hp > 0) ||
                    (u.stats.cls == 21 && o != null && !o.IsPlayerSide && o.stats.hp < 20 && o != u))
                    candidates.Add((c, o, t));
            }

        // 4: 보이는 아군에게서 가장 먼 도달 칸으로 (도주)
        if (ai == 4 && candidates.Exists(c => c.unit != null && c.unit.IsPlayerSide))
        {
            int far = -1;
            foreach (var kv in reach)
                foreach (var c in candidates)
                    if (c.unit != null && c.unit.IsPlayerSide && b.At(kv.Key) == null)
                    {
                        int dist = FarlandRules.HexDistance(c.pos, kv.Key);
                        if (dist > far) { far = dist; plan.moveTo = kv.Key; }
                    }
        }

        // HP 20 미만: 후보 중 빈 마을로 (회복)
        if (u.stats.hp < 20)
            foreach (var c in candidates)
                if (c.terrain == 5 && c.unit == null) { plan.moveTo = c.pos; break; }

        // 페어리(21): HP 20 미만인 같은 편을 치료하러 감
        if (u.stats.cls == 21)
            foreach (var c in candidates)
                if (c.unit != null && !c.unit.IsPlayerSide && AttackCell(d, b, u, c.unit, reach, out var cell, out var m))
                { plan.moveTo = cell; plan.attackTarget = c.unit; plan.mode = m; return plan; }

        // 공격 대상: 공격 위치가 있는 아군 중 가장 가까운 대상, 동률이면 피해 배율 높은 지형에 선 대상
        Unit best = null; int bestDist = 99, bestPct = -1;
        foreach (var c in candidates)
        {
            if (c.unit == null || !c.unit.IsPlayerSide || c.unit.stats.hp <= 0) continue;
            if (!AttackCell(d, b, u, c.unit, reach, out _, out _)) continue;
            int dist = FarlandRules.HexDistance(u.pos, c.pos), pct = d.terrains[c.terrain].damagePercent;
            if (dist < bestDist || (dist == bestDist && pct > bestPct)) { best = c.unit; bestDist = dist; bestPct = pct; }
        }
        if (best != null && AttackCell(d, b, u, best, reach, out var at, out var mode))
        {
            plan.moveTo = at; plan.attackTarget = best; plan.mode = mode;
            return plan;
        }

        // 이동 뒤 제자리 공격 (0xB017)
        var saved = u.pos; u.pos = plan.moveTo;
        plan.attackTarget = InPlaceTarget(d, b, u, rng, out plan.mode);
        u.pos = saved;
        return plan;
    }

    // ---- 소환 (0x9F8F) ----
    /// 3번 시도, 각각 25% 확률: 소환 표(DS:0x1C86, 클래스당 6칸)에서 무작위 한 칸(0 = 실패),
    /// 무작위 방향의 이웃 칸이 그 클래스로 진입 가능하고 비어 있으면 소환. ai 15·18은 20칸 안에 아군이 있어야 한다(0x9F4C).
    /// 반환된 클래스는 FarlandStageAI.SummonTemplate 으로 스테이지 템플릿을 찾아 생성하고, 생성된 유닛은 이번 턴 행동 완료.
    public static List<(int cls, Vector2Int pos)> Summons(FarlandRules.Data d, IBoard b, Unit u, System.Random rng)
    {
        var made = new List<(int, Vector2Int)>();
        int ai = u.stats.ai;
        if (ai == 15 || ai == 18)
        {
            bool near = false;
            foreach (var o in b.Units)
                if (o.IsPlayerSide && FarlandRules.HexDistance(o.pos, u.pos) < 20) { near = true; break; }
            if (!near) return made;
        }
        int[] table = null;
        foreach (var s in d.summonTable) if (s.summoner == u.stats.cls) table = s.summons;
        for (int k = 0; k < 3; k++)
        {
            if ((rng.Next(0x10000) & 3) != 0) continue;
            int cls = table != null ? table[rng.Next(0x10000) % 6] : 0;
            if (cls == 0) continue;
            var p = FarlandRules.Neighbor(u.pos, rng.Next(0x10000) % 6);
            if (!Inside(b, p) || FarlandRules.MoveCost(d, cls, b.Terrain(p)) >= FarlandRules.Impassable) continue;
            if (b.At(p) != null || made.Exists(m => m.Item2 == p)) continue;
            made.Add((cls, p));
        }
        return made;
    }

    // ---- 보조 ----
    static bool InZoc(IBoard b, Unit u, Vector2Int p)
    {
        for (int dir = 0; dir < 6; dir++)
        {
            var n = FarlandRules.Neighbor(p, dir);
            var o = Inside(b, n) ? b.At(n) : null;
            if (o != null && o.stats.hp > 0 && o.IsPlayerSide != u.IsPlayerSide) return true;
        }
        return false;
    }

    static FarlandRules.Item Item(FarlandRules.Data d, int id) => id > 0 && id <= d.items.Length ? d.items[id - 1]
        : new FarlandRules.Item { physical = new[] { 0, 0 }, magic = new[] { 0, 0 } };

    static bool NextToReach(Vector2Int c, Dictionary<Vector2Int, int> reach)
    {
        for (int dir = 0; dir < 6; dir++) if (reach.ContainsKey(FarlandRules.Neighbor(c, dir))) return true;
        return false;
    }

    static bool Inside(IBoard b, Vector2Int p) => p.x >= 0 && p.y >= 0 && p.x < b.Width && p.y < b.Height;

    static readonly int[] RingDirs = { 1, 2, 3, 4, 5, 0 };   // DS:0x1E5A

    /// 반경 r 링 (원작 0xA1D2: 방향 5로 r칸 간 뒤 방향 1,2,3,4,5,0 으로 각 r칸씩 돈다)
    public static IEnumerable<Vector2Int> Ring(Vector2Int center, int r)
    {
        var p = center;
        for (int i = 0; i < r; i++) p = FarlandRules.Neighbor(p, 5);
        foreach (int dir in RingDirs)
            for (int i = 0; i < r; i++) { p = FarlandRules.Neighbor(p, dir); yield return p; }
    }
}

using System;
using UnityEngine;

// 원작 FSMAIN.EXE 전투 규칙 포팅. 데이터는 Battle/battle_data.json.
// 주소는 원본 코드 위치(분석 근거). 확인하지 못한 부분은 '미확인'으로 표시했다.
public static class FarlandRules
{
    // ---- 데이터 (battle_data.json) ----
    [Serializable] public class ClassInfo { public int id; public string name; public int[] moveCost; public bool female; public int[] growth; }
    [Serializable] public class Terrain { public int id; public string name; public int damagePercent; }
    [Serializable] public class Item { public int id; public string name; public int price; public int[] physical; public int[] magic; public int flags; public string weakenStat; public int equipMask; }
    [Serializable] public class Summon { public int summoner; public int[] summons; }
    [Serializable] public class Spawn : Stats { public int x, y; }
    [Serializable] public class Switch { public int plateX, plateY, x, y; }
    [Serializable] public class StageAI { public int stage; public Stats[] summonTemplates; public Spawn[] reinforcements; public Switch[] switches; }
    [Serializable] public class Data
    {
        public ClassInfo[] classes; public Terrain[] terrains; public Item[] items;
        public int[] chipTerrainMAPCHIP, chipTerrainMAPCHIP2;
        public Summon[] summonTable; public StageAI[] stageAI;
    }

    // 유닛 능력치 = 원본 템플릿 26바이트(유닛 +0x0A~)와 같은 순서
    [Serializable] public class Stats
    {
        public int nameId, cls, level, maxHp, hp, exp, attack, defense, knowledge, magicDefense, agility, ai, item;
        public Stats Clone() => (Stats)MemberwiseClone();
    }

    public const int MovePoints = 20;          // 0x9816: 모든 유닛 이동력 20
    public const int Impassable = 99;
    public const int MaxLevel = 20, MaxExp = 2000, MaxHp = 150, MaxStat = 45;
    public const int ItemSpecialSword = 0x19, ItemBurningRod = 0x23, ItemMedusaKiller = 0x42;

    public static Data Load(TextAsset json) => Load(json.text);
    public static Data Load(string json) => JsonUtility.FromJson<Data>(json);

    // ---- 헥스 격자 (0x9A5A) ----
    // 짝수 행이 반 칸 오른쪽으로 밀린 'even-r' 오프셋. 방향 0~5 (원작 방향 번호 그대로)
    static readonly int[,] EvenRow = { { 1, -1 }, { 1, 0 }, { 1, 1 }, { 0, 1 }, { -1, 0 }, { 0, -1 } };
    static readonly int[,] OddRow = { { 0, -1 }, { 1, 0 }, { 0, 1 }, { -1, 1 }, { -1, 0 }, { -1, -1 } };

    public static Vector2Int Neighbor(Vector2Int p, int dir)
    {
        var t = (p.y & 1) == 0 ? EvenRow : OddRow;
        return new Vector2Int(p.x + t[dir, 0], p.y + t[dir, 1]);
    }

    public static int HexDistance(Vector2Int a, Vector2Int b)
    {
        int aq = a.x - (a.y + (a.y & 1)) / 2, bq = b.x - (b.y + (b.y & 1)) / 2;
        int dq = aq - bq, dr = a.y - b.y;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    // ---- 사거리·반격 (0xA171, 0xA39E, 0x1875C) ----
    /// 공격 방식(0=직접, 1=간접)별 사거리: 직접은 거리 1, 간접은 정확히 거리 2. 마왕(41)은 각각 +1.
    public static int RangeOf(int cls, int mode) => mode + 1 + (cls == 41 ? 1 : 0);

    /// 이 무기로 해당 방식 공격이 가능한가 (아이템 플래그 1=직접, 2=간접)
    public static bool CanAttack(Item weapon, int mode) => (weapon.flags & (mode == 0 ? 1 : 2)) != 0;

    /// 반격: 방어측 무기가 같은 방식을 지원해야 한다. 같은 편 대상 행동(비숍·클레릭 회복, 프리스트 회복·간접,
    /// 버드 노래, 페어리 회복)에는 반격이 없다.
    public static bool CanCounter(Data d, Stats attacker, Stats defender, int mode)
    {
        bool sameSide = (attacker.cls < 18) == (defender.cls < 18);
        int c = attacker.cls;
        if (sameSide && (c == 6 || c == 7 || c == 8 || c == 17 || c == 21)) return false;
        if (c == 8 && mode == 1) return false;
        return CanAttack(ItemOf(d, defender.item), mode);
    }

    // ---- 2연타 (0x19425·0x1C06F) ----
    /// 로드+성검, 위저드·소사라(33 포함)+버닝 롯은 공격 계산·적용을 두 번 한다(크리티컬도 각각).
    public static int HitCount(Stats attacker) =>
        (attacker.cls == 0 && attacker.item == ItemSpecialSword) ||
        ((attacker.cls == 3 || attacker.cls == 5 || attacker.cls == 33) && attacker.item == ItemBurningRod) ? 2 : 1;

    /// 결과 메시지 'N 의 DAMAGE' 숫자(0x17C22). 2연타면 클래스 0·3·5는 첫 피해 + 둘째 피해/2 로 표시(실제 HP 감소는 합계).
    public static int DisplayedDamage(Stats attacker, int first, int second) =>
        HitCount(attacker) == 2 && (attacker.cls == 0 || attacker.cls == 3 || attacker.cls == 5) ? first + second / 2
        : HitCount(attacker) == 2 ? second : first;

    // ---- 지형·이동 ----
    /// mapchip2: STAGE14 만 true (FSMAIN 0x811A)
    public static int ChipTerrain(Data d, int chip, bool mapchip2) => (mapchip2 ? d.chipTerrainMAPCHIP2 : d.chipTerrainMAPCHIP)[chip];

    /// 이동 비용 (DS:0x0E72). 이동력 20에서 칸마다 차감, 99는 진입 불가.
    public static int MoveCost(Data d, int cls, int terrain) => d.classes[cls].moveCost[terrain];

    // ---- 장비 (0xB9DA) ----
    public static bool CanEquip(Item it, int cls)
    {
        if (it.equipMask == 0x8000) return true;
        int b = cls;
        if ((it.equipMask & 0x4000) == 0) { if (b < 9) return false; b -= 9; }
        else if (b >= 9) return false;
        return (it.equipMask >> b & 1) != 0;
    }

    public static bool IsFemale(int cls) { int c = cls / 3 * 3; return c == 6 || c == 9 || c == 15; }

    // ---- 공격 (0x17E01 → 0x2851E) ----
    public struct Hit { public int damage; public bool critical; }

    /// mode 0 = 직접(인접), 1 = 간접(원거리). defenderTerrain은 방어측 칸의 지형(0은 1로 취급, 0xB15F).
    /// 원작처럼 정령 무기 공격은 방어측 능력치를 깎으므로 def가 수정될 수 있다.
    public static Hit Attack(Data d, Stats atk, Stats def, int mode, int defenderTerrain, System.Random rng,
                             bool petrifyGaze = false)
    {
        var wa = ItemOf(d, atk.item);
        // 기본값: 무기 위력이 0이면 그 계열 피해는 0
        int p = wa.physical[mode] == 0 ? 0 : Math.Max(0, atk.attack + wa.physical[mode] - def.defense);
        int m = wa.magic[mode] == 0 ? 0 : Math.Max(0, atk.knowledge + wa.magic[mode] - def.magicDefense);
        if ((wa.flags & 0x08) != 0) m = 0;

        Matchup(d, atk, def, mode, ref p, ref m, rng);
        if (p < 0) p = 0;
        if (m < 0) m = 0;

        int t = defenderTerrain == 0 ? 1 : defenderTerrain;
        int dmg = (p + m) * d.terrains[t].damagePercent / 100;

        // 크리티컬: 민첩 차(최대 80) × 3 % 확률로 3배 (0x28863)
        var hit = new Hit();
        int diff = Math.Min(80, atk.agility - def.agility);
        if (diff > 0 && rng.Next(100) < diff * 3) { dmg *= 3; hit.critical = true; }

        // 메두사 석화(0x288A2): 공격측이 메두사(27)·AI 2 이고 연출 상태가 '시선'(전투 기록 +0x26 == 7)일 때,
        // 방어측이 메두사 킬러를 들지 않았으면 피해 = 남은 HP. 연출 상태는 전투 연출 쪽에서 판단해 petrifyGaze 로 넘긴다.
        if (petrifyGaze && atk.cls == 27 && atk.ai == 2 && def.item != ItemMedusaKiller) dmg = def.hp;
        hit.damage = Math.Min(dmg, def.hp);   // 0이면 빗나감 효과음(0x288C9)
        return hit;
    }

    static Item ItemOf(Data d, int id) => id > 0 && id <= d.items.Length ? d.items[id - 1]
        : new Item { physical = new[] { 0, 0 }, magic = new[] { 0, 0 } };

    // 클래스 상성 보정. 0x2851E 의 두 switch (방어측 클래스 → 공격측 클래스 순)
    static void Matchup(Data d, Stats a, Stats t, int mode, ref int p, ref int m, System.Random rng)
    {
        int ac = a.cls, dc = t.cls, wa = a.item, wd = t.item;
        bool atkElf = ac == 10 || ac == 11 || ac == 34 || ac == 35;   // 옐후, 엘리멘탈 마스터
        bool atkDwarf = ac >= 12 && ac <= 14;                          // 바사카, 드와후, 스팔탄
        bool bow = wa >= 7 && wa <= 10;
        switch (dc)
        {
            case 2: case 31:                                           // 나이트: 엘프 활 피해를 방어력만큼 더 감소, 드워프계 1.5배
                if (atkElf && bow) p -= t.defense;
                else if (atkDwarf) p += p / 2;
                break;
            case 3: case 4: case 5: case 10: case 11:
            case 32: case 33: case 34: case 35:                        // 마법사·엘프: 마법을 마법방어/2 만큼 더 감소
                m -= t.magicDefense / 2; break;
            case 12: case 13: case 14: m += m; break;                  // 드워프계: 마법 2배
            case 15: case 16: case 17: case 21: case 28:               // 비행: 엘프 활 2배
                if (atkElf && bow) p *= 2; break;
            case 19:                                                   // 고스트: 성직자의 물리 = 공격력×16
                if (ac >= 6 && ac <= 8) p = a.attack << 4; break;
            case 23:                                                   // 스켈톤: 마법 2배, 성직자 물리 = (공격 - 방어/4)×8
                m *= 2;
                if (ac >= 6 && ac <= 8) p = (a.attack - t.defense / 4) << 3;
                break;
            case 25:                                                   // 삿큐버스: 남성 클래스(0~5,12~14)의 공격 1/3
                if (ac <= 14 && !(ac >= 6 && ac <= 11)) { p /= 3; m /= 3; }
                break;
            case 27: if (wa != ItemMedusaKiller) p /= 3; break;         // 메두사: 메두사 킬러 외 물리 1/3
            case 22: case 37: case 39: case 40: p /= 2; m /= 2; break;  // 데몬·드래곤·다크 프린세스·다크 퀸
            case 38: m /= 2; break;                                    // 흑기사: 마법 1/2
            case 41: p = p * 2 / 3; m = m * 2 / 3; break;              // 마왕: 2/3
        }
        switch (ac)
        {
            case 6: case 7: case 8: m = 0; break;                      // 성직자는 마법 피해 없음(회복 담당)
            case 19: case 23:                                          // 고스트·스켈톤 → 성직자 상대로 물리 1/3, 마법 0
                if (dc >= 6 && dc <= 8) { p /= 3; m = 0; }
                break;
            case 9: case 11:                                           // 하이옐후·엘리멘탈 마스터의 정령 무기(간접)
            {
                var w = ItemOf(d, wa);
                if (mode == 0 || (w.flags & 0x20) == 0) break;
                int r = rng.Next(8) + 1;
                switch (w.flags & 0x700)
                {
                    case 0x100: t.defense = Math.Max(0, t.defense - r); break;
                    case 0x200: t.knowledge = Math.Max(0, t.knowledge - r); break;
                    case 0x300: t.magicDefense = Math.Max(0, t.magicDefense - r); break;
                    case 0x400: t.attack = Math.Max(0, t.attack - r); break;
                }
                if (t.ai == 1 || t.ai == 2 || t.ai == 15) m /= 2; else m *= 2;
                break;
            }
            case 12: case 13: case 14:                                 // 드워프계 직접 공격은 물리 한 번 더
                if (mode == 0) p += a.attack - t.defense; break;
            case 25:                                                   // 삿큐버스 → 남성 클래스 물리 2배
                if (dc <= 14 && !(dc >= 6 && dc <= 11)) p *= 2; break;
            case 41: if (wa == ItemSpecialSword) p += p / 2; break;    // 마왕: 특정 무기 1.5배
        }
    }

    // ---- 아이템 사용 (0xC44B 특수, 0xC641 회복, 0xC70F 전직) ----
    public const int ItemElixir = 23, ItemWhisky = 62, ItemPill = 65, ClassChangeLevel = 10;

    /// 회복 아이템(플래그 8) 회복량. 최대HP를 넘지 않게 적용할 것
    public static int HealAmount(Item it) =>
        it.id == ItemElixir || it.id == ItemPill ? 999 : it.id == ItemWhisky ? 20 : it.price / 2;

    /// 특수 아이템(플래그 4). 사용되면 true. 금 변화는 goldDelta 로 돌려준다(빨간 날개).
    public static bool UseSpecial(Item it, Stats u, ref int status, out int goldDelta, System.Random rng)
    {
        goldDelta = 0;
        switch (it.id)
        {
            case 38:                                    // 사랑의 꿈: 상태 0이면 잠들며 HP 완전 회복
                if (status != 0) return false;
                u.hp = u.maxHp; status = 1; return true;
            case 39: goldDelta = -(rng.Next(6) * 100 + 500); return true;   // 빨간 날개 (금이 모자라면 0까지)
            case 49: u.magicDefense = Math.Min(MaxStat, u.magicDefense + 5); return true;
            case 50: u.agility = Math.Min(MaxStat, u.agility + 5); return true;
            case 51: u.knowledge = Math.Min(MaxStat, u.knowledge + 5); return true;
            case 56: u.attack = Math.Min(MaxStat, u.attack + 5); return true;
            case 57: u.defense = Math.Min(MaxStat, u.defense + 5); return true;
            case 68: u.maxHp = Math.Min(MaxHp, u.maxHp + 10); return true;
        }
        return false;                                   // 드카티의 열쇠 등: 사용할 곳 없음
    }

    /// 전직 아이템(플래그 0x40)을 장비했을 때. 레벨 10 미만이면 '레벨이 부족합니다'
    public static bool TryClassChange(Item it, Stats u)
    {
        if ((it.flags & 0x40) == 0 || u.level < ClassChangeLevel) return false;
        u.cls++; u.level = 1; u.exp = 0;
        switch (u.cls)
        {
            case 2: case 14: u.attack += 8; u.defense += 8; break;           // 나이트, 스팔탄
            case 5: case 17: u.knowledge += 8; u.magicDefense += 8; break;   // 소사라, 버드
            case 8: u.knowledge += 4; u.magicDefense += 4; break;            // 프리스트
            case 11: u.attack += 8; u.defense += 8; u.knowledge += 8; u.magicDefense += 8; break; // 엘리멘탈 마스터
        }
        u.attack = Math.Min(MaxStat, u.attack); u.defense = Math.Min(MaxStat, u.defense);
        u.knowledge = Math.Min(MaxStat, u.knowledge); u.magicDefense = Math.Min(MaxStat, u.magicDefense);
        return true;
    }

    // ---- 경험치·레벨 (0x28171, 0x27D02, 0x17352) ----
    /// 공격 후 경험치. 처치하면 적 템플릿 exp, 아니면 준 피해(적에게만). 레벨 20 이상은 없음.
    /// 돌려준 값만큼 exp가 늘고, 100의 배수를 넘었으면 true(레벨업 1회).
    public static bool GainExp(Stats unit, Stats enemy, int damage, bool killed)
    {
        if (unit.level >= MaxLevel) return false;
        int gain = Math.Max(1, killed ? enemy.exp : damage);
        int before = unit.exp % 100;
        unit.exp = Math.Min(MaxExp, unit.exp + gain);
        return before + gain >= 100;
    }

    /// 레벨업: 클래스 성장 확률(%)마다 HP +0~3(상한 150), 나머지 능력치 +1 (클래스 번호가 3의 배수면 +0~2), 상한 45
    public static void LevelUp(Data d, Stats u, System.Random rng)
    {
        var g = d.classes[u.cls].growth;
        u.level++;
        if (rng.Next(100) <= g[0]) u.maxHp = Math.Min(MaxHp, u.maxHp + rng.Next(4));
        u.attack = Grow(u.attack, g[1]); u.defense = Grow(u.defense, g[2]); u.knowledge = Grow(u.knowledge, g[3]);
        u.magicDefense = Grow(u.magicDefense, g[4]); u.agility = Grow(u.agility, g[5]);

        int Grow(int v, int chance)
        {
            if (rng.Next(100) > chance) return v;
            return Math.Min(MaxStat, v + (u.cls % 3 == 0 ? rng.Next(3) : 1));
        }
    }

    /// 아군이 쓰러졌을 때: 레벨 -1, exp -100, 성장 확률마다 각 능력치 -1 (하한 1)
    public static void FaintPenalty(Data d, Stats u, System.Random rng)
    {
        var g = d.classes[u.cls].growth;
        u.level = Math.Max(1, u.level - 1);
        u.exp = Math.Max(0, u.exp - 100);
        if (rng.Next(100) <= g[1]) u.attack = Math.Max(1, u.attack - 1);
        if (rng.Next(100) <= g[2]) u.defense = Math.Max(1, u.defense - 1);
        if (rng.Next(100) <= g[3]) u.knowledge = Math.Max(1, u.knowledge - 1);
        if (rng.Next(100) <= g[4]) u.magicDefense = Math.Max(1, u.magicDefense - 1);
        if (rng.Next(100) <= g[5]) u.agility = Math.Max(1, u.agility - 1);
    }
}

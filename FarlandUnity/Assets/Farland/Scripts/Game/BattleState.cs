using System;
using System.Collections.Generic;
using System.Linq;
using Farland.Content;
using UnityEngine;

namespace Farland.Game
{
    /// 전투 중 유닛. FarlandAI.Unit(위치·능력치)에 슬롯·행동 여부·표시 정보를 더한다.
    public class GameUnit : FarlandAI.Unit, FarlandDialogue.IUnit
    {
        public int slot;                 // 0~17 아군, 18~61 적 (원작 유닛 배열 순서)
        public bool acted, engaged, removed;
        public string displayName;
        public int face = -1;
        public string Name => displayName;
        public int Face => face;
        public bool IsFemale => FarlandRules.IsFemale(stats.cls);
        public bool Alive => !removed && stats.hp > 0;
    }

    /// 스테이지 사이에 이어지는 파티: 동료(슬롯 = 얼굴 번호 = 이름 id - 1), 금, 가방, 명령
    public class Party
    {
        public readonly SortedDictionary<int, FarlandRules.Stats> members = new SortedDictionary<int, FarlandRules.Stats>();
        public int gold;
        public readonly List<int> bag = new List<int>();
        public int commandMode;

        public void Join(FarlandRules.Stats s) { int slot = s.nameId - 1; if (!members.ContainsKey(slot)) members[slot] = s.Clone(); }
    }

    /// 전투에서 일어난 일. 화면 쪽(BattleController)이 받아서 연출한다.
    public abstract class BattleEvent { }
    public class MoveEvent : BattleEvent { public GameUnit unit; public Vector2Int from, to; }
    public class HitEvent : BattleEvent { public GameUnit attacker, target; public int damage; public bool critical, counter; }
    public class MessageEvent : BattleEvent { public string text; }
    public class RemoveEvent : BattleEvent { public GameUnit unit; }
    public class SpawnEvent : BattleEvent { public GameUnit unit; }
    public class TalkEvent : BattleEvent { public string block; public object[] args; public string file; }   // file 이 비면 스테이지 대사
    public class MusicEvent : BattleEvent { public int music; }
    public class SoundEvent : BattleEvent { public int sound; }
    public class TileEvent : BattleEvent { public Vector2Int cell; }
    public class EndEvent : BattleEvent { public bool victory; }
    public class ChestEvent : BattleEvent { public Chest chest; }

    /// 교전 한 번의 기록(전투 연출 화면용). 일격마다 누가 누구를 쳤고 HP가 어떻게 됐는지
    public class CombatEvent : BattleEvent
    {
        public class Strike { public GameUnit attacker, target; public int damage, hpBefore, hpAfter, heal; public bool critical, counter, song; }
        public GameUnit attacker, defender;
        public int mode, defenderTerrain, attackerTerrain;
        public readonly List<Strike> strikes = new List<Strike>();
    }

    /// 한 스테이지의 전투 상태와 규칙 진행. Unity 렌더링에 의존하지 않는다(헤드리스 테스트 가능).
    public sealed class BattleState : FarlandStageScript.IHost
    {
        public readonly FarlandContent content;
        public readonly StageDef def;
        public readonly MapData map;
        public readonly FarlandRules.Data rules;
        public readonly List<GameUnit> units = new List<GameUnit>();
        public readonly FarlandStageAI.State stageState = new FarlandStageAI.State();
        public readonly FarlandRandom rng;
        public int turn = 1;
        public bool over, won;

        // ---- 파티 소지품·금 (원작 DS:0x83BC 48칸 공용 가방, DS:0x52A 금) ----
        public const int BagSize = 48, GoldMax = 60000;
        public readonly List<Chest> chests = new List<Chest>();
        public int commandMode;          // 명령(DS:0x56A): 0 각자 판단, 1 모두 모여라
        public int gold;
        public readonly List<int> bag = new List<int>();
        readonly HashSet<StageEvent> fired = new HashSet<StageEvent>();
        readonly HashSet<string> firedGroups = new HashSet<string>();
        readonly int[] chips;
        public int music;                 // 지금 곡(전투 중 저장용)
        readonly bool mapchip2;

        public readonly Party party;

        public BattleState(FarlandContent content, StageDef def, Party party = null)
        {
            this.party = party;
            this.content = content; this.def = def;
            rules = content.Rules;
            rng = new FarlandRandom(content.Game.rngSeed);
            map = content.LoadMap(def.map);
            chips = (int[])map.tiles.Clone();
            mapchip2 = map.tileset != null && map.tileset.StartsWith("MAPCHIP2");
            gold = party != null ? party.gold : def.startGold;
            if (party != null) { bag.AddRange(party.bag); commandMode = party.commandMode; }
            var cs = content.Placements.chests == null ? null : System.Array.Find(content.Placements.chests, c => c.stage == def.id);
            if (cs?.chests != null)
                foreach (var c in cs.chests) chests.Add(new Chest { state = c.state, x = c.x, y = c.y, gold = c.gold, item = c.item });
            int ally = 0, enemy = 18;
            var placement = content.PlacementOf(def.placement ?? def.id);
            // 기존 동료: 배치의 partyStart(슬롯별 위치)에 놓는다. HP 는 다시 가득(스테이지 사이)
            if (party != null && placement.partyStart != null)
                foreach (var ps in placement.partyStart)
                    if (party.members.TryGetValue(ps.slot, out var m))
                    {
                        var st = m.Clone(); st.hp = st.maxHp; st.ai = 0;
                        AddUnit(st, new Vector2Int(ps.x, ps.y), ps.slot);
                    }
            foreach (var s in placement.units)
            {
                if (party != null && s.cls < 18 && units.Any(u => u.stats.nameId == s.nameId)) continue;   // 이미 파티에 있는 동료
                var st = new FarlandRules.Stats
                {
                    nameId = s.nameId, cls = s.cls, level = s.level, maxHp = s.maxHp, hp = s.hp, exp = s.exp, attack = s.attack,
                    defense = s.defense, knowledge = s.knowledge, magicDefense = s.magicDefense, agility = s.agility, ai = s.ai, item = s.item
                };
                if (st.cls < 18) { while (units.Any(u => u.slot == ally)) ally++; }
                AddUnit(st, new Vector2Int(s.x, s.y), st.cls < 18 ? ally++ : enemy++);       // 원작 0x9B26: 빈 슬롯 순서
            }
        }

        public GameUnit AddUnit(FarlandRules.Stats st, Vector2Int pos, int slot)
        {
            var u = new GameUnit { stats = st, pos = pos, slot = slot, displayName = content.NameOf(st.nameId), face = st.nameId - 1 };
            units.Add(u);
            return u;
        }

        int FreeEnemySlot() { int s = 18; while (units.Any(u => u.slot == s && !u.removed)) s++; return s; }

        // ---- IStageMap ----
        public int Width => map.width;
        public int Height => map.height;
        public bool Inside(Vector2Int p) => p.x >= 0 && p.y >= 0 && p.x < map.width && p.y < map.height;
        public int Chip(Vector2Int p) => Inside(p) ? chips[p.y * map.width + p.x] : 0;
        public void SetChip(Vector2Int p, int chip) { if (Inside(p)) { chips[p.y * map.width + p.x] = chip; pending.Add(new TileEvent { cell = p }); } }
        public int Terrain(Vector2Int p) => Inside(p) ? FarlandRules.ChipTerrain(rules, Chip(p), mapchip2) : 0;
        public FarlandAI.Unit At(Vector2Int p) => UnitAt(p);
        public IEnumerable<FarlandAI.Unit> Units => units.Where(u => u.Alive);
        public GameUnit UnitAt(Vector2Int p) => units.FirstOrDefault(u => u.Alive && u.pos == p);
        public GameUnit BySlot(int slot) => units.FirstOrDefault(u => !u.removed && u.slot == slot);

        // 이번 단계에서 생긴 연출 이벤트
        readonly List<BattleEvent> pending = new List<BattleEvent>();
        public List<BattleEvent> Drain() { var l = new List<BattleEvent>(pending); pending.Clear(); return l; }
        void Emit(BattleEvent e) { if (e is MusicEvent me) music = me.music; pending.Add(e); }
        void Say(string fmt, params object[] a) => Emit(new MessageEvent { text = string.Format(fmt ?? "", a) });

        // ---- 스테이지 이벤트 (Config/stages.json) ----
        object[] Args(StageEvent e, GameUnit attacker = null, GameUnit defender = null, GameUnit mover = null, GameUnit boss = null)
        {
            if (e.args == null) return new object[0];
            return e.args.Select(a =>
            {
                if (a.StartsWith("unit:")) return (object)BySlot(int.Parse(a.Substring(5))) ?? Placeholder();
                if (a.StartsWith("str:")) return a.Substring(4);
                if (a.StartsWith("name:")) return content.NameOf(int.Parse(a.Substring(5)));     // 이름표 문자열(DS:0x2CEE)
                switch (a)
                {
                    case "attacker": return attacker;
                    case "defender": return defender;
                    case "mover": return mover;
                    case "boss": return boss ?? defender;
                }
                return Placeholder();
            }).ToArray();
        }

        static GameUnit Placeholder() => new GameUnit { displayName = "", stats = new FarlandRules.Stats() };

        bool Fire(string trigger, Func<StageEvent, bool> match, GameUnit attacker = null, GameUnit defender = null, GameUnit mover = null, GameUnit boss = null, Vector2Int? place = null)
        {
            bool any = false;
            if (def.events == null) return false;
            foreach (var e in def.events)
            {
                if (e.trigger != trigger || (e.once && fired.Contains(e)) || (match != null && !match(e))) continue;
                if (!string.IsNullOrEmpty(e.group) && firedGroups.Contains(e.group)) continue;
                fired.Add(e); any = true;
                if (!string.IsNullOrEmpty(e.group)) firedGroups.Add(e.group);
                if (e.spawn != null)
                    foreach (var sp in e.spawn)
                    {
                        var at = new Vector2Int(sp.x, sp.y);
                        if (UnitAt(at) != null) continue;
                        var st = sp.Clone(); st.cls = sp.cls;
                        var nu = AddUnit(st, at, st.cls < 18 ? units.Where(o => o.IsPlayerSide).Select(o => o.slot).DefaultIfEmpty(-1).Max() + 1 : FreeEnemySlot());
                        Emit(new SpawnEvent { unit = nu });
                    }
                if (e.setAi >= 0 && defender != null) defender.stats.ai = e.setAi;
                if (e.tiles != null)
                    for (int t = 0; t + 2 < e.tiles.Length; t += 3) SetChip(new Vector2Int(e.tiles[t], e.tiles[t + 1]), e.tiles[t + 2]);
                if (e.modifyAi >= 0)
                    foreach (var o in units.Where(o => o.Alive && !o.IsPlayerSide && o.stats.ai == e.modifyAi))
                    {
                        if (e.setDefense >= 0) o.stats.defense = e.setDefense;
                        if (e.setMagicDefense >= 0) o.stats.magicDefense = e.setMagicDefense;
                    }
                if (e.removeCls >= 0)
                    foreach (var r in units.Where(o => o.Alive && o.stats.cls == e.removeCls).ToList()) { r.removed = true; Emit(new RemoveEvent { unit = r }); }
                if (e.recruit != null) Recruit(e.recruit, place ?? mover?.pos ?? defender?.pos ?? Vector2Int.zero);
                if (e.retreat) Retreat();
                if (e.music > 0) Emit(new MusicEvent { music = e.music });
                if (!string.IsNullOrEmpty(e.talk)) Emit(new TalkEvent { block = e.talk, args = Args(e, attacker, defender, mover, boss) });
                if (e.victory) End(true);
            }
            return any;
        }

        /// 동료 합류(0x8A6 아군 템플릿): 이미 있으면 건너뜀. 자리가 차 있으면 가까운 빈 칸
        void Recruit(int[] nameIds, Vector2Int near)
        {
            var rec = content.Placements.recruits == null ? null : System.Array.Find(content.Placements.recruits, r => r.stage == def.id);
            foreach (var id in nameIds)
            {
                if (units.Any(u => u.IsPlayerSide && u.stats.nameId == id && !u.removed)) continue;
                var tpl = rec?.units == null ? null : System.Array.Find(rec.units, r => r.nameId == id);
                if (tpl == null) continue;
                var pos = FreeCellNear(near);
                var st = tpl.Clone(); st.cls = tpl.cls;
                var nu = AddUnit(st, pos, id - 1);
                nu.acted = true;                                    // 합류한 턴에는 행동하지 않음
                Emit(new SpawnEvent { unit = nu });
            }
        }

        Vector2Int FreeCellNear(Vector2Int p)
        {
            if (Inside(p) && UnitAt(p) == null && Terrain(p) != 0) return p;
            for (int r = 1; r < 6; r++)
                foreach (var c in FarlandAI.Ring(p, r))
                    if (Inside(c) && UnitAt(c) == null && Terrain(c) != 0) return c;
            return p;
        }

        public void Begin()
        {
            if (def.music > 0) Emit(new MusicEvent { music = def.music });
            Fire("start", null);
            FarlandStageScript.TurnStart(def.Number, this);
        }

        // ---- FarlandStageScript.IHost: 원작 스테이지 훅 대본이 쓰는 동작 ----
        public int TurnIndex => turn - 1;
        public int[] Vars => stageState.vars;
        public FarlandStageAI.State StageState => stageState;
        public FarlandAI.Unit Slot(int index) => BySlot(index);
        public bool HasMember(int nameId) => units.Any(u => u.IsPlayerSide && !u.removed && u.stats.cls < 18 && u.stats.nameId == nameId);
        public void Talk(string block, params object[] args) =>
            Emit(new TalkEvent { block = block, args = args.Select(a => a is string str ? (object)str : (object)(a as GameUnit) ?? Placeholder()).ToArray() });
        public void Message(string text) => Emit(new MessageEvent { text = text });
        public void Sound(int n) => Emit(new SoundEvent { sound = n });   // 0xCCA1 번호(battle_scene.json sounds 규칙)
        public void Music(int n) => Emit(new MusicEvent { music = n });
        public int StageMusic => def.music;
        public bool BagFull => bag.Count >= BagSize;
        public bool GiveItem(int id) { if (BagFull) return false; bag.Add(id); return true; }
        public bool TakeItem(int id) => bag.Remove(id);
        public int Random(int n) => rng.Next(n);
        public void Victory() => End(true);
        public int IndexOf(FarlandAI.Unit u) => (u as GameUnit)?.slot ?? -1;
        public string NameOf(FarlandAI.Unit u) => (u as GameUnit)?.Name ?? "";
        public void Recruit(int nameId, Vector2Int at) => Recruit(new[] { nameId }, at);
        public void Remove(FarlandAI.Unit u) { if (u is GameUnit g && !g.removed) { g.removed = true; Emit(new RemoveEvent { unit = g }); } }

        FarlandRules.StageAI ScriptData(int stage) => rules.stageAI?.FirstOrDefault(e => e.stage == stage);

        public void Wave(string id, int stage)
        {
            var w = ScriptData(stage)?.waves?.FirstOrDefault(x => x.id == id);
            if (w?.spawns == null) return;
            foreach (var sp in w.spawns)
            {
                var at = new Vector2Int(sp.x, sp.y);
                if (UnitAt(at) != null) continue;                            // 빈 칸에만(0x9EFF)
                var st = sp.Clone(); st.cls = sp.cls;
                Emit(new SpawnEvent { unit = AddUnit(st, at, FreeEnemySlot()) });
            }
        }

        public FarlandAI.Unit Create(string templateId, int stage, Vector2Int at)
        {
            var t = ScriptData(stage)?.templates?.FirstOrDefault(x => x.id == templateId);
            if (t == null || UnitAt(at) != null) return null;
            var st = t.Clone(); st.cls = t.cls;
            var u = AddUnit(st, at, st.cls < 18 ? units.Where(o => o.IsPlayerSide).Select(o => o.slot).DefaultIfEmpty(-1).Max() + 1 : FreeEnemySlot());
            Emit(new SpawnEvent { unit = u });
            return u;
        }

        public void Reveal(FarlandAI.Unit u, string templateId, int stage)
        {
            var t = ScriptData(stage)?.templates?.FirstOrDefault(x => x.id == templateId);
            if (t == null || !(u is GameUnit g)) return;
            if (t.revealClass > 0) g.stats.cls = t.revealClass;
            if (t.revealName > 0) { g.stats.nameId = t.revealName; g.displayName = content.NameOf(t.revealName); }
            Emit(new SpawnEvent { unit = g });                               // 그림을 새 클래스로
        }

        public void SwapMap(string mapFile)
        {
            var m = content.LoadMap(mapFile);
            if (m?.tiles == null || m.width != map.width || m.height != map.height) return;
            for (int i = 0; i < m.tiles.Length; i++)
                if (chips[i] != m.tiles[i]) SetChip(new Vector2Int(i % map.width, i / map.width), m.tiles[i]);
        }

        /// 마을에서 쇼핑하기 직전(원작 훅 3)
        public void VisitVillage(GameUnit u) => FarlandStageScript.Village(def.Number, this, u);

        static bool Matches(StageEvent e, GameUnit t) =>
            (e.cls < 0 || e.cls == t.stats.cls) && (e.ai < 0 || e.ai == t.stats.ai) && (e.name <= 0 || e.name == t.stats.nameId) && !t.IsPlayerSide;

        /// FarlandStageAI 가 직접 진행하는 연출(11스테이지 함정)의 대사
        void DrainScriptTalks()
        {
            foreach (var (block, who) in stageState.talks)
                Emit(new TalkEvent { block = block, args = who.Select(w => (object)(w as GameUnit) ?? Placeholder()).ToArray() });
            stageState.talks.Clear();
        }

        // ---- 아군 행동 ----
        public Dictionary<Vector2Int, int> MoveRange(GameUnit u) => FarlandAI.Reach(rules, this, u);

        public void Move(GameUnit u, Vector2Int to)
        {
            if (u.pos == to) return;
            var from = u.pos; u.pos = to;
            Emit(new MoveEvent { unit = u, from = from, to = to });
            FarlandStageAI.OnUnitMoved(def.Number, u, this, stageState);
            DrainScriptTalks();
            if (u.IsPlayerSide)
            {
                FarlandStageScript.UnitMoved(def.Number, this, u);
                CheckChests(u);
                Fire("reach", e => e.x == to.x && e.y == to.y && (e.requireItem <= 0 || bag.Contains(e.requireItem)), mover: u);
                Fire("moved", e => e.slot == u.slot, mover: u);
            }
        }

        /// 이 위치에서 mode 로 칠 수 있는 상대
        public List<GameUnit> Targets(GameUnit u, int mode)
        {
            var list = new List<GameUnit>();
            if (!FarlandRules.CanAttack(Weapon(u.stats.item), mode)) return list;
            var support = FarlandRules.SupportOf(u.stats.cls);
            foreach (var c in FarlandAI.Ring(u.pos, FarlandRules.RangeOf(u.stats.cls, mode)))
            {
                var t = UnitAt(c);
                if (t == null || t == u) continue;
                if (t.IsPlayerSide != u.IsPlayerSide || FarlandRules.CanSupport(support, t.stats, t.acted)) list.Add(t);   // 같은 편은 회복·노래
            }
            return list;
        }

        public FarlandRules.Item Weapon(int id) => id > 0 && id <= rules.items.Length ? rules.items[id - 1]
            : new FarlandRules.Item { physical = new[] { 0, 0 }, magic = new[] { 0, 0 }, name = "" };

        public void Wait(GameUnit u) => u.acted = true;

        // ---- 보물 상자 (0xB51E): 이동 뒤 이웃 6칸의 숨은 상자 발견, 자기 칸의 보이는 상자 열기 ----
        public Chest ChestAt(Vector2Int p) => chests.Find(c => c.state != 0 && c.x == p.x && c.y == p.y);

        void CheckChests(GameUnit u)
        {
            var T = content.Text;
            for (int d = 0; d < 6; d++)
            {
                var c = ChestAt(FarlandRules.Neighbor(u.pos, d));
                if (c != null && c.state == 1) { c.state = 2; Emit(new ChestEvent { chest = c }); Say(T.chestFound); }
            }
            var own = ChestAt(u.pos);
            if (own == null || own.state != 2) return;
            bool opened = true;
            if (own.gold > 0) { gold = System.Math.Min(GoldMax, gold + own.gold); Say(T.chestGold, own.gold); }
            else if (own.item > 0)
            {
                if (bag.Count < BagSize) { bag.Add(own.item); Say(T.chestItem, Item(own.item)?.name); }
                else { Say(T.chestFull); opened = false; }
            }
            else Say(T.chestEmpty);
            if (!opened) return;
            own.state = 3;
            Emit(new ChestEvent { chest = own });
            var sys = content.Game.system;
            if (sys != null && own.item == sys.chestKeyItem && !string.IsNullOrEmpty(sys.chestKeyTalk))
                Emit(new TalkEvent { block = sys.chestKeyTalk, args = new object[] { u } });   // 드카티의 열쇠: 대사 '1'
        }

        // ---- WARP (0x99DF): 마을 칩 위에서 다른 빈 마을 칩으로 ----
        public bool OnWarpTile(Vector2Int p) => def.warp && content.Visual.warpChips != null && System.Array.IndexOf(content.Visual.warpChips, Chip(p)) >= 0;

        /// 같은 WARP 칩(같은 마을 그림)이 있는 자리를 맵 순서(위→아래, 왼→오른)로 돌아가며 다음 빈 곳. 없으면 빈 목록
        public List<Vector2Int> WarpTargets(GameUnit u)
        {
            var list = new List<Vector2Int>();
            if (!OnWarpTile(u.pos)) return list;
            int chip = Chip(u.pos), n = map.width * map.height, start = u.pos.y * map.width + u.pos.x;
            for (int k = 1; k < n; k++)
            {
                int i = (start + k) % n;
                var p = new Vector2Int(i % map.width, i / map.width);
                if (Chip(p) == chip && UnitAt(p) == null) { list.Add(p); break; }
            }
            return list;
        }

        public void Warp(GameUnit u, Vector2Int to)
        {
            var from = u.pos; u.pos = to;
            Emit(new MoveEvent { unit = u, from = from, to = to });
            CheckChests(u);
            Fire("reach", e => e.x == to.x && e.y == to.y && (e.requireItem <= 0 || bag.Contains(e.requireItem)), mover: u);
        }

        // ---- 명령 (0x9E49, 아크만): 0 아무것도 안하다, 1 모두 모여라 ----
        public void Command(GameUnit leader, int mode)
        {
            commandMode = mode;
            var sys = content.Game.system;
            var others = units.Where(o => o.Alive && o.IsPlayerSide && o != leader).ToList();
            var partner = others.Count > 0 ? others[rng.Next(others.Count)] : leader;
            if (sys?.commandBlocks != null && mode < sys.commandBlocks.Length)
                Emit(new TalkEvent { block = sys.commandBlocks[mode], args = new object[] { leader, partner }, file = sys.system });
            if (mode == 1) Gather(leader);
        }

        /// 모두 모여라 (0xA8DF): 아직 행동하지 않은 아군(아크 제외)이 아크 쪽으로 이동(0xA58C 접근 규칙)하고 행동을 마친다
        public void Gather(GameUnit leader)
        {
            if (leader == null || !leader.Alive) return;
            foreach (var u in units.Where(o => o.Alive && o.IsPlayerSide && o != leader && !o.acted).OrderBy(o => o.slot).ToList())
            {
                var reach = MoveRange(u);
                Vector2Int best = u.pos; int bestD = FarlandRules.HexDistance(u.pos, leader.pos), bestRest = -1;
                foreach (var kv in reach)
                {
                    if (UnitAt(kv.Key) != null && kv.Key != u.pos) continue;
                    int d = FarlandRules.HexDistance(kv.Key, leader.pos);
                    if (d < bestD || (d == bestD && kv.Value > bestRest)) { best = kv.Key; bestD = d; bestRest = kv.Value; }
                }
                if (best != u.pos) Move(u, best);
                u.acted = true;
            }
        }

        // ---- 상점 (0xC86C) ----
        public Village ShopAt(Vector2Int p)
        {
            var stage = content.Placements.shops == null ? null : System.Array.Find(content.Placements.shops, s => s.stage == def.id);
            return stage?.villages == null ? null : System.Array.Find(stage.villages, v => v.x == p.x && v.y == p.y);
        }

        public FarlandRules.Item Item(int id) => id > 0 && id <= rules.items.Length ? rules.items[id - 1] : null;

        /// 사다: 값 > 금 이면 '금이 부족', 가방이 차면 '더 가질 수 없음'
        public bool Buy(int id, out string msg)
        {
            var it = Item(id); var T = content.Text;
            if (it == null) { msg = T.cantUse; return false; }
            if (it.price > gold) { msg = T.notEnoughGold; return false; }
            if (bag.Count >= BagSize) { msg = T.bagFull; return false; }
            gold -= it.price; bag.Add(id);
            msg = string.Format(T.bought, it.name);
            return true;
        }

        /// 팔다: 값의 절반(0xBF4B), 값 0 은 못 판다. 금 상한 60000
        public bool Sell(int bagIndex, out string msg)
        {
            var it = Item(bag[bagIndex]); var T = content.Text;
            if (it == null || it.price == 0) { msg = T.cantSell; return false; }
            int got = it.price / 2;
            gold = System.Math.Min(GoldMax, gold + got); bag.RemoveAt(bagIndex);
            msg = string.Format(T.sold, it.name, got);
            return true;
        }

        // ---- 도구 (0xC146) ----
        public enum ItemAction { None, Use, UseOnAlly, Equip, ClassChange }

        /// 원작 판정 순서: 0x80 이면 옆 아군에게, 4·8 이면 사용, 0x40 이면 전직, 그 외는 장비
        public ItemAction ActionOf(int id)
        {
            var it = Item(id);
            if (it == null) return ItemAction.None;
            if ((it.flags & 0x80) != 0) return ItemAction.UseOnAlly;
            if ((it.flags & 0x0C) != 0) return ItemAction.Use;
            if ((it.flags & 0x40) != 0) return ItemAction.ClassChange;
            return ItemAction.Equip;
        }

        public List<GameUnit> AdjacentAllies(GameUnit u)
        {
            var list = new List<GameUnit> { u };
            for (int d = 0; d < 6; d++)
            {
                var o = UnitAt(FarlandRules.Neighbor(u.pos, d));
                if (o != null && o.IsPlayerSide == u.IsPlayerSide) list.Add(o);
            }
            return list;
        }

        /// 사용(회복·특수). 0x80 아이템은 target(옆 아군)에게. 사용하면 행동이 끝난다
        public bool UseItem(GameUnit u, int bagIndex, GameUnit target, out string msg)
        {
            var T = content.Text; var it = Item(bag[bagIndex]); target = target ?? u;
            msg = T.cantUse;
            if (it == null) return false;
            bool used = false;
            if ((it.flags & 0x08) != 0)
            {
                int before = target.stats.hp;
                target.stats.hp = System.Math.Min(target.stats.maxHp, target.stats.hp + FarlandRules.HealAmount(it));
                msg = target.stats.hp >= target.stats.maxHp && FarlandRules.HealAmount(it) >= 999
                    ? string.Format(T.fullyHealed, target.Name) : string.Format(T.healed, target.Name, target.stats.hp - before);
                if (target != u && (it.flags & 0x80) != 0 && target.acted) { target.acted = false; msg += "\n" + string.Format(T.woke, target.Name); }
                used = true;
            }
            else if ((it.flags & 0x04) != 0)
            {
                int st = u.acted ? 1 : 0;
                var before = u.stats.Clone();
                if (FarlandRules.UseSpecial(it, u.stats, ref st, out int goldDelta, rng))
                {
                    used = true;
                    gold = System.Math.Max(0, gold + goldDelta);
                    msg = goldDelta < 0 ? T.goldLost : it.id == 38 ? string.Format(T.slept, u.Name) + "\n" + string.Format(T.fullyHealed, u.Name)
                        : string.Format(T.statUp, u.Name, StatName(before, u.stats));
                }
                else msg = it.id == 38 ? T.nothing : T.cantUse;
            }
            if (!used) return false;
            bag.RemoveAt(bagIndex);
            u.acted = true;
            Emit(new MessageEvent { text = msg });
            return true;
        }

        string StatName(FarlandRules.Stats a, FarlandRules.Stats b)
        {
            var T = content.Text;
            if (b.maxHp != a.maxHp) return T.hp;
            if (b.attack != a.attack) return T.atk;
            if (b.defense != a.defense) return T.def_;
            if (b.knowledge != a.knowledge) return T.know;
            if (b.magicDefense != a.magicDefense) return T.mdef;
            return T.agi;
        }

        /// 장비: 가방의 아이템과 지금 장비를 맞바꾼다(0xC430). 전직 아이템은 레벨 10 이상이면 전직(0xC70F)
        public bool Equip(GameUnit u, int bagIndex, out string msg)
        {
            var T = content.Text; int id = bag[bagIndex]; var it = Item(id);
            msg = T.cantUse;
            if (it == null) return false;
            if (!FarlandRules.CanEquip(it, u.stats.cls)) return false;           // 전직 아이템도 장비 가능 클래스만(기사의 란스 → 파이터)
            if ((it.flags & 0x40) != 0)
            {
                if (u.stats.level < FarlandRules.ClassChangeLevel) { msg = T.levelShort; return false; }
                if (!FarlandRules.TryClassChange(it, u.stats)) return false;
                bag.RemoveAt(bagIndex);
                msg = string.Format(T.classChanged, u.Name, rules.classes[u.stats.cls].name);
                Emit(new MessageEvent { text = msg });
                return true;
            }
            int old = u.stats.item;
            u.stats.item = id;
            if (old > 0) bag[bagIndex] = old; else bag.RemoveAt(bagIndex);
            msg = string.Format(T.equipped, it.name);
            Emit(new MessageEvent { text = msg });
            return true;
        }

        public bool Unequip(GameUnit u, out string msg)
        {
            var T = content.Text; var it = Item(u.stats.item);
            msg = T.cantUse;
            if (it == null) return false;
            if (bag.Count >= BagSize) { msg = T.bagFull; return false; }
            bag.Add(u.stats.item); u.stats.item = 0;
            msg = string.Format(T.unequipped, it.name);
            Emit(new MessageEvent { text = msg });
            return true;
        }

        // ---- 교전 (공격 → 2연타 → 반격) ----
        CombatEvent combat;

        public void Combat(GameUnit atk, GameUnit def, int mode)
        {
            if (FarlandStageScript.BeforeBattle(this.def.Number, this, atk, def)) { atk.acted = true; return; }   // 대본이 교전을 막음
            if (atk.IsPlayerSide == def.IsPlayerSide) { SupportAction(atk, def, mode); return; }
            combat = new CombatEvent { attacker = atk, defender = def, mode = mode, defenderTerrain = Terrain(def.pos), attackerTerrain = Terrain(atk.pos) };
            Emit(combat);
            Strike(atk, def, mode, false);
            if (def.Alive && atk.Alive && FarlandRules.CanCounter(rules, atk.stats, def.stats, mode))
                Strike(def, atk, mode, true);
            combat = null;
            atk.acted = true;
            Engage(atk, def);
            if (!def.Alive) Die(def, atk);
            if (!atk.Alive) Die(atk, def);
            FarlandStageScript.AfterBattle(this.def.Number, this, atk, def);
        }

        /// 같은 편 대상 행동: 회복(지식력 × 2) 또는 격려의 노래(대상이 다시 행동). 반격·경험치 없음
        void SupportAction(GameUnit a, GameUnit t, int mode)
        {
            var ce = new CombatEvent { attacker = a, defender = t, mode = mode, defenderTerrain = Terrain(t.pos), attackerTerrain = Terrain(a.pos) };
            FarlandStageScript.Battle(def.Number, this, a, t);                 // 훅 5 는 회복에도 불린다(2스테이지 대사 '3')
            Emit(ce);
            var st = new CombatEvent.Strike { attacker = a, target = t, hpBefore = t.stats.hp };
            var T = content.Text;
            if (FarlandRules.SupportOf(a.stats.cls) == FarlandRules.Support.Song)
            {
                t.acted = false; st.song = true;
                Say("{0}{1}{2}", a.Name, FarlandStageScript.Particle(a.stats.nameId), T.song ?? " 격려의 노래를 연주하다");   // 0x1AEE9: 은/는
            }
            else
            {
                int before = t.stats.hp;
                t.stats.hp = System.Math.Min(t.stats.maxHp, t.stats.hp + FarlandRules.SpellHeal(a.stats));
                st.heal = t.stats.hp - before;
                Emit(new MessageEvent { text = t.stats.hp >= t.stats.maxHp ? string.Format(T.fullyHealed, t.Name) : string.Format(T.healed, t.Name, st.heal) });
            }
            st.hpAfter = t.stats.hp;
            ce.strikes.Add(st);
            a.acted = true;
            FarlandStageScript.AfterBattle(def.Number, this, a, t);
        }

        void Strike(GameUnit a, GameUnit d, int mode, bool counter)
        {
            int hits = FarlandRules.HitCount(a.stats), first = 0, second = 0, total = 0;
            for (int i = 0; i < hits && d.stats.hp > 0; i++)
            {
                var h = FarlandRules.Attack(rules, a.stats, d.stats, mode, Terrain(d.pos), rng);
                int before = d.stats.hp;
                d.stats.hp -= h.damage; total += h.damage;
                combat?.strikes.Add(new CombatEvent.Strike { attacker = a, target = d, damage = h.damage, hpBefore = before, hpAfter = d.stats.hp, critical = h.critical, counter = counter });
                if (i == 0) first = h.damage; else second = h.damage;
                Emit(new HitEvent { attacker = a, target = d, damage = h.damage, critical = h.critical, counter = counter });
                if (h.critical) Say(content.Text.critical);
            }
            if (total == 0) Say(content.Text.noDamage);
            else Say(content.Text.damage, d.Name, hits == 2 ? FarlandRules.DisplayedDamage(a.stats, first, second) : first);
            if (a.IsPlayerSide && !d.IsPlayerSide && FarlandRules.GainExp(a.stats, d.stats, total, d.stats.hp <= 0))
            {
                FarlandRules.LevelUp(rules, a.stats, rng);
                Say(content.Text.levelUp, a.Name);
            }
        }

        /// 훅 5(공격자, 대상): 보스(ai 1)가 처음 공격받으면 대사·음악 후 ai 2
        void Engage(GameUnit atk, GameUnit target)
        {
            if (!target.IsPlayerSide && !target.engaged && target.stats.ai == 1 && target.stats.cls < 60)
            {
                target.engaged = true;
                FarlandStageScript.BossEngaged(def.Number, this);
                if (Fire("bossEngaged", null, attacker: atk, defender: target, boss: target) && def.bossMusic > 0)
                    Emit(new MusicEvent { music = def.bossMusic });
                target.stats.ai = 2;
            }
            Fire("battle", e => Matches(e, target), attacker: atk, defender: target, boss: target);
            FarlandStageScript.Battle(def.Number, this, atk, target);
            FarlandStageAI.OnBattle(def.Number, target);
            FarlandStageAI.OnAttacked(def.Number, target, stageState);
        }

        /// 테스트용: 전투 계산 없이 쓰러뜨림(격파 이벤트는 그대로 일어남)
        public void DebugDefeat(GameUnit u, GameUnit by) { u.stats.hp = 0; Die(u, by); }

        void Die(GameUnit u, GameUnit by)
        {
            if (u.removed) return;
            bool npc = u.stats.cls >= 60;                                   // 구출 NPC(60·61)는 보스·격파 조건에서 뺀다
            bool boss = !u.IsPlayerSide && !npc && (u.stats.ai == 1 || u.stats.ai == 2);
            int deadCls = u.stats.cls, deadAi = u.stats.ai;
            u.removed = true;
            Emit(new RemoveEvent { unit = u });
            if (u.IsPlayerSide)
            {
                FarlandRules.FaintPenalty(rules, u.stats, rng);
                Say(content.Text.fainted, u.Name);
                if (def.defeat != null && u.slot == def.defeat.unitSlot) End(false);
            }
            else
            {
                Say(content.Text.defeated, u.Name);
                if (boss) Fire("bossDefeated", null, attacker: by, defender: u, boss: u);
                var deadPos = u.pos; int deadName = u.stats.nameId;
                Fire("defeated", e => (npc ? e.cls == deadCls : e.cls < 0 || e.cls == deadCls) && (e.ai < 0 || e.ai == deadAi) &&
                                      (e.name <= 0 || e.name == deadName) && (e.x < 0 || (e.x == deadPos.x && e.y == deadPos.y)) &&
                                      (!e.allGone || !units.Any(o => o.Alive && !o.IsPlayerSide && (e.cls < 0 || o.stats.cls == e.cls) && (e.ai < 0 || o.stats.ai == e.ai))),
                     attacker: by, defender: u, boss: u, place: deadPos);
            }
        }

        /// 보스 격파 시 ai 1·2가 아닌 적 전원 철수 (0xB2C9)
        public void Retreat()
        {
            foreach (var u in units.Where(u => u.Alive && !u.IsPlayerSide && u.stats.ai != 1 && u.stats.ai != 2).ToList())
            { u.removed = true; Emit(new RemoveEvent { unit = u }); }
        }

        // ---- 전투 중 저장 ----
        public BattleSnapshot Snapshot()
        {
            var s = new BattleSnapshot { turn = turn, music = music, stFlag = stageState.flag, stReinforce = stageState.reinforce, stSwitchTalked = stageState.switchTalked,
                                       stVars = (int[])stageState.vars.Clone(), rngState = rng.state };
            s.stTriggerSlot = (stageState.triggerUnit as GameUnit)?.slot ?? -1;
            s.stTrappedSlot = (stageState.trapped as GameUnit)?.slot ?? -1;
            s.units = units.Select(u => new UnitSnap
            {
                slot = u.slot, x = u.pos.x, y = u.pos.y, face = u.face, name = u.displayName,
                acted = u.acted, engaged = u.engaged, removed = u.removed, stats = u.stats.Clone()
            }).ToArray();
            var t = new List<int>();
            for (int i = 0; i < chips.Length; i++)
                if (chips[i] != map.tiles[i]) { t.Add(i % map.width); t.Add(i / map.width); t.Add(chips[i]); }
            s.tiles = t.ToArray();
            s.chests = chests.Select(c => new Chest { state = c.state, x = c.x, y = c.y, gold = c.gold, item = c.item }).ToArray();
            s.firedEvents = def.events == null ? new int[0] : Enumerable.Range(0, def.events.Length).Where(i => fired.Contains(def.events[i])).ToArray();
            s.firedGroups = firedGroups.ToArray();
            return s;
        }

        /// 새로 만든 상태(같은 스테이지)에 저장한 전투 상태를 덮어쓴다
        public void Restore(BattleSnapshot s)
        {
            turn = s.turn; music = s.music;
            units.Clear();
            foreach (var u in s.units)
                units.Add(new GameUnit { slot = u.slot, pos = new Vector2Int(u.x, u.y), face = u.face, displayName = u.name,
                                         acted = u.acted, engaged = u.engaged, removed = u.removed, stats = u.stats.Clone() });
            System.Array.Copy(map.tiles, chips, chips.Length);
            if (s.tiles != null) for (int i = 0; i + 2 < s.tiles.Length; i += 3) chips[s.tiles[i + 1] * map.width + s.tiles[i]] = s.tiles[i + 2];
            chests.Clear();
            if (s.chests != null) chests.AddRange(s.chests.Select(c => new Chest { state = c.state, x = c.x, y = c.y, gold = c.gold, item = c.item }));
            fired.Clear(); firedGroups.Clear();
            if (s.firedEvents != null && def.events != null) foreach (var i in s.firedEvents) if (i < def.events.Length) fired.Add(def.events[i]);
            if (s.firedGroups != null) foreach (var g in s.firedGroups) firedGroups.Add(g);
            stageState.flag = s.stFlag; stageState.reinforce = s.stReinforce; stageState.switchTalked = s.stSwitchTalked;
            if (s.rngState != 0) rng.state = (uint)s.rngState;                // 불러온 뒤에도 같은 난수
            if (s.stVars != null) System.Array.Copy(s.stVars, stageState.vars, System.Math.Min(s.stVars.Length, stageState.vars.Length));
            stageState.triggerUnit = s.stTriggerSlot >= 0 ? BySlot(s.stTriggerSlot) : null;
            stageState.trapped = s.stTrappedSlot >= 0 ? BySlot(s.stTrappedSlot) : null;
            pending.Clear();
        }

        /// 클리어 뒤 파티 갱신: 동료(쓰러진 동료 포함, 원작처럼 다음 스테이지에 복귀), 이번 스테이지 합류 동료, 금, 가방
        public Party ExportParty()
        {
            var p = new Party { gold = gold, commandMode = commandMode };
            p.bag.AddRange(bag);
            foreach (var u in units.Where(u => u.IsPlayerSide && u.stats.cls < 18 && u.stats.nameId > 0)) p.Join(u.stats);
            var rec = content.Placements.recruits == null ? null : System.Array.Find(content.Placements.recruits, r => r.stage == def.id);
            if (rec?.units != null) foreach (var r in rec.units) p.Join(r);
            return p;
        }

        void End(bool victory)
        {
            if (over) return;
            over = true; won = victory;
            Emit(new EndEvent { victory = victory });
        }

        // ---- 턴 진행 ----
        public void StartPlayerTurn()
        {
            foreach (var u in units) u.acted = false;
            if (commandMode == 1) Gather(BySlot(0));                    // 명령이 유지되는 동안 매 턴 모인다
            FarlandStageScript.TurnStart(def.Number, this);
            FarlandStageAI.OnTurnStart(def.Number, this, stageState, BySlot(3) != null);
            Fire("turn", e => e.turn == turn);
        }

        /// 적 턴을 유닛 행동 단위로 진행한다. 한 번 MoveNext 할 때마다 Drain() 으로 연출을 꺼낸다.
        public IEnumerable<GameUnit> EnemyPhase()
        {
            for (int slot = 18; slot < 62 && !over; slot++)
            {
                var u = BySlot(slot);
                if (u == null || !u.Alive || u.acted || !FarlandAI.Acts(u)) continue;
                int times = FarlandAI.ActsTwice(u) ? 2 : 1;
                for (int t = 0; t < times && u.Alive && !over; t++)
                {
                    EnemyAct(u);
                    yield return u;
                }
                u.acted = true;
            }
            if (!over) { turn++; StartPlayerTurn(); }
        }

        void EnemyAct(GameUnit u)
        {
            int stage = def.Number;
            if (!FarlandStageAI.Waypoint(stage, u, this, stageState, out var waypoint)) return;
            var plan = FarlandAI.PlanTurn(rules, this, u, waypoint, rng);
            if (plan.moveTo != u.pos && UnitAt(plan.moveTo) == null) Move(u, plan.moveTo);
            if (plan.attackTarget is GameUnit target && target.Alive && FarlandRules.HexDistance(u.pos, target.pos) == FarlandRules.RangeOf(u.stats.cls, plan.mode))
                Combat(u, target, plan.mode);
            else if (plan.summon)
                foreach (var (cls, pos) in FarlandAI.Summons(rules, this, u, rng))
                {
                    var tpl = FarlandStageAI.SummonTemplate(rules, stage, cls);
                    if (tpl == null || UnitAt(pos) != null) continue;
                    var s = AddUnit(tpl, pos, FreeEnemySlot());
                    s.acted = true;
                    Emit(new SpawnEvent { unit = s });
                }
            if (u.Alive) FarlandStageAI.AfterMove(stage, u);
            if (stage == 13) FarlandStageAI.UpdateSwitches(rules, this);
            if (stageState.reinforce)
            {
                stageState.reinforce = false;
                FarlandStageScript.BlackKnightCall(this, u);                   // 'B' 후 증원(0x140DA)
                foreach (var r in FarlandStageAI.Reinforcements(rules, this).ToList())
                {
                    var st = r.Clone(); st.cls = r.cls;
                    var s = AddUnit(st, new Vector2Int(r.x, r.y), FreeEnemySlot());
                    s.acted = true;
                    Emit(new SpawnEvent { unit = s });
                }
            }
        }
    }
}

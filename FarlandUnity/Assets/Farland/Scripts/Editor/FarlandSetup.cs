using System;
using System.IO;
using System.Linq;
using Farland.Content;
using Farland.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Farland.EditorTools
{
    public static class FarlandSetup
    {
        const string ScenePath = "Assets/Farland/Scenes/Main.unity";

        [MenuItem("Farland/Create Main Scene")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var c = cam.GetComponent<Camera>();
            c.orthographic = true; c.orthographicSize = 7.5f; c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = Color.black;
            cam.transform.position = new Vector3(0, 0, -10);
            new GameObject("Game", typeof(BattleController));
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PlayerSettings.companyName = "Prototype";
            PlayerSettings.productName = "Farland Story";
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.runInBackground = true;
            AssetDatabase.SaveAssets();
            Debug.Log("[Farland] 씬 생성: " + ScenePath);
        }

        /// 배치 모드 검증: 콘텐츠를 읽고 1스테이지를 화면 없이 진행한다(아군은 공격 가능한 상대를 치고, 아니면 대기).
        [MenuItem("Farland/Run Smoke Test")]
        public static void SmokeTest()
        {
            var content = FarlandContent.Load(ContentDB.Load(new[] { Path.Combine(Application.dataPath, "StreamingAssets", "Content") }));
            Debug.Log("[Smoke] 팩: " + string.Join(", ", content.DB.Packs.Select(p => p.id)));
            Debug.Log("[Smoke] 클래스 " + content.Rules.classes.Length + ", 아이템 " + content.Rules.items.Length + ", 스테이지 정의 " + content.Stages.stages.Length);
            foreach (var path in new[] { content.Visual.mapUnitSheet, content.FacePath(0), "Tilesets/MAPCHIP_tiles.png", content.MusicPath(2) })
                if (!content.DB.Exists(path)) throw new Exception("리소스 없음: " + path);

            var def = content.Stage(content.Game.startStage);
            var s = new BattleState(content, def);
            Debug.Log($"[Smoke] {def.id} 맵 {s.map.width}x{s.map.height}, 유닛 {s.units.Count} (아군 {s.units.Count(u => u.IsPlayerSide)})");
            if (content.MapUnitSprite(content.MapChip(0, false)) == null) throw new Exception("맵 유닛 스프라이트 실패");
            if (content.TileSprite(s.map.tileset, s.Chip(Vector2Int.zero)) == null) throw new Exception("타일 스프라이트 실패");

            s.Begin();
            Count(s, "start");
            for (int turn = 0; turn < 30 && !s.over; turn++)
            {
                foreach (var u in s.units.Where(u => u.Alive && u.IsPlayerSide).ToList())
                {
                    if (s.over || !u.Alive) break;
                    // 가장 가까운 적 쪽으로 이동한 뒤 칠 수 있으면 친다
                    var enemy = s.units.Where(e => e.Alive && !e.IsPlayerSide).OrderBy(e => FarlandRules.HexDistance(e.pos, u.pos)).FirstOrDefault();
                    if (enemy == null) break;
                    var reach = s.MoveRange(u);
                    var to = reach.Keys.Where(c => s.UnitAt(c) == null || c == u.pos).OrderBy(c => FarlandRules.HexDistance(c, enemy.pos)).First();
                    s.Move(u, to);
                    var t = s.Targets(u, 0).Concat(s.Targets(u, 1)).FirstOrDefault();
                    if (t != null) s.Combat(u, t, s.Targets(u, 0).Contains(t) ? 0 : 1); else s.Wait(u);
                }
                foreach (var _ in s.EnemyPhase()) { }
                Count(s, "turn " + s.turn);
            }
            Debug.Log($"[Smoke] 종료: over={s.over} won={s.won} 아군 {s.units.Count(u => u.Alive && u.IsPlayerSide)} 적 {s.units.Count(u => u.Alive && !u.IsPlayerSide)}");

            // 시나리오 B: 보스 교전 → 격파(잡병 철수) → 마을 도착 승리
            var b = new BattleState(content, def);
            b.Begin(); b.Drain();
            var boss = b.units.First(u => !u.IsPlayerSide && u.stats.ai == 1);
            var ark = b.BySlot(0);
            ark.stats.attack = 99; ark.stats.hp = ark.stats.maxHp = 150;
            var near = FarlandAI.Ring(boss.pos, 1).First(c => b.UnitAt(c) == null && b.Terrain(c) != 0);
            ark.pos = near;
            for (int i = 0; i < 5 && boss.Alive; i++) b.Combat(ark, boss, 0);
            Count(b, "boss");
            int left = b.units.Count(u => u.Alive && !u.IsPlayerSide);
            if (boss.Alive || left != 0) throw new Exception($"보스 격파·철수 실패 (boss {boss.stats.hp}, 남은 적 {left})");
            b.Move(ark, new Vector2Int(17, 25));
            Count(b, "reach");
            if (!b.over || !b.won) throw new Exception("마을 도착 승리 실패");
            // 시나리오 D: 상점·도구
            var d = new BattleState(content, def);
            var v = d.ShopAt(new Vector2Int(21, 12));
            if (v == null || d.gold != 1000) throw new Exception($"1스테이지 상점/시작 금 실패 (village {v != null}, gold {d.gold})");
            if (!d.Buy(22, out var m1) || d.gold != 960) throw new Exception("약초 구매 실패: " + m1);
            var ark2 = d.BySlot(0); ark2.stats.hp -= 30;
            int hpBefore = ark2.stats.hp;
            if (!d.UseItem(ark2, 0, null, out var m2) || ark2.stats.hp != hpBefore + 20 || !ark2.acted) throw new Exception("약초 사용 실패: " + m2);
            if (d.Buy(2, out var poor) || poor != content.Text.notEnoughGold) throw new Exception("금 부족 판정 실패");
            d.gold = 5000;
            if (!d.Buy(1, out _) || !d.Buy(2, out _)) throw new Exception("무기 구매 실패");
            int oldItem = ark2.stats.item;
            if (!d.Equip(ark2, d.bag.IndexOf(2), out var m3) || ark2.stats.item != 2 || !d.bag.Contains(oldItem)) throw new Exception("장비 교체 실패: " + m3);
            int g = d.gold;
            if (!d.Sell(0, out var m4) || d.gold != g + d.Item(1).price / 2) throw new Exception("판매 실패: " + m4);
            Debug.Log($"[Smoke] 상점·도구: {m1} / {m2} / {m3} / {m4} / 금 {d.gold}, 가방 {d.bag.Count}");

            // 시나리오 E: 보물 상자·WARP·명령
            var e = new BattleState(content, def);
            foreach (var en in e.units.Where(x => !x.IsPlayerSide)) en.removed = true;
            var ark3 = e.BySlot(0);
            var chest = e.ChestAt(new Vector2Int(17, 9));
            if (chest == null || chest.state != 1) throw new Exception("1스테이지 상자 (17,9) 없음");
            var near2 = FarlandAI.Ring(new Vector2Int(17, 9), 1).First(c => e.UnitAt(c) == null);
            ark3.pos = near2 + Vector2Int.right * 3; e.Move(ark3, near2);
            if (chest.state != 2) throw new Exception("숨은 상자 발견 실패");
            int g0 = e.gold; e.Move(ark3, new Vector2Int(17, 9));
            if (chest.state != 3 || e.gold != g0 + 200) throw new Exception($"상자 열기 실패 (state {chest.state}, gold {e.gold - g0})");
            ark3.pos = new Vector2Int(21, 12);
            var targets = e.WarpTargets(ark3);
            if (!targets.Contains(new Vector2Int(17, 25))) throw new Exception("WARP 목적지 실패: " + string.Join(",", targets));
            var lan = e.BySlot(1); var ali = e.BySlot(2);
            int dBefore = FarlandRules.HexDistance(lan.pos, ark3.pos);
            e.Command(ark3, 1);
            int dAfter = FarlandRules.HexDistance(lan.pos, ark3.pos);
            if (!lan.acted || dAfter >= dBefore) throw new Exception("모두 모여라 실패");
            var talk = e.Drain().OfType<TalkEvent>().FirstOrDefault(t => t.file != null);
            if (talk == null || talk.block != "1") throw new Exception("명령 대사 실패");
            e.Warp(ark3, new Vector2Int(17, 25));
            Debug.Log($"[Smoke] 상자·WARP·명령: 금 +200, 모두 모여라 {dBefore}→{dAfter}, WARP 후 승리={e.won}");

            // 시나리오 F: 1~14스테이지 연속 진행(파티 승계, 스테이지별 클리어 조건 강제 달성)
            Party party = null;
            for (var sd = content.Stage("1"); sd != null; sd = string.IsNullOrEmpty(sd.next) ? null : content.Stage(sd.next))
            {
                var bs = new BattleState(content, sd, party);
                var pl = content.PlacementOf(sd.placement ?? sd.id);
                int allies = bs.units.Count(u => u.IsPlayerSide), expect = party == null ? allies : pl.partyStart.Count(ps => party.members.ContainsKey(ps.slot));
                if (party != null && allies != expect) throw new Exception($"{sd.id}: 아군 배치 {allies} != {expect}");
                bs.Begin(); bs.Drain();
                var hero = bs.BySlot(0);
                hero.stats.attack = 999; hero.stats.knowledge = 999; hero.stats.hp = hero.stats.maxHp = 999; hero.stats.defense = 999; hero.stats.magicDefense = 999; hero.stats.item = 34;   // 라이트닝: 물리·마법 모두(상성 무시용)
                ScriptDrive(bs, hero, sd.id);                               // FarlandStageScript 로 옮긴 조건(합류·연출)을 먼저 밟는다
                foreach (var ev in sd.events.Where(ev => ev.trigger != "start" && ev.trigger != "bossEngaged" && ev.trigger != "moved" && ev.trigger != "turn" && !bs.over))
                {
                    if (ev.trigger == "battle")                     // 접촉만(대상은 살아남게)
                    {
                        var foe = bs.units.FirstOrDefault(u => u.Alive && !u.IsPlayerSide && (ev.cls < 0 || u.stats.cls == ev.cls) && (ev.ai < 0 || u.stats.ai == ev.ai));
                        if (foe != null) { foe.stats.hp = foe.stats.maxHp = 5000; bs.Combat(hero, foe, 0); }
                        continue;
                    }
                    if (ev.trigger == "reach")
                    {
                        if (ev.requireItem > 0) bs.bag.Add(ev.requireItem);
                        var occ = bs.UnitAt(new Vector2Int(ev.x, ev.y)); if (occ != null && occ != hero) occ.removed = true;
                        hero.pos = new Vector2Int(ev.x, ev.y) + Vector2Int.left; bs.Move(hero, new Vector2Int(ev.x, ev.y));
                        continue;
                    }
                    var target = bs.units.FirstOrDefault(u => u.Alive && !u.IsPlayerSide && (u.stats.cls < 60 || ev.cls == u.stats.cls) &&
                        (ev.trigger == "bossDefeated" ? (u.stats.ai == 1 || u.stats.ai == 2) && u.stats.cls < 60
                         : (ev.cls < 0 || u.stats.cls == ev.cls) && (ev.ai < 0 || u.stats.ai == ev.ai) && (ev.x < 0 || u.pos == new Vector2Int(ev.x, ev.y))));
                    if (target == null)
                    {
                        if (ev.victory) throw new Exception($"{sd.id}: '{ev.talk}' 조건 대상 없음 ({ev.trigger} cls {ev.cls} ai {ev.ai} @{ev.x},{ev.y})");
                        Debug.Log($"[Smoke]   {sd.id}: '{ev.talk}' 대상 없음(이미 처리됨) {ev.trigger} @{ev.x},{ev.y}");
                        continue;
                    }
                    bs.DebugDefeat(target, hero);
                    if (ev.allGone)                                     // 같은 조건의 나머지도
                        foreach (var rest in bs.units.Where(u => u.Alive && !u.IsPlayerSide && (ev.cls < 0 || u.stats.cls == ev.cls) && (ev.ai < 0 || u.stats.ai == ev.ai)).ToList())
                            bs.DebugDefeat(rest, hero);
                    if (target.Alive) throw new Exception($"{sd.id}: '{ev.talk}' 대상 {target.Name}을 쓰러뜨리지 못함");
                }
                var evs = bs.Drain();
                if (!bs.over || !bs.won) throw new Exception($"{sd.id}: 클리어 실패 over={bs.over} won={bs.won} 보스후보=" +
                    string.Join(";", bs.units.Where(u => !u.IsPlayerSide && (u.stats.ai == 1 || u.stats.ai == 2 || u.stats.cls == 25)).Select(u => $"{u.Name}/{u.stats.cls}/ai{u.stats.ai}/hp{u.stats.hp}/rm{u.removed}")) +
                    " 대사=" + string.Join(",", evs.OfType<TalkEvent>().Select(t => t.block)) + " 메시지=" + string.Join("|", evs.OfType<MessageEvent>().Take(6).Select(m => m.text)));
                party = bs.ExportParty();
                int joined = bs.units.Count(u => u.IsPlayerSide && !u.removed) - allies;
                Debug.Log($"[Smoke] 스테이지 {sd.id}: 아군 {allies}명 배치, 도중 합류 {joined}명, 클리어 대사 {string.Join(",", evs.OfType<TalkEvent>().Select(t => t.block))} → 파티 {party.members.Count}명, 금 {party.gold}");
            }
            if (party.members.Count != 16) throw new Exception("최종 파티 16명 아님: " + party.members.Count);

            // 시나리오 K: 고치기 쉬운 형식 — Tiled 맵, 대사 표, 용어집, 캐릭터 폴더를 상위 팩으로 덮어쓰기
            EditableFormats(content);

            // 시나리오 J: 원작 훅 대본 분기(FarlandStageScript)
            ScriptBranches(content, party);

            // 시나리오 G: 11스테이지 함정 대사 순서, 13스테이지 지형 변경, 3스테이지 5턴째 대사
            var s11 = new BattleState(content, content.Stage("11"), party);
            s11.Begin(); s11.Drain();
            var a1 = s11.units.First(u => u.IsPlayerSide && u.slot == 0); var a2 = s11.units.First(u => u.IsPlayerSide && u.slot == 1);
            foreach (var o in s11.units.Where(u => u.pos == new Vector2Int(8, 38) || u.pos == new Vector2Int(35, 38))) o.removed = true;
            a2.pos = new Vector2Int(34, 38); s11.Move(a2, new Vector2Int(35, 38));
            a1.pos = new Vector2Int(9, 38); s11.Move(a1, new Vector2Int(8, 38));
            var trapTalks = string.Join("", s11.Drain().OfType<TalkEvent>().Select(t => t.block));
            if (trapTalks != "ABCDEFGOH") throw new Exception("11스테이지 함정 대사 순서: " + trapTalks);
            var s13 = new BattleState(content, content.Stage("13"), party);
            var h13 = s13.units.First(u => u.IsPlayerSide);
            h13.pos = new Vector2Int(17, 35); s13.Move(h13, new Vector2Int(17, 36));
            if (s13.Chip(new Vector2Int(5, 40)) != 0x70) throw new Exception("13스테이지 (17,36) 지형 변경 실패");
            var s3 = new BattleState(content, content.Stage("3"), party);
            s3.Begin(); s3.Drain();
            for (int t = 0; t < 4; t++) { foreach (var _ in s3.EnemyPhase()) { } }
            var t3 = s3.Drain().OfType<TalkEvent>().Select(t => t.block + ":" + (t.args.FirstOrDefault() as string)).ToList();
            if (!t3.Any(x => x.StartsWith("4:"))) throw new Exception("3스테이지 5턴째 대사 없음 (turn " + s3.turn + ")");
            Debug.Log($"[Smoke] 대본 분기: 11 함정 {trapTalks}, 13 지형 OK, 3스테이지 턴{s3.turn} 대사 {string.Join(",", t3)}");

            // 시나리오 H: 세이브·로드 왕복(임시 폴더)
            var sdir = Path.Combine(Path.GetTempPath(), "farland_save_test");
            if (Directory.Exists(sdir)) Directory.Delete(sdir, true);
            var store = new SaveStore(sdir);
            party.bag.Add(22); party.gold = 4321; party.commandMode = 1;
            store.Save(3, SaveData.From(party, "14", "제14장", 3725f));
            var back = store.Load(3);
            var restored = back.ToParty();
            if (back.stageId != "14" || restored.gold != 4321 || restored.members.Count != party.members.Count || !restored.bag.Contains(22) || restored.commandMode != 1
                || restored.members[0].level != party.members[0].level || store.List().Count(x => x != null) != 1)
                throw new Exception("세이브·로드 왕복 실패");
            var resumed = new BattleState(content, content.Stage(back.stageId), restored);
            if (resumed.units.Count(u => u.IsPlayerSide) != 16 || resumed.gold != 4321) throw new Exception("불러온 파티로 스테이지 시작 실패");
            // 전투 도중 저장 → 복원이 같은 상태인지(유닛·지형·상자·이벤트·턴)
            var live = new BattleState(content, content.Stage("13"), restored);
            live.Begin(); live.Drain();
            var mover = live.units.First(u => u.IsPlayerSide);
            mover.pos = new Vector2Int(17, 35); live.Move(mover, new Vector2Int(17, 36));   // 지형 변경 이벤트
            foreach (var _ in live.EnemyPhase()) { }
            live.Drain();
            var foe3 = live.units.First(u => !u.IsPlayerSide && u.Alive); foe3.stats.hp = 3;
            var bsave = SaveData.From(restored, "13", "제13장", 10f); bsave.inBattle = true; bsave.battle = live.Snapshot();
            store.Save(5, bsave);
            var bload = store.Load(5);
            var again = new BattleState(content, content.Stage(bload.stageId), bload.ToParty());
            again.Restore(bload.battle);
            string Sig(BattleState b) => $"t{b.turn} m{b.music} " + string.Join(";", b.units.Select(u => $"{u.slot}:{u.pos}:{u.stats.hp}:{u.stats.ai}:{u.acted}:{u.removed}"))
                + " c" + string.Join(",", b.chests.Select(c => c.state)) + " k" + b.Chip(new Vector2Int(5, 40));
            if (!bload.inBattle || Sig(live) != Sig(again)) throw new Exception("전투 중 저장 복원 불일치:\n" + Sig(live) + "\n" + Sig(again));
            var r1 = string.Join(",", Enumerable.Range(0, 8).Select(_ => live.rng.Next(100)));
            var r2 = string.Join(",", Enumerable.Range(0, 8).Select(_ => again.rng.Next(100)));
            if (r1 != r2) throw new Exception($"불러온 뒤 난수가 다름: {r1} / {r2}");
            // WARP: 같은 마을 칩의 다음 자리로 차례로
            var wb = new BattleState(content, content.Stage("2"), restored);
            var wu = wb.units.First(u => u.IsPlayerSide);
            var villages = new System.Collections.Generic.List<Vector2Int>();
            for (int y = 0; y < wb.map.height; y++) for (int x = 0; x < wb.map.width; x++) { var c = new Vector2Int(x, y); if (wb.OnWarpTile(c)) villages.Add(c); }
            if (villages.Count >= 2)
            {
                foreach (var o in wb.units.Where(o => villages.Contains(o.pos)).ToList()) o.removed = true;
                wu.pos = villages[0];
                var next = wb.WarpTargets(wu);
                var expect = villages.Skip(1).First(c => wb.Chip(c) == wb.Chip(villages[0]));
                if (next.Count != 1 || next[0] != expect) throw new Exception($"WARP 다음 자리 {string.Join(",", next)} != {expect}");
            }
            Directory.Delete(sdir, true);
            Debug.Log($"[Smoke] 전투 중 저장: 13스테이지 {live.turn}턴, 유닛 {live.units.Count}개·지형·상자·이벤트 동일하게 복원");
            Debug.Log($"[Smoke] 세이브·로드: 슬롯 4 → {back.stageTitle}, 동료 {restored.members.Count}명, 금 {restored.gold}, 가방 {restored.bag.Count}");

            // 시나리오 C: 우선순위 높은 팩이 같은 경로 파일을 덮어쓰는지
            var tmp = Path.Combine(Path.GetTempPath(), "farland_pack_test");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            var mod = Path.Combine(tmp, "mod");
            Directory.CreateDirectory(Path.Combine(mod, "Config"));
            File.WriteAllText(Path.Combine(mod, "pack.json"), "{\"id\":\"test_mod\",\"priority\":5}");
            File.WriteAllText(Path.Combine(mod, "Config", "strings.json"), "{\"title\":\"MOD TITLE\"}");
            var modded = FarlandContent.Load(ContentDB.Load(new[] { Path.Combine(Application.dataPath, "StreamingAssets", "Content"), tmp }));
            if (modded.Text.title != "MOD TITLE" || modded.DB.SourceOf("Config/strings.json") != "test_mod" || modded.DB.SourceOf("Config/visuals.json") != "base")
                throw new Exception("팩 덮어쓰기 실패");
            Directory.Delete(tmp, true);
            Debug.Log("[Smoke] 팩 덮어쓰기: strings.json ← test_mod, visuals.json ← base");
            // 시나리오 I: 낱장 파일이 시트 칸보다 우선하는지(맵 유닛·지형·전투 프레임)
            var cellChecks = new (string sheet, Sprite sp)[]
            {
                (content.Visual.mapUnitSheet, content.MapUnitSprite(content.MapChip(0, false))),
                ("SpriteDefs/CHAR_00_frames.png", content.DB.Sprite("SpriteDefs/CHAR_00_frames.png", new RectInt(160, 0, 160, 160), new Vector2(0.6f, 0.2f), 1f)),
            };
            foreach (var (sheet, sp) in cellChecks)
            {
                var cellRel = ContentDB.CellFile(sheet, sheet == content.Visual.mapUnitSheet ? content.MapChip(0, false) : 1);
                if (content.DB.Exists(cellRel) && (sp == null || sp.texture.name != cellRel)) throw new Exception("낱장 우선 적용 실패: " + cellRel);
                Debug.Log($"[Smoke] 낱장: {cellRel} {(content.DB.Exists(cellRel) ? "사용" : "없음(시트 사용)")}");
            }
            Debug.Log("[Smoke] OK");
        }

        static void Count(BattleState s, string label)
        {
            var ev = s.Drain();
            foreach (var e in ev)
                if (e is MessageEvent m) Debug.Log("[Smoke]   " + m.text);
                else if (e is TalkEvent t) Debug.Log("[Smoke]   대화 " + t.block);
                else if (e is EndEvent end) Debug.Log("[Smoke]   종료 victory=" + end.victory);
            Debug.Log($"[Smoke] {label}: 이벤트 {ev.Count}");
        }

        static void EditableFormats(FarlandContent baseContent)
        {
            // Tiled 맵 = 원래 추출 맵
            var ua = Path.Combine(Application.dataPath, "..", "..", "UnityAssets", "Maps", "STAGE01.json");
            var tm = baseContent.LoadMap("Maps/STAGE01.tmj");
            if (tm == null || tm.tileset != "MAPCHIP_tiles") throw new Exception("Tiled 맵 읽기 실패");
            if (File.Exists(ua) && !JsonUtility.FromJson<MapData>(File.ReadAllText(ua)).tiles.SequenceEqual(tm.tiles)) throw new Exception("Tiled 맵 타일이 원본과 다름");
            // 임시 팩: 대사 표 한 줄, 용어집, 아크 맵 그림
            var root = Path.Combine(Path.GetTempPath(), "farland_pack_test");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var pk = Path.Combine(root, "edit");
            Directory.CreateDirectory(Path.Combine(pk, "Dialogue")); Directory.CreateDirectory(Path.Combine(pk, "Config")); Directory.CreateDirectory(Path.Combine(pk, "Characters", "01"));
            File.WriteAllText(Path.Combine(pk, "pack.json"), "{\"id\":\"edit\",\"priority\":50,\"enabled\":true}");
            File.WriteAllText(Path.Combine(pk, "Dialogue", "STAGE01.tsv"), "블록	줄	얼굴	원문	고친 대사\n0	1	훼리오	-	멜, 서둘러!\n");
            File.WriteAllText(Path.Combine(pk, "Config", "glossary.json"), "{\"terms\":[{\"from\":\"아크\",\"to\":\"아크로이드\"},{\"from\":\"훼리오\",\"to\":\"페리오\"}]}");
            var png = new Texture2D(32, 32); File.WriteAllBytes(Path.Combine(pk, "Characters", "01", "map.png"), png.EncodeToPNG());
            var c2 = FarlandContent.Load(ContentDB.Load(new[] { Path.Combine(Application.dataPath, "StreamingAssets", "Content"), root }));
            var sc = c2.LoadScript("Dialogue/STAGE01.json");
            var b0 = System.Array.Find(sc.blocks, b => b.id == "0");
            if (b0.commands[1].text != "멜, 서둘러!") throw new Exception("대사 표 반영 실패: " + b0.commands[1].text);
            if (c2.NameOf(1) != "아크로이드" || c2.NameOf(16) != "페리오") throw new Exception("용어집 이름 반영 실패: " + c2.NameOf(1));
            if (c2.CharacterMapSprite(1, 0, false, out _) == null) throw new Exception("캐릭터 폴더 그림 없음");
            if (c2.CharacterMapSprite(2, 0, false, out _) != null) throw new Exception("다른 캐릭터에 그림이 적용됨");
            Directory.Delete(root, true);
            Debug.Log("[Smoke] 고치기 쉬운 형식: Tiled 맵, 대사 표, 용어집, 캐릭터 폴더 덮어쓰기 OK");
        }

        static string Talks(BattleState b) => string.Join("", b.Drain().OfType<TalkEvent>().Select(t => t.block));

        static void Step(BattleState b, GameUnit u, int x, int y)
        {
            var to = new Vector2Int(x, y);
            var occ = b.UnitAt(to); if (occ != null && occ != u) occ.removed = true;
            u.pos = to + Vector2Int.left; b.Move(u, to);
        }

        static GameUnit Ally(BattleState b, int nameId, GameUnit hero, Vector2Int near)
        {
            var u = b.units.FirstOrDefault(o => o.IsPlayerSide && !o.removed && o.stats.nameId == nameId);
            if (u != null) return u;
            var st = hero.stats.Clone(); st.cls = 0; st.nameId = nameId;
            return b.AddUnit(st, near, nameId - 1);
        }

        /// 연속 진행 테스트: 대본으로 옮긴 합류·보스 전환 조건을 원작대로 밟는다
        static void ScriptDrive(BattleState bs, GameUnit hero, string id)
        {
            GameUnit Foe(System.Func<GameUnit, bool> f) => bs.units.FirstOrDefault(u => u.Alive && !u.IsPlayerSide && f(u));
            switch (id)
            {
                case "4":                                                       // (17,44) 를 지키던 적 → 리사
                    var g4 = Foe(u => u.pos == new Vector2Int(17, 44)); if (g4 != null) bs.Combat(hero, g4, 0);
                    break;
                case "6":                                                       // 미리아가 아리나 옆에 → 합류, 그 다음 고렘을 깨움
                    var g6 = Foe(u => u.pos == new Vector2Int(12, 36)); if (g6 != null) bs.Combat(hero, g6, 0);
                    var npc = bs.units.First(u => u.Alive && u.stats.cls == 61);
                    var miria = Ally(bs, 10, hero, new Vector2Int(1, 1));
                    var side = FarlandAI.Ring(npc.pos, 1).First(c => bs.Inside(c) && bs.UnitAt(c) == null);
                    miria.pos = side + Vector2Int.left; bs.Move(miria, side);
                    var golem = Foe(u => u.stats.cls == 26 && u.stats.ai == 5);
                    golem.stats.hp = golem.stats.maxHp = 5000; bs.Combat(hero, golem, 0);
                    break;
                case "7":                                                       // 잠든 삿큐버스를 두 번 친다(깨움 → 본모습 → 보스)
                    for (int k = 0; k < 2; k++) { var sc = Foe(u => u.stats.cls == 25); sc.stats.hp = sc.stats.maxHp = 5000; bs.Combat(hero, sc, 0); }
                    break;
                case "9":                                                       // 소녀 경비를 없애고 성문으로 → 흑기사
                    foreach (var gd in bs.units.Where(u => u.Alive && !u.IsPlayerSide && u.stats.cls == 36).ToList()) bs.DebugDefeat(gd, hero);
                    Step(bs, hero, 26, 14);
                    break;
                case "11":                                                      // 열쇠를 들고 봉인의 문
                    bs.bag.Add(37); Step(bs, hero, 22, 39);
                    break;
            }
        }

        static void ScriptBranches(FarlandContent content, Party party)
        {
            var log = new System.Collections.Generic.List<string>();
            // 5: 이완이 없으면 통로 대사 없음 / 이완이 아닌 아군은 '0' / 이완이 오면 '1'·'2' 후 통로가 열림. 신비한 나무 → 능력치 +1
            var s5 = new BattleState(content, content.Stage("5"), party); s5.Begin(); s5.Drain();
            var ark = s5.BySlot(0);
            Step(s5, ark, 29, 3); var t = Talks(s5);
            if (s5.Chip(new Vector2Int(26, 3)) == 2) throw new Exception("5: 이완 없이 통로가 열림");
            var iwan = Ally(s5, 9, ark, new Vector2Int(28, 4));
            Step(s5, ark, 29, 3); t = Talks(s5);
            if (t != "0") throw new Exception("5: 이완이 아닌 아군 대사 " + t);
            Step(s5, iwan, 29, 3); t = Talks(s5);
            if (t != "12" || s5.Chip(new Vector2Int(26, 3)) != 2) throw new Exception("5: 이완 통로 개방 " + t);
            s5.SetChip(new Vector2Int(10, 10), 0x26); int atk = ark.stats.attack;
            Step(s5, ark, 10, 10); t = Talks(s5);
            if (t != "56" || ark.stats.attack != atk + 1 || s5.Chip(new Vector2Int(10, 10)) != 1) throw new Exception("5: 신비한 나무 " + t);
            log.Add("5 통로·나무");
            // 6: 아리나가 없으면 잠든 고렘을 칠 수 없음('C')
            var s6 = new BattleState(content, content.Stage("6"), party); s6.Begin(); s6.Drain();
            var golem = s6.units.First(u => u.stats.cls == 26 && u.stats.ai == 5); int ghp = golem.stats.hp;
            s6.Combat(s6.BySlot(0), golem, 0); t = Talks(s6);
            if (t != "C" || golem.stats.hp != ghp) throw new Exception("6: 고렘 교전 막기 " + t);
            var npc = s6.units.First(u => u.stats.cls == 61);
            Step(s6, s6.BySlot(0), 32, 25); t = Talks(s6);
            if (t != "1" && t != "2") throw new Exception("6: 아리나 대사 " + t);
            log.Add("6 고렘 막기·아리나");
            // 7: 시민을 치면 '0' 후 '3', 다시 치면 '4'
            var s7 = new BattleState(content, content.Stage("7"), party); s7.Begin(); s7.Drain();
            var cit = s7.units.FirstOrDefault(u => u.Alive && u.stats.nameId == 27);
            if (cit != null)
            {
                cit.stats.hp = cit.stats.maxHp = 5000; var h7 = s7.BySlot(0); h7.stats.attack = 1;
                s7.Combat(h7, cit, 0); t = Talks(s7);
                s7.Combat(h7, cit, 0); t += Talks(s7);
                if (t != "034") throw new Exception("7: 시민 대사 " + t);
                log.Add("7 시민");
            }
            // 8: 노인 — 마을 (9,49) 에서 메두사킬러(66), 가방이 가득이면 '3'
            var s8 = new BattleState(content, content.Stage("8"), party); s8.Begin(); s8.Drain();
            var h8 = s8.BySlot(0); h8.pos = new Vector2Int(9, 49);
            s8.bag.Clear(); for (int i = 0; i < BattleState.BagSize; i++) s8.bag.Add(22);
            s8.VisitVillage(h8); t = Talks(s8);
            if (t != "3") throw new Exception("8: 가방 가득 대사 " + t);
            s8.bag.Clear(); s8.VisitVillage(h8); t = Talks(s8);
            if (t != "45" || !s8.bag.Contains(66)) throw new Exception("8: 메두사킬러 " + t);
            log.Add("8 노인");
            // 3: 아군 둘이 아래로 내려가면 증원 + '3'
            var s3 = new BattleState(content, content.Stage("3"), party); s3.Begin(); s3.Drain();
            int before = s3.units.Count(u => u.Alive && !u.IsPlayerSide);
            foreach (var a in s3.units.Where(u => u.IsPlayerSide).Take(2)) a.pos = new Vector2Int(a.pos.x, 22);
            foreach (var _ in s3.EnemyPhase()) { }
            t = Talks(s3);
            int after = s3.units.Count(u => u.Alive && !u.IsPlayerSide);
            if (!t.Contains("3") || after <= before - 3) throw new Exception($"3: 증원 {before}→{after} 대사 {t}");
            log.Add($"3 증원(적 {before}→{after})");
            // 11: 봉인의 문 — 열쇠 없으면 대사만, 있으면 클리어
            var s11 = new BattleState(content, content.Stage("11"), party); s11.Begin(); s11.Drain();
            var h11 = s11.BySlot(0);
            Step(s11, h11, 22, 39); t = Talks(s11);
            if (s11.over || (t != "5" && t != "4")) throw new Exception("11: 열쇠 없는 문 " + t);
            s11.bag.Add(37); Step(s11, h11, 22, 39); t = Talks(s11);
            if (!s11.won || !t.EndsWith("z") || s11.bag.Contains(37) || !s11.bag.Contains(25)) throw new Exception("11: 열쇠 문 " + t);
            log.Add("11 봉인의 문");
            // 9: 소녀 경비가 있으면 성문 '0', 없으면 흑기사 등장 연출
            var s9 = new BattleState(content, content.Stage("9"), party); s9.Begin(); s9.Drain();
            var h9 = s9.BySlot(0);
            if (s9.units.Any(u => u.Alive && u.stats.cls == 36)) { Step(s9, h9, 26, 14); t = Talks(s9); if (t != "0") throw new Exception("9: 경비 있는 성문 " + t); }
            foreach (var gd in s9.units.Where(u => u.Alive && u.stats.cls == 36).ToList()) s9.DebugDefeat(gd, h9);
            s9.Drain();
            Step(s9, h9, 27, 14); t = Talks(s9);
            if (t != "182345" || !s9.units.Any(u => u.Alive && u.stats.cls == 38))                       // 소녀(다크 프린세스)는 철수
                throw new Exception("9: 성문 연출 " + t);
            log.Add("9 성문·흑기사");
            // 같은 편 대상 행동: 회복(지식력×2, 최대HP 까지)·격려의 노래·적 페어리 회복, 2스테이지 '2'·'3'
            var p2 = new Party(); foreach (var m in party.members.Values.Where(m => m.nameId <= 3)) p2.Join(m.Clone());   // 드카티 합류 전
            var s2 = new BattleState(content, content.Stage("2"), p2); s2.Begin(); s2.Drain();
            var ark2 = s2.BySlot(0);
            var cleric = s2.units.FirstOrDefault(u => u.IsPlayerSide && u.stats.nameId == 3) ?? Ally(s2, 3, ark2, new Vector2Int(1, 1));
            cleric.stats.cls = 7; cleric.pos = ark2.pos + Vector2Int.right; if (s2.UnitAt(cleric.pos) != cleric) cleric.pos = FarlandAI.Ring(ark2.pos, 1).First(c => s2.UnitAt(c) == null);
            ark2.stats.hp = 1; int kn = cleric.stats.knowledge;
            if (!s2.Targets(cleric, 0).Contains(ark2)) throw new Exception("회복: 다친 아크가 대상 목록에 없음");
            s2.Combat(cleric, ark2, 0);
            var ev2 = s2.Drain();
            t = string.Join("", ev2.OfType<TalkEvent>().Select(x => x.block));
            if (ark2.stats.hp != System.Math.Min(ark2.stats.maxHp, 1 + kn * 2) || !cleric.acted) throw new Exception($"회복량 {ark2.stats.hp} (지식 {kn})");
            if (t != "3") throw new Exception("2: 회복할 때 드카티 대사 " + t + " slot18=" + s2.BySlot(18)?.Name + "/" + s2.BySlot(18)?.stats.cls + " cleric=" + cleric.stats.nameId + "/" + cleric.slot + " npc60=" + string.Join(",", s2.units.Where(u => u.stats.cls == 60).Select(u => u.slot)));
            var boss2 = s2.units.FirstOrDefault(u => u.Alive && !u.IsPlayerSide && u.stats.ai == 1);
            if (boss2 != null)
            {
                int bhp = boss2.stats.hp; var other = s2.units.First(u => u.IsPlayerSide && u.stats.nameId != 1 && u.Alive);
                s2.Combat(other, boss2, 0); t = Talks(s2);
                if (t != "2" || boss2.stats.hp != bhp) throw new Exception("2: 아크 아닌 동료가 드카티에게 " + t);
            }
            var bard = Ally(s2, 10, ark2, new Vector2Int(2, 2)); bard.stats.cls = 17;
            ark2.acted = true; bard.pos = FarlandAI.Ring(ark2.pos, 1).First(c => s2.UnitAt(c) == null);
            s2.Combat(bard, ark2, 0); s2.Drain();
            if (ark2.acted || !bard.acted) throw new Exception("격려의 노래: 대상이 다시 행동할 수 없음");
            log.Add($"회복 +{kn * 2}·노래·2장 '2''3'");
            Debug.Log("[Smoke] 대본 분기: " + string.Join(", ", log));
        }

        [MenuItem("Farland/Build Windows Player")]
        public static void BuildPlayer()
        {
            PlayerSettings.runInBackground = true;                    // 창이 포커스를 잃어도 멈추지 않게(자동 캡처·테스트용)
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;             // 스팀덱 1280×800 전체 화면(창 모드는 Alt+Enter)
            PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true; PlayerSettings.runInBackground = true;
            var r = BuildPipeline.BuildPlayer(new[] { ScenePath }, "Build/Win/Farland.exe", BuildTarget.StandaloneWindows64, BuildOptions.None);
            Debug.Log("[Farland] 빌드: " + r.summary.result + " " + r.summary.totalSize / (1024 * 1024) + "MB");
            if (r.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("빌드 실패");
        }

        public static void BatchBuild()
        {
            try { BuildPlayer(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        /// 명령줄: -executeMethod Farland.EditorTools.FarlandSetup.Batch
        public static void Batch()
        {
            try { CreateScene(); SmokeTest(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
    }
}

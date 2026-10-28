using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Farland.Content;
using Farland.UI;
using UnityEngine;

namespace Farland.Game
{
    /// 진입점. 씬에 이 컴포넌트 하나만 있으면 콘텐츠를 읽어 타이틀 → 스테이지를 진행한다.
    public class BattleController : MonoBehaviour
    {
        [Tooltip("비워 두면 Config/game.json 의 startStage")] public string stageOverride;

        FarlandContent content;
        BattleState state;
        BattleView view;
        GameUI ui;
        MusicPlayer music;
        Camera cam;
        FarlandDialogue.Script script;

        enum Mode { Busy, Idle, Selected, Moved, Targeting, Warping }
        bool warped;
        readonly Dictionary<string, FarlandDialogue.Script> scripts = new Dictionary<string, FarlandDialogue.Script>();

        FarlandDialogue.Script ScriptOf(string file)
        {
            if (string.IsNullOrEmpty(file)) return script;
            if (!scripts.TryGetValue(file, out var s))
            {
                scripts[file] = s = content.LoadScript(file);              // JSON + 대사 표(.tsv) + 용어집
            }
            return s;
        }
        Mode mode = Mode.Busy;
        GameUnit selected;
        Vector2Int origin;
        Dictionary<Vector2Int, int> reach;
        int attackMode;
        Vector3 lastMouse;
        Vector2Int padCell;                   // 게임패드 커서 칸
        SfxPlayer mapSfx;                     // 맵 화면 효과음(대본·WARP)
        bool sceneOn = true;                        // B 키로 전투 연출 켜기/끄기

        IEnumerator Start()
        {
            cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" }.GetComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 7.5f; cam.backgroundColor = Color.black;
            cam.clearFlags = CameraClearFlags.SolidColor;

            content = FarlandContent.Load(ContentDB.Load());
            Debug.Log("[Farland] 콘텐츠 팩: " + string.Join(", ", content.DB.Packs.Select(p => p.id + "(" + p.priority + ")")));
            ui = gameObject.AddComponent<GameUI>(); ui.Init(content);
            mapSfx = gameObject.AddComponent<SfxPlayer>(); mapSfx.Init(content);
            ui.onEndTurn = () => { if (mode == Mode.Idle) StartCoroutine(EnemyPhase()); };
            ui.onSave = () => { if (mode == Mode.Idle && state != null && !state.over) StartCoroutine(BattleSave()); };
            music = gameObject.AddComponent<MusicPlayer>(); music.Init(content);
            view = gameObject.AddComponent<BattleView>();

            ui.SetEndTurnVisible(false);
            var args = System.Environment.GetCommandLineArgs();
            if (System.Array.IndexOf(args, "-skipTitle") >= 0)               // 개발용: 바로 스테이지
            {
                yield return RunStage(string.IsNullOrEmpty(stageOverride) ? content.Game.startStage : stageOverride);
                yield break;
            }
            var flow = content.Game.flow;
            if (System.Array.IndexOf(args, "-debugEnding") >= 0) { yield return EndingSequence(); yield break; }   // 개발용
            int ds = System.Array.IndexOf(args, "-debugStage");                                                         // 개발용: N스테이지부터
            if (ds >= 0 && ds + 1 < args.Length) { yield return Campaign(args[ds + 1], PartyBefore(args[ds + 1])); yield break; }
            if (System.Array.IndexOf(args, "-skipOpening") < 0) yield return PlayMovie(flow?.opening);
            while (true)                                                     // 타이틀 → 선택
            {
                int pick = 0;
                yield return ui.TitleMenu(k => pick = k);
                if (pick == 2) { yield return PlayMovie(flow?.opening); continue; }
                if (pick == 3) { yield return EndingSequence(); continue; }
                if (pick == 1)                                               // 이어하기
                {
                    SaveData chosen = null;
                    yield return LoadScreen(d => chosen = d);
                    if (chosen == null) continue;
                    playSeconds = chosen.playSeconds;
                    yield return Campaign(chosen.stageId, chosen.ToParty(), chosen.inBattle ? chosen.battle : null);
                    continue;
                }
                playSeconds = 0;
                yield return Campaign(string.IsNullOrEmpty(stageOverride) ? content.Game.startStage : stageOverride);
            }
        }

        IEnumerator PlayMovie(string relative)
        {
            if (string.IsNullOrEmpty(relative)) yield break;
            music.Stop();
            yield return ui.PlayVideo(content.DB.Resolve(relative));
        }

        /// 스테이지를 끝까지 진행하고, 마지막 스테이지면 에필로그·엔딩. 끝나면 타이틀로
        Party party;

        // ---- 세이브·로드 ----
        SaveStore saveStore;
        SaveStore saves => saveStore ??= new SaveStore();          // persistentDataPath 는 생성자에서 못 읽으므로 처음 쓸 때 만든다
        float playSeconds;

        string SlotLabel(int i, SaveData d)
        {
            var T = content.Text;
            if (d == null) return string.Format(T.saveSlot, i + 1, "<color=#7F8698>" + T.emptySlot + "</color>");
            var t = System.TimeSpan.FromSeconds(d.playSeconds);
            return string.Format(T.saveSlot, i + 1, $"<color=#FFE08A>{d.stageTitle}</color>   <color=#B8C0D0>{d.members?.Length ?? 0}명 · {d.gold}G · {(int)t.TotalHours}:{t.Minutes:00} · {d.savedAt?.Replace('T', ' ')}</color>");
        }

        /// 아군 턴 도중 저장(행동 선택 전). 파티는 지금 맵의 아군 + 이번 스테이지에 이어 온 파티
        IEnumerator BattleSave()
        {
            mode = Mode.Busy;
            ui.SetEndTurnVisible(false);
            var snap = state.Snapshot();
            var p = new Party { gold = state.gold, commandMode = state.commandMode };
            p.bag.AddRange(state.bag);
            if (party != null) foreach (var m in party.members.Values) p.Join(m);
            foreach (var u in state.units.Where(u => u.IsPlayerSide && u.stats.cls < 18 && u.stats.nameId > 0)) p.Join(u.stats);
            yield return SaveScreen(p, state.def.id, snap);
            ui.SetEndTurnVisible(true);
            mode = Mode.Idle;
        }

        IEnumerator SaveScreen(Party p, string nextStage, BattleSnapshot battle = null)
        {
            var T = content.Text;
            bool done = false;
            string title = content.Stage(nextStage)?.title ?? nextStage;
            void Show()
            {
                var all = saves.List();
                var rows = new List<(string, bool, System.Action)>();
                for (int i = 0; i < all.Length; i++)
                {
                    int k = i;
                    void Write()
                    {
                        var data = SaveData.From(p, nextStage, title, playSeconds);
                        if (battle != null) { data.inBattle = true; data.battle = battle; data.stageTitle = title + " " + string.Format(T.inBattleMark, battle.turn); }
                        saves.Save(k, data);
                        ui.Message(string.Format(T.saved, k + 1));
                        done = true;
                    }
                    var old = all[i];
                    rows.Add((SlotLabel(i, old), true, () =>
                    {
                        if (old == null) { Write(); return; }
                        ui.ShowList(string.Format(T.overwriteAsk, k + 1), "", new List<(string, bool, System.Action)> { (SlotLabel(k, old), false, null) },
                            new List<(string, System.Action)> { (T.overwrite, Write), (T.cancel, Show) }, 1);
                    }));
                }
                ui.ShowList(T.saveTitle, title, rows, new List<(string, System.Action)> { (T.noSave, () => done = true) }, 1);
            }
            Show();
            while (!done) yield return null;
            ui.HideList();
        }

        IEnumerator LoadScreen(System.Action<SaveData> chosen)
        {
            var T = content.Text;
            var all = saves.List();
            bool done = false;
            var rows = new List<(string, bool, System.Action)>();
            for (int i = 0; i < all.Length; i++)
            {
                int k = i;
                rows.Add((SlotLabel(i, all[i]), all[i] != null, () => { chosen(all[k]); done = true; }));
            }
            ui.ShowList(T.loadTitle, saves.Any() ? "" : T.noSaves, rows, new List<(string, System.Action)> { (T.cancel, () => { chosen(null); done = true; }) }, 1);
            while (!done) yield return null;
            ui.HideList();
        }

        void LateUpdate() { if (state != null && !state.over) playSeconds += Time.unscaledDeltaTime; }

        /// 개발용: 1스테이지 아군 + 그 앞 스테이지들의 합류 동료로 파티를 만든다
        Party PartyBefore(string stageId)
        {
            if (stageId == content.Game.startStage) return null;
            var p = new Party { gold = content.Stage(content.Game.startStage)?.startGold ?? 0 };
            foreach (var u in content.PlacementOf(content.Game.startStage).units) if (u.cls < 18) p.Join(u);
            for (var sd = content.Stage(content.Game.startStage); sd != null && sd.id != stageId; sd = string.IsNullOrEmpty(sd.next) ? null : content.Stage(sd.next))
            {
                var rec = content.Placements.recruits == null ? null : System.Array.Find(content.Placements.recruits, r => r.stage == sd.id);
                if (rec?.units != null) foreach (var r in rec.units) p.Join(r);
            }
            return p;
        }

        IEnumerator Campaign(string stageId, Party startParty = null, BattleSnapshot resume = null)
        {
            party = startParty;
            while (!string.IsNullOrEmpty(stageId))
            {
                yield return RunStage(stageId, resume);
                resume = null;
                while (state != null && !state.over) yield return null;
                bool won = state != null && state.won;
                var def = state?.def;
                var next = won ? state.ExportParty() : null;
                yield return new WaitForSeconds(0.5f);
                yield return ui.WaitClick();
                TearDown();
                if (!won || def == null) yield break;                                        // 패배 → 타이틀
                if (def.id == content.Game.flow?.endingAfterStage) { yield return EndingSequence(); yield break; }
                party = next;
                stageId = def.next;
                if (!string.IsNullOrEmpty(stageId)) yield return SaveScreen(party, stageId);   // 클리어 뒤 저장(원작 15칸)
            }
        }

        void TearDown()
        {
            ui.HideEnd(); ui.HideActions(); ui.HideList(); ui.SetEndTurnVisible(false); ui.ShowUnit(null, null, null);
            ui.ClearMessages(); ui.SetPhase(""); ui.SetHelpVisible(false);
            view.Clear(); music.Stop();
            state = null; selected = null; mode = Mode.Busy;
        }

        /// 에필로그(0x116B8): STAGE99 대사 → 엔딩 영상(음악 12 포함)
        IEnumerator EndingSequence()
        {
            var flow = content.Game.flow;
            var ep = flow?.epilogue;
            if (ep?.talks != null)
            {
                var epScript = content.LoadScript(ep.dialogue);
                ui.SetBackdrop(string.IsNullOrEmpty(ep.background) ? null : content.DB.Sprite(ep.background));
                var party = new Dictionary<int, GameUnit>();
                GameUnit Member(int k)
                {
                    if (!party.TryGetValue(k, out var u))
                        party[k] = u = new GameUnit { slot = k, face = k, displayName = content.NameOf(k + 1), stats = new FarlandRules.Stats { nameId = k + 1 } };
                    return u;
                }
                foreach (var step in ep.talks)
                {
                    var a = (step.args ?? new string[0]).Select(s => s.StartsWith("unit:") ? (object)Member(int.Parse(s.Substring(5)))
                                                                    : s.StartsWith("str:") ? s.Substring(4) : (object)s).ToArray();
                    yield return ui.Dialogue(epScript, step.talk, a, new System.Random());
                }
                ui.HideBackdrop();
            }
            yield return PlayMovie(flow?.ending);
        }

        IEnumerator RunStage(string id, BattleSnapshot resume = null)
        {
            var def = content.Stage(id);
            if (def == null) { Debug.LogError("[Farland] stages.json 에 스테이지가 없습니다: " + id); yield break; }
            script = content.LoadScript(def.dialogue);
            if (resume == null && def.prologue?.events != null && def.prologue.events.Length > 0)      // 프롤로그: 같은 맵, 프롤로그 배치로 대사
            {
                var pdef = JsonUtility.FromJson<StageDef>(JsonUtility.ToJson(def));
                pdef.placement = def.prologue.placement; pdef.events = def.prologue.events; pdef.prologue = null;
                var pstate = new BattleState(content, pdef);
                view.Build(content, pstate, cam);
                var focus = pstate.BySlot(4) ?? pstate.BySlot(0);
                if (focus != null) view.Focus(focus.pos);
                pstate.Begin();
                state = pstate;
                yield return Play(pstate.Drain());
                view.Clear();
            }
            state = new BattleState(content, def, party);
            ui.SetHelpVisible(true);
            if (resume != null)                                                // 전투 중 저장에서 이어 감
            {
                state.Restore(resume);
                view.Build(content, state, cam);
                view.Focus(state.BySlot(0)?.pos ?? Vector2Int.zero);
                ui.SetPhase(def.title);
                music.Play(state.music > 0 ? state.music : def.music);
                yield return PlayerPhase();
                yield break;
            }
            view.Build(content, state, cam);
            view.Focus(state.BySlot(0)?.pos ?? Vector2Int.zero);
            ui.SetPhase(def.title);
            state.Begin();
            yield return Play(state.Drain());
            yield return PlayerPhase();
        }

        IEnumerator PlayerPhase()
        {
            if (state.over) yield break;
            yield return Play(state.Drain());                               // 턴 시작 처리(모두 모여라 등)
            ui.SetPhase(content.Text.playerPhase + "  " + state.turn);
            view.RefreshAll();
            ui.SetEndTurnVisible(true);
            mode = Mode.Idle;
        }

        IEnumerator EnemyPhase()
        {
            mode = Mode.Busy;
            Deselect();
            ui.SetEndTurnVisible(false);
            ui.SetPhase(content.Text.enemyPhase);
            foreach (var u in state.EnemyPhase())
            {
                view.Focus(u.pos);
                var events = state.Drain();
                if (events.Count > 0) { yield return Play(events); yield return new WaitForSeconds(content.Game.enemyStepDelay); }
                view.Refresh(u);
            }
            yield return Play(state.Drain());
            if (!state.over) yield return PlayerPhase();
        }

        /// 상태가 낸 연출 이벤트를 차례로 재생
        IEnumerator Play(List<BattleEvent> events)
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case MoveEvent m: yield return view.MoveUnit(m.unit, m.from, m.to); break;
                    case CombatEvent c when ui.Battle.Enabled && sceneOn: yield return ui.Battle.Play(c, state.def.Number); break;
                    case HitEvent h: if (!(ui.Battle.Enabled && sceneOn)) yield return view.Flash(h.target); break;
                    case MessageEvent msg: ui.Message(msg.text); break;
                    case RemoveEvent r: view.RemoveUnit(r.unit); break;
                    case SpawnEvent s: view.AddUnit(s.unit); break;
                    case TileEvent t: view.SetTile(t.cell); break;
                    case MusicEvent mu: music.Play(mu.music); break;
                    case SoundEvent so: mapSfx.Play(so.sound); break;
                    case TalkEvent talk: yield return ui.Dialogue(ScriptOf(talk.file), talk.block, talk.args, state.rng); break;
                    case ChestEvent ch: view.RefreshChest(ch.chest); break;
                    case EndEvent end:
                        ui.SetEndTurnVisible(false); ui.HideActions(); mode = Mode.Busy;
                        ui.ShowEnd(end.victory);
                        break;
                }
            }
        }

        // ---- 개발용 (DebugCapture) ----
        public GameUI UI => ui;
        public bool Ready => mode == Mode.Idle;
        public void SelectSlot(int slot) { var u = state?.BySlot(slot); if (u != null && mode == Mode.Idle) Select(u); }

        /// 아크를 1스테이지 마을로 옮겨 상점을 연다(화면 확인용)
        public IEnumerator DebugShop()
        {
            var ark = state.BySlot(0);
            Select(ark);
            var v = state.ShopAt(new Vector2Int(21, 12));
            ark.pos = new Vector2Int(21, 12); view.Refresh(ark);
            view.Focus(ark.pos);
            state.gold = 3000; state.bag.Add(22); state.bag.Add(4); state.bag.Add(44);
            yield return Shop(v);
        }

        public void DebugBag() { ui.HideList(); OpenBag(); }
        public int DebugHoverSlot = -1;           // 개발용: 마우스가 이 슬롯 유닛 위에 있는 것처럼

        public void DebugIdle() { if (state != null && !state.over) { Deselect(); mode = Mode.Idle; } }

        /// 상자 발견 → 명령(모두 모여라) → WARP 표시 (화면 확인용)
        public IEnumerator DebugMisc(System.Action<string> shot)
        {
            var ark = state.BySlot(0);
            var near = FarlandAI.Ring(new Vector2Int(17, 9), 1).First(c => state.UnitAt(c) == null && state.Terrain(c) != 0);
            ark.pos = near + Vector2Int.left; view.RemoveUnit(ark); view.AddUnit(ark);
            Select(ark);
            yield return MoveSelected(near);
            view.Focus(near);
            yield return new WaitForSeconds(0.3f);
            shot("8_chest_found");
            ui.HideActions();
            StartCoroutine(DoCommand(1));
            yield return new WaitForSeconds(0.5f);
            shot("9_command_talk");
            for (int i = 0; i < 20 && ui.DialogueOpen; i++) { ui.AdvanceDialogue(); yield return new WaitForSeconds(0.2f); }
            yield return new WaitForSeconds(1.5f);
            shot("10_after_gather");
            ui.HideActions();
            ark.pos = new Vector2Int(17, 25); view.RemoveUnit(ark); view.AddUnit(ark);
            foreach (var en in state.units.Where(u => u.pos == new Vector2Int(21, 12) && !u.IsPlayerSide)) { en.removed = true; view.RemoveUnit(en); }
            BeginWarp();
            view.Focus(new Vector2Int(19, 18));
            yield return new WaitForSeconds(0.3f);
            shot("11_warp");
        }

        /// 아크를 가장 가까운 적 옆으로 옮겨 직접 공격시킨다(연출 확인용)
        public IEnumerator DebugCombat(int cls = -1, int item = -1, int forceMode = -1)
        {
            Deselect(); mode = Mode.Busy;
            var ark = state.BySlot(0);
            if (cls >= 0) ark.stats.cls = cls;
            if (item >= 0) ark.stats.item = item;
            if (forceMode == 9)                                                  // 같은 편 대상(회복·노래) 연출 확인: 다친 동료를 치료
            {
                var friend = state.units.First(u => u.Alive && u.IsPlayerSide && u != ark);
                friend.stats.hp = 1; friend.acted = true;
                var fc = FarlandAI.Ring(friend.pos, 1).First(c => state.Inside(c) && state.UnitAt(c) == null && state.Terrain(c) != 0);
                state.Move(ark, fc); view.Focus(fc);
                state.Combat(ark, friend, 0);
                yield return Play(state.Drain());
                mode = Mode.Idle;
                yield break;
            }
            var enemy = state.units.Where(u => u.Alive && !u.IsPlayerSide).OrderBy(u => FarlandRules.HexDistance(u.pos, ark.pos)).First();
            var cell = FarlandAI.Ring(enemy.pos, 1).First(c => state.Inside(c) && state.UnitAt(c) == null && state.Terrain(c) != 0);
            state.Move(ark, cell);
            view.Focus(cell);
            state.Combat(ark, enemy, forceMode >= 0 ? forceMode : FarlandRules.CanAttack(state.Weapon(ark.stats.item), 0) ? 0 : 1);
            yield return Play(state.Drain());
            mode = Mode.Idle;
        }

        // ---- 입력 ----
        void Update()
        {
            if (state == null || ui == null) return;
            PadInput.Poll();
            CameraInput();
            if (ui.DialogueOpen) return;
            if (ui.ListOpen) { if (Input.GetKeyDown(KeyCode.Escape) || PadInput.B) { ui.HideList(); StartCoroutine(AfterMove()); } return; }
            if (Input.GetKeyDown(KeyCode.B) || PadInput.Y) { sceneOn = !sceneOn; ui.Message(sceneOn ? "전투 연출 ON" : "전투 연출 OFF"); }
            if (PadInput.Active && !ui.MenuOpen)                                 // 패드: 칸 커서를 스틱·D패드로
            {
                if (!state.Inside(padCell)) padCell = (selected ?? state.BySlot(0))?.pos ?? Vector2Int.zero;
                var step = PadInput.Step();
                if (step != Vector2Int.zero)
                {
                    var n = padCell + step;
                    if (state.Inside(n)) { padCell = n; view.KeepVisible(padCell); }
                }
            }
            var cell = PadInput.Active ? padCell : view.MouseCell();
            view.SetCursor(cell, mode != Mode.Busy);
            if (DebugHoverSlot >= 0 && state.BySlot(DebugHoverSlot) != null) cell = state.BySlot(DebugHoverSlot).pos;
            var hover = state.UnitAt(cell);
            var shown = hover ?? selected;
            ui.ShowHover(mode == Mode.Busy ? null : hover, hover != null ? view.CellToScreen(cell) : Vector2.zero);
            ui.ShowUnit(selected, selected != null ? state.Weapon(selected.stats.item) : null, selected != null ? content.Rules.classes[selected.stats.cls].name : null);

            if (mode == Mode.Busy) return;
            if (PadInput.Menu && mode == Mode.Idle && selected == null) { ui.ToggleSystemMenu(); return; }
            if (PadInput.View) ui.ToggleHelp();
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape) || PadInput.B)
            {
                if (mode == Mode.Idle && selected == null) ui.ToggleSystemMenu();     // 원작: 빈 곳 오른쪽 클릭 → 메뉴
                else Cancel();
                return;
            }
            if (ui.SystemMenuOpen) { if (Input.GetMouseButtonDown(0) && !ui.PointerOverUI) ui.HideActions(); return; }
            if (mode == Mode.Idle && (Input.GetKeyDown(KeyCode.Space) || PadInput.X)) { StartCoroutine(EnemyPhase()); return; }
            if (mode == Mode.Idle && Input.GetKeyDown(KeyCode.F5)) { StartCoroutine(BattleSave()); return; }
            bool confirm = (Input.GetMouseButtonDown(0) && !ui.PointerOverUI && !PadInput.Active) || (PadInput.A && !ui.MenuOpen);
            if (!confirm) return;

            switch (mode)
            {
                case Mode.Idle:
                    if (hover != null && hover.IsPlayerSide && !hover.acted) Select(hover);
                    break;
                case Mode.Selected:
                    if (hover == selected) StartCoroutine(AfterMove());
                    else if (reach.ContainsKey(cell) && hover == null) StartCoroutine(MoveSelected(cell));
                    else if (hover != null && hover.IsPlayerSide && !hover.acted) Select(hover);
                    break;
                case Mode.Warping:
                    if (reach != null && reach.ContainsKey(cell)) StartCoroutine(DoWarp(cell));
                    break;
                case Mode.Targeting:
                    if (hover != null && state.Targets(selected, attackMode).Contains(hover)) StartCoroutine(Attack(hover));
                    break;
            }
        }

        void CameraInput()
        {
            var d = PadInput.Active ? Vector2.zero : new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));   // 패드 스틱은 커서용
            if (d != Vector2.zero) view.Pan(d * Time.deltaTime * 12f);
            if (Input.mouseScrollDelta.y != 0 && !ui.PointerOverUI) view.Zoom(Input.mouseScrollDelta.y);
            if (PadInput.LB) view.Zoom(-1);
            if (PadInput.RB) view.Zoom(1);
            var look = PadInput.Look;
            if (look != Vector2.zero) view.Pan(look * Time.deltaTime * 14f);
            if (Input.GetMouseButton(2))
            {
                var delta = cam.ScreenToWorldPoint(lastMouse) - cam.ScreenToWorldPoint(Input.mousePosition);
                view.Pan(delta);
            }
            lastMouse = Input.mousePosition;
        }

        void Select(GameUnit u)
        {
            selected = u; origin = u.pos; mode = Mode.Selected; warped = false;
            reach = state.MoveRange(u);
            view.ClearMarks();
            view.Mark(reach.Keys.Where(c => state.UnitAt(c) == null || c == u.pos), "move");
            ui.HideActions();
        }

        void Deselect()
        {
            selected = null; reach = null;
            view.ClearMarks(); ui.HideActions();
        }

        void Cancel()
        {
            if (selected == null) return;
            if (mode == Mode.Warping) { view.ClearMarks(); StartCoroutine(AfterMove()); return; }
            if (mode == Mode.Moved || mode == Mode.Targeting)
            {
                ui.HideList();
                if (selected.pos != origin)           // 원작처럼 행동 전이면 제자리로 되돌린다
                {
                    var from = selected.pos; selected.pos = origin;
                    StartCoroutine(view.MoveUnit(selected, from, origin, 0.01f));
                }
                Select(selected);
                return;
            }
            Deselect(); mode = Mode.Idle;
        }

        IEnumerator MoveSelected(Vector2Int to)
        {
            mode = Mode.Busy;
            view.ClearMarks();
            state.Move(selected, to);
            yield return Play(state.Drain());
            if (state.over) yield break;
            yield return AfterMove();
        }

        IEnumerator AfterMove()
        {
            mode = Mode.Moved;
            view.ClearMarks();
            var T = content.Text;
            var items = new List<(string, System.Action)>();
            for (int m = 0; m < 2; m++)
            {
                int mm = m;
                var targets = state.Targets(selected, m);
                if (targets.Count > 0)
                    items.Add((m == 0 ? T.attackDirect : T.attackIndirect, () => BeginTargeting(mm)));
            }
            if (!warped && state.WarpTargets(selected).Count > 0) items.Add((T.warp, BeginWarp));                  // 마을 → 마을 (0x99DF)
            if (selected.slot == 0) items.Add((T.command, OpenCommand));                                         // 아크만 (0x9CB1)
            var village = state.ShopAt(selected.pos);
            if (village != null) items.Add((T.shop, () => StartCoroutine(Shop(village))));       // 마을에 멈추면 쇼핑(0x9E28)
            items.Add((T.items, () => OpenBag()));
            items.Add((T.wait, () => FinishAction()));
            items.Add((T.cancel, Cancel));
            ui.ShowActions(items, view.CellToScreen(selected.pos));
            yield break;
        }

        // ---- WARP ----
        void BeginWarp()
        {
            ui.HideActions();
            var next = state.WarpTargets(selected);
            if (next.Count == 0) { mapSfx.Play(0xd); StartCoroutine(AfterMove()); return; }      // 0x9A4F: 갈 곳이 없으면 효과음만
            StartCoroutine(DoWarp(next[0]));
        }

        IEnumerator DoWarp(Vector2Int to)
        {
            mode = Mode.Busy; view.ClearMarks();
            state.Warp(selected, to);
            warped = true;
            var ev = state.Drain();
            foreach (var e in ev) if (e is MoveEvent m) view.Focus(m.to);
            yield return Play(ev);
            if (state.over) yield break;
            yield return AfterMove();
        }

        // ---- 명령 (아크): 아무것도 안하다 / 모두 모여라 ----
        void OpenCommand()
        {
            ui.HideActions();
            var T = content.Text;
            var rows = new List<(string, bool, System.Action)>
            {
                ((state.commandMode == 0 ? "▶ " : "") + T.cmdNone, true, () => StartCoroutine(DoCommand(0))),
                ((state.commandMode == 1 ? "▶ " : "") + T.cmdGather, true, () => StartCoroutine(DoCommand(1))),
            };
            ui.ShowList(T.command, "", rows, new List<(string, System.Action)> { (T.cancel, () => { ui.HideList(); StartCoroutine(AfterMove()); }) });
        }

        IEnumerator DoCommand(int m)
        {
            ui.HideList(); mode = Mode.Busy;
            state.Command(selected, m);
            yield return Play(state.Drain());
            view.RefreshAll();
            if (state.over) yield break;
            yield return AfterMove();
        }

        void FinishAction()
        {
            ui.HideList();
            state.Wait(selected); view.Refresh(selected); Deselect(); mode = Mode.Idle;
            if (state.units.Where(u => u.Alive && u.IsPlayerSide).All(u => u.acted)) StartCoroutine(EnemyPhase());
        }

        // ---- 상점 (0xC86C): 마을 대사 → 사다/팔다 ----
        int shopReturnMusic;

        IEnumerator Shop(Village v)
        {
            ui.HideActions();
            mode = Mode.Busy;
            var sys = content.Game.system;
            var stageShops = System.Array.Find(content.Placements.shops, s => s.stage == state.def.id);
            int idx = stageShops != null ? System.Array.IndexOf(stageShops.villages, v) : 0;
            shopReturnMusic = music.Current;
            state.VisitVillage(selected);                                        // 원작 훅 3(8스테이지 노인 등)
            yield return Play(state.Drain());
            if (sys?.shopMusic != null && sys.shopMusic.Length > 1) music.Play(sys.shopMusic[idx % 2 == 1 ? 1 : 0]);       // 0xC8A4
            if (sys != null && !string.IsNullOrEmpty(sys.shopGreeting))
                yield return ui.Dialogue(ScriptOf(sys.system), sys.shopGreeting, new object[] { selected }, state.rng);
            if (!string.IsNullOrEmpty(v.@event)) yield return ui.Dialogue(script, v.@event, new object[] { content.Text.villager, selected }, state.rng);
            mode = Mode.Moved;
            ShowShop(v, true, null);
        }

        bool describe;

        /// 아이템 설명 (talk 0x100+번호 → STAGE51 블록 chr(0x40+번호))
        IEnumerator Describe(int id, System.Action back)
        {
            ui.HideList();
            var it = state.Item(id);
            yield return ui.Dialogue(ScriptOf(content.Game.system?.itemInfo), ((char)(0x40 + id)).ToString(), new object[] { it?.name ?? "" }, state.rng);
            back();
        }

        void ShowShop(Village v, bool buying, string message)
        {
            var T = content.Text;
            var rows = new List<(string, bool, System.Action)>();
            if (buying)
                foreach (var si in v.items)
                {
                    var it = state.Item(si.id); int id = si.id;
                    rows.Add(($"{it.name}  {string.Format(T.price, it.price)}", describe || it.price <= state.gold,
                              () => { if (describe) StartCoroutine(Describe(id, () => ShowShop(v, true, null))); else { state.Buy(id, out var msg); ShowShop(v, true, msg); } }));
                }
            else
                for (int i = 0; i < state.bag.Count; i++)
                {
                    var it = state.Item(state.bag[i]); int k = i;
                    int sid = state.bag[i];
                    rows.Add(($"{it.name}  {string.Format(T.price, it.price / 2)}", describe || it.price > 0,
                              () => { if (describe) StartCoroutine(Describe(sid, () => ShowShop(v, false, null))); else { state.Sell(k, out var msg); ShowShop(v, false, msg); } }));
                }
            if (message != null) ui.Message(message);
            ui.ShowList(T.shop + " — " + (describe ? T.view : buying ? T.buy : T.sell), string.Format(T.gold, state.gold), rows, new List<(string, System.Action)>
            {
                ((describe ? "▶ " : "") + T.view, () => { describe = !describe; ShowShop(v, buying, null); }),
                (buying ? T.sell : T.buy, () => { describe = false; ShowShop(v, !buying, null); }),
                (T.close, () => { describe = false; ui.HideList(); music.Play(shopReturnMusic); StartCoroutine(AfterMove()); }),
            });
        }

        // ---- 도구 (0xC146): 사용 / 옆 아군에게 사용 / 장비 / 전직 / 떼어 내기 ----
        void OpenBag(string message = null)
        {
            ui.HideActions();
            var T = content.Text;
            if (message != null) ui.Message(message);
            var rows = new List<(string, bool, System.Action)>();
            var eq = state.Item(selected.stats.item);
            if (eq != null)
                rows.Add(($"{T.equippedMark} {eq.name} ({T.unequip})", true, () => { state.Unequip(selected, out var m); OpenBag(m); }));
            for (int i = 0; i < state.bag.Count; i++)
            {
                int k = i; var it = state.Item(state.bag[i]); int iid = state.bag[i];
                if (describe) { rows.Add((it.name, true, () => StartCoroutine(Describe(iid, () => OpenBag())))); continue; }
                switch (state.ActionOf(state.bag[i]))
                {
                    case BattleState.ItemAction.Use:
                        rows.Add(($"{it.name} ({T.use})", true, () => { if (state.UseItem(selected, k, null, out var m)) Used(); else OpenBag(m); }));
                        break;
                    case BattleState.ItemAction.UseOnAlly:
                        rows.Add(($"{it.name} ({T.use})", true, () => ChooseTarget(k)));
                        break;
                    case BattleState.ItemAction.Equip:
                    case BattleState.ItemAction.ClassChange:
                        bool ok = FarlandRules.CanEquip(it, selected.stats.cls);
                        rows.Add(($"{it.name} ({T.equip})", ok, () => { state.Equip(selected, k, out var m); view.Refresh(selected); OpenBag(m); }));
                        break;
                }
            }
            ui.ShowList(string.Format(T.bagTitle, state.bag.Count) + " — " + selected.Name, string.Format(T.gold, state.gold), rows,
                        new List<(string, System.Action)>
                        {
                            ((describe ? "▶ " : "") + T.view, () => { describe = !describe; OpenBag(); }),
                            (T.close, () => { describe = false; ui.HideList(); StartCoroutine(AfterMove()); }),
                        });
        }

        void ChooseTarget(int bagIndex)
        {
            var T = content.Text;
            var rows = new List<(string, bool, System.Action)>();
            foreach (var t in state.AdjacentAllies(selected))
            {
                var tt = t;
                rows.Add(($"{t.Name}  {T.hp} {t.stats.hp}/{t.stats.maxHp}", true, () => { if (state.UseItem(selected, bagIndex, tt, out var m)) Used(); else OpenBag(m); }));
            }
            ui.ShowList(T.chooseTarget, "", rows, new List<(string, System.Action)> { (T.cancel, () => OpenBag()) });
        }

        void Used()
        {
            ui.HideList();
            StartCoroutine(PlayThen(state.Drain(), () => FinishAction()));
        }

        IEnumerator PlayThen(List<BattleEvent> events, System.Action then) { yield return Play(events); then(); }

        void BeginTargeting(int m)
        {
            attackMode = m; mode = Mode.Targeting;
            ui.HideActions();
            view.ClearMarks();
            view.Mark(state.Targets(selected, m).Select(t => t.pos), "attack");
        }

        IEnumerator Attack(GameUnit target)
        {
            mode = Mode.Busy;
            view.ClearMarks();
            state.Combat(selected, target, attackMode);
            yield return Play(state.Drain());
            if (selected != null) view.Refresh(selected);
            Deselect();
            if (state.over) yield break;
            mode = Mode.Idle;
            if (state.units.Where(u => u.Alive && u.IsPlayerSide).All(u => u.acted)) StartCoroutine(EnemyPhase());
        }
    }
}

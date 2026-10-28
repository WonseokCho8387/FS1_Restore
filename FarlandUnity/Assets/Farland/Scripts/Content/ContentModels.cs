using System;
using System.Collections.Generic;
using UnityEngine;

namespace Farland.Content
{
    // Config/*.json 의 형식. 모두 콘텐츠 팩에서 교체할 수 있다.

    [Serializable] public class TalkStep { public string talk; public string[] args; }
    /// 에필로그(0x116B8): unit:N = 동료 N번(얼굴 N)
    [Serializable] public class EpilogueDef { public string dialogue, background; public TalkStep[] talks; }
    [Serializable]
    public class FlowConfig
    {
        public string opening, ending, endingAfterStage;
        public EpilogueDef epilogue;
    }
    [Serializable] public class PrologueDef { public string placement; public StageEvent[] events; }

    [Serializable]
    public class SystemTalk
    {
        public string itemInfo, system, shopGreeting, chestKeyTalk;
        public string[] commandBlocks;
        public int[] shopMusic;
        public int chestKeyItem = 37;
    }

    [Serializable]
    public class GameConfig
    {
        public SystemTalk system = new SystemTalk();
        public FlowConfig flow = new FlowConfig();
        public string startStage = "1";
        public string musicFile = "Audio/BGM/track{0:00}.ogg";   // {0} = 음악 번호 + musicTrackOffset
        public int musicTrackOffset = 1;
        public string titleImage, charactersFile, battleData;
        public float enemyStepDelay = 0.25f;
        public int rngSeed;                                      // 0 이면 매번 다름
    }

    [Serializable] public class ClassChip { public int cls, chip; }

    [Serializable]
    public class VisualColors { public string move, attack, cursor, ally, enemy, window, windowBorder; }

    /// 맵 유닛 오른쪽 아래의 작은 글자(원작 HELP03: ENEMY 'E' 빨강, FRIEND 'F' 파랑, HELP!! 'D' 노랑). ICONCHIP 칸 번호
    [Serializable]
    public class SideMarks { public int enemy = 18, ally = 20, rescue = 22, cursor = 16; public string enemyColor, allyColor, rescueColor; }

    [Serializable]
    public class BitmapFontDef { public string han, eng; }

    /// 9조각 창 이미지. border = 왼, 아래, 오른, 위(원작 픽셀)
    [Serializable]
    public class WindowSkin { public string image; public int[] border; }

    [Serializable]
    public class WindowSkins { public WindowSkin normal, solid, menu; }

    [Serializable]
    public class GaugeColors { public string fill, lost, back; }

    [Serializable]
    public class VisualConfig
    {
        public int tileSize = 32, tileColumns = 10;
        public string mapUnitSheet;
        public int mapUnitSize = 32, mapUnitColumns = 20, actedChipOffset = 42, actedMaxClass = 17;
        public ClassChip[] classChip;
        public string facePattern, iconSheet;
        public int iconSize = 32, iconColumns = 12, chestClosedIcon = 4, chestOpenIcon = 6;
        public int[] warpChips;
        public SideMarks sideMarks;
        public string[] fonts;
        public BitmapFontDef bitmapFont;
        public WindowSkins windows;
        public string cursorImage;
        public int cursorSize = 24;
        public GaugeColors hpGauge;
        public VisualColors colors;

        public static Color ParseColor(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
    }

    [Serializable]
    public class UiStrings
    {
        public string title, pressStart, playerPhase, enemyPhase, endTurn, wait, cancel, attackDirect, attackIndirect,
            victory, defeat, damage, critical, noDamage, miss, levelUp, fainted, defeated, hp, lv, atk, def_, know, mdef, agi, exp, help,
            shop, items, buy, sell, close, equip, use, unequip, gold, price, bought, sold, notEnoughGold, bagFull, cantSell, cantUse,
            noTarget, levelShort, equipped, unequipped, healed, fullyHealed, nothing, classChanged, goldLost, statUp, slept, woke,
            chooseTarget, villager, bagTitle, equippedMark, warp, command, cmdNone, cmdGather, view,
            chestFound, chestGold, chestItem, chestFull, chestEmpty, warpPick, menuStart, menuOpening, menuEnding, skip, backToTitle,
            menuContinue, saveTitle, loadTitle, emptySlot, saveSlot, saved, noSave, noSaves, saveGame, inBattleMark,
            overwriteAsk, overwrite, controls, song;
    }

    [Serializable]
    public class StageEvent
    {
        public string trigger;     // start | bossEngaged | bossDefeated | reach | turn
        public string talk;        // 대화 블록 id (Dialogue 파일)
        public string[] args;      // unit:N, boss, attacker, defender, mover, str:텍스트
        public int x = -1, y = -1, turn;
        public int cls = -1, ai = -1;      // defeated: 쓰러진 적의 클래스·AI 조건(-1 = 무관)
        public int requireItem;            // reach: 가방에 이 아이템이 있어야(11스테이지 열쇠)
        public FarlandRules.Spawn[] spawn; // 대본이 적·동료를 새로 만듦(9스테이지 흑기사 등)
        public string group;               // 같은 group 은 한 번만(여러 칸 중 하나에 도착 등)
        public int name;                   // defeated·battle: 대상 이름 id 조건(0 = 무관)
        public int slot = -1;              // moved: 이 슬롯 유닛이 움직였을 때
        public int setAi = -1;             // battle: 대상의 ai 를 바꿈(6스테이지 고렘 → 보스)
        public int[] recruit;              // 합류 동료 이름 id. 쓰러진 적 자리(defeated) / 도착한 유닛 옆(reach)에 나타남
        public int removeCls = -1;         // 이 클래스 유닛을 맵에서 뺌(합류로 바뀌는 NPC)
        public bool allGone;               // defeated: 조건에 맞는 적이 하나도 남지 않았을 때만
        public int[] tiles;                // 지형 변경: (x, y, 맵칩) 반복
        public int modifyAi = -1, setDefense = -1, setMagicDefense = -1;   // 이 ai 의 적 능력치 변경(4스테이지 보스 무적 해제)
        public int music;          // 0 = 바꾸지 않음
        public bool retreat, victory, once = true;
    }

    [Serializable] public class DefeatDef { public int unitSlot; }

    [Serializable]
    public class StageDef
    {
        public string id, title, map, dialogue, placement;
        public int music, bossMusic, startGold;
        public bool warp = true;
        public StageEvent[] events;
        public DefeatDef defeat;
        public PrologueDef prologue;
        public string next;                // 클리어 뒤 다음 스테이지(비면 마지막)

        public int Number => int.TryParse(id, out var n) ? n : 0;
    }

    [Serializable] public class StageList { public StageDef[] stages; }

    // ---- 전투 연출 (Config/battle_scene.json) ----
    [Serializable] public class SoundCue { public int at, sound; }
    /// 연출 클립. effect* 는 클래스 고유 이펙트(창·화살·음표 등: 원작 0x6118 기록), spell 은 공용 마법 이름
    [Serializable]
    public class AnimClip
    {
        public int state, hitIndex, effectAt = -1; public int[] frames, effectFrames; public bool run, effectMove;
        public SoundCue[] sounds; public string spell;
    }
    /// 공용 마법 연출(0x18CFB 불·0x18F63 얼음·0x191EF 번개·0x19425 버닝·0x19615 불새). 프레임은 시전자 SPZ 안의 번호
    [Serializable] public class SpellDef { public string name, sheet; public int[] launch, frames, soundFrames; public bool move; public int sound = -1, hits = 1; }
    [Serializable] public class HitSounds { public int miss = 34, hit = 28, critical = 25; }
    [Serializable] public class SoundRule { public int start; public string pattern; }
    /// 0x183AD 를 조건별로 실행한 결과: item(-1 = 그 외), mode, spirit(정령 무기), vsPlayer(상대 클래스 < 18) → state(-1 = 6·7 무작위)
    [Serializable] public class StateRule { public int item, mode, spirit, vsPlayer, state; }
    [Serializable] public class ClassAnim { public int cls; public StateRule[] rules; public AnimClip[] clips; }
    [Serializable] public class StageLetter { public int stage; public string letter, demonKingLetter; }
    [Serializable] public class CommonFrames { public int stance, hit = 1, fall = 2, down = 3; }

    [Serializable]
    public class BattleSceneConfig
    {
        public bool enabled = true;
        public int screenWidth = 640, screenHeight = 400, meleeGap = 56, backgroundY = 88;
        public float frameTime = 0.09f, runSpeed = 240;
        public int[] allyFeet, enemyFeet;
        public string sheetPattern, background;
        public string[] terrainLetters;
        public StageLetter[] stageLetters;
        public CommonFrames common = new CommonFrames();
        public ClassAnim[] classes;
        public SoundRule[] sounds;
        public string fallbackSound;
        public float soundVolume = 0.8f, projectileSpeed = 320, bodyHeight = 40;
        public HitSounds hitSounds = new HitSounds();
        public SpellDef[] spells;
        public string[] spiritSpells;                    // 정령 무기 (플래그>>8)-1 → 정령 이름

        public SpellDef Spell(string name) => string.IsNullOrEmpty(name) || spells == null ? null : Array.Find(spells, s => s.name == name);

        /// 효과음 번호 → 파일 경로 (원작 DS:0x7FD 목록 규칙). 없으면 fallbackSound
        public string SoundPath(ContentDB db, int n)
        {
            if (n < 0 || sounds == null) return null;
            SoundRule best = null;
            foreach (var r in sounds) if (r.start <= n && (best == null || r.start > best.start)) best = r;
            var path = best == null ? null : string.Format(best.pattern, n - best.start);
            return path != null && db.Exists(path) ? path : fallbackSound;
        }

        public ClassAnim Class(int cls) => classes == null ? null : Array.Find(classes, c => c.cls == cls);

        /// 원작 0x183AD: 무기·방식·정령 무기·상대 편으로 연출 상태를 고르고 그 클립을 돌려준다
        public AnimClip Clip(int cls, int item, int mode, bool spirit, bool vsPlayer, System.Random rng = null)
        {
            var c = Class(cls);
            if (c?.clips == null || c.clips.Length == 0) return null;
            int state = 6;
            if (c.rules != null)
            {
                StateRule pick = null;
                foreach (var r in c.rules)
                    if ((r.item == item || (r.item == -1 && pick == null)) && r.mode == mode && (r.spirit != 0) == spirit && (r.vsPlayer != 0) == vsPlayer)
                        if (pick == null || r.item == item) pick = r;
                if (pick != null) state = pick.state;
            }
            if (state < 0) state = (rng ?? new System.Random()).Next(2) == 0 ? 6 : 7;
            return Array.Find(c.clips, k => k.state == state) ?? c.clips[0];
        }

        /// 정령 무기면 정령 번호(0~3), 아니면 -1. 원작: (아이템 플래그 >> 8) - 1
        public static int SpiritIndex(FarlandRules.Item weapon) => weapon != null && (weapon.flags & 0x20) != 0 ? ((weapon.flags >> 8) & 7) - 1 : -1;

        public string BackgroundLetter(int stage, int terrain, bool vsDemonKing)
        {
            if (stageLetters != null)
                foreach (var s in stageLetters)
                    if (s.stage == stage) return vsDemonKing && !string.IsNullOrEmpty(s.demonKingLetter) ? s.demonKingLetter : s.letter;
            return terrainLetters != null && terrain >= 0 && terrain < terrainLetters.Length ? terrainLetters[terrain] : "A";
        }
    }

    /// SpriteDefs/CHAR_xx.json (SPZ 프레임 스트립 정보)
    [Serializable] public class SpritePart { public int x, y, chip; }
    [Serializable] public class SpriteFrame { public int anchorX, anchorY; public SpritePart[] parts; }
    [Serializable]
    public class SpriteSheetDef
    {
        public string name; public int frameWidth, frameHeight, frameCount, chipSize = 16; public float pivotX, pivotY;
        public int[] screenOrigin; public SpriteFrame[] frames;

        /// 프레임에서 실제로 그려진 칩들의 중심 (프레임 이미지 좌상단 기준 픽셀)
        public Vector2 PartsCenter(int frame)
        {
            if (frames == null || frame < 0 || frame >= frames.Length || frames[frame].parts == null || frames[frame].parts.Length == 0)
                return new Vector2(frameWidth / 2f, frameHeight / 2f);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in frames[frame].parts)
            { x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x + chipSize); y1 = Mathf.Max(y1, p.y + chipSize); }
            int ox = screenOrigin != null && screenOrigin.Length > 1 ? screenOrigin[0] : 0, oy = screenOrigin != null && screenOrigin.Length > 1 ? screenOrigin[1] : 0;
            return new Vector2((x0 + x1) / 2f - ox, (y0 + y1) / 2f - oy);
        }
    }

    [Serializable]
    public class MapData
    {
        public string name, layout, tileset;
        public int width, height, tileSize;
        public int[] tiles;
    }

    // battle_data.json 중 배치·캐릭터 부분 (규칙 데이터는 FarlandRules.Data)
    [Serializable] public class PartySlot { public int slot, x, y; }
    /// 배치(0xA0B9): partyStart = 기존 동료 슬롯별 시작 위치, units = 템플릿 유닛
    [Serializable] public class Placement { public string stage; public PartySlot[] partyStart; public FarlandRules.Spawn[] units; }
    /// 스테이지 대본이 아군 템플릿으로 만드는 동료(합류)
    [Serializable] public class RecruitStage { public string stage; public FarlandRules.Spawn[] units; }
    [Serializable] public class Character { public int id; public string name; public int face = -1; }
    // 상점 (0xC86C): 스테이지 대본이 마을 좌표·마을별 대화·4칸 판매 목록을 넘긴다
    [Serializable] public class ShopItem { public int id; public string name; }
    [Serializable] public class Village { public int x, y; public string @event; public ShopItem[] items; }
    [Serializable] public class ShopStage { public string stage; public Village[] villages; }
    // 보물 상자 (0xB4A8 → DS:0x841C): 상태 1 숨김, 2 보임, 3 열림
    [Serializable] public class Chest { public int state, x, y, gold, item; }
    [Serializable] public class ChestStage { public string stage; public Chest[] chests; }
    [Serializable] public class BattlePlacements { public Placement[] stages; public Character[] characters; public ShopStage[] shops; public ChestStage[] chests; public RecruitStage[] recruits; }

    /// 게임이 쓰는 데이터 묶음. 모두 ContentDB 를 거쳐 읽으므로 팩으로 교체된다.
    public sealed partial class FarlandContent
    {
        public ContentDB DB { get; private set; }
        public GameConfig Game { get; private set; }
        public VisualConfig Visual { get; private set; }
        public UiStrings Text { get; private set; }
        public StageList Stages { get; private set; }
        public FarlandRules.Data Rules { get; private set; }
        public BattlePlacements Placements { get; private set; }
        public BattleSceneConfig BattleScene { get; private set; }
        readonly Dictionary<int, string> names = new Dictionary<int, string>();

        public static FarlandContent Load(ContentDB db)
        {
            var c = new FarlandContent { DB = db };
            c.Game = db.ReadJson<GameConfig>("Config/game.json") ?? new GameConfig();
            c.Visual = db.ReadJson<VisualConfig>("Config/visuals.json") ?? new VisualConfig();
            c.Text = db.ReadJson<UiStrings>("Config/strings.json") ?? new UiStrings();
            c.Stages = db.ReadJson<StageList>("Config/stages.json") ?? new StageList { stages = new StageDef[0] };
            c.BattleScene = db.ReadJson<BattleSceneConfig>("Config/battle_scene.json") ?? new BattleSceneConfig { enabled = false };
            var battleJson = db.ReadText(c.Game.battleData ?? "Battle/battle_data.json");
            c.Rules = FarlandRules.Load(battleJson);
            c.Placements = JsonUtility.FromJson<BattlePlacements>(battleJson);
            PadClasses(c.Rules);
            if (c.Placements.characters != null)
                foreach (var ch in c.Placements.characters) c.names[ch.id] = ch.name;
            // 대화용 이름표(characters.json)가 있으면 그것이 우선 — 이름만 바꾸는 팩이 이 파일 하나로 끝나도록
            var chars = db.ReadJson<CharacterList>(c.Game.charactersFile ?? "Dialogue/characters.json");
            if (chars?.characters != null) foreach (var ch in chars.characters) { c.names[ch.id] = ch.name; if (ch.face >= 0) c.faceOwner[ch.face] = ch.id; }
            c.ApplyGlossary();                                                 // Config/glossary.json: 이름·클래스·아이템 일괄 바꾸기
            return c;
        }

        [Serializable] class CharacterList { public Character[] characters; }

        /// 클래스 표는 0~41만 있다. 구출 NPC 60·61은 원작 그림 매핑(0x65AF)처럼 바사카(12)·왈큐레(15) 성능을 빌린다.
        static void PadClasses(FarlandRules.Data d)
        {
            if (d.classes.Length > 61) return;
            var list = new List<FarlandRules.ClassInfo>(d.classes);
            while (list.Count < 62)
            {
                int id = list.Count;
                var src = d.classes[id == 60 ? 12 : id == 61 ? 15 : 0];
                list.Add(new FarlandRules.ClassInfo { id = id, name = src.name, moveCost = src.moveCost, female = src.female, growth = src.growth });
            }
            d.classes = list.ToArray();
        }

        public string NameOf(int nameId) => names.TryGetValue(nameId, out var n) ? n : "???";

        public StageDef Stage(string id) => Array.Find(Stages.stages, s => s.id == id);

        public Placement PlacementOf(string stage) => Array.Find(Placements.stages, p => p.stage == stage);

        public string MusicPath(int music) => music <= 0 ? null : string.Format(Game.musicFile, music + Game.musicTrackOffset);

        public string FacePath(int face) => face < 0 || string.IsNullOrEmpty(Visual.facePattern) ? null : string.Format(Visual.facePattern, face);

        /// 맵 유닛 그림 칩 번호 (FSMAIN 0x65AF)
        public int MapChip(int cls, bool greyed)
        {
            if (Visual.classChip != null)
                foreach (var m in Visual.classChip) if (m.cls == cls) return m.chip;
            return cls <= Visual.actedMaxClass && greyed ? cls + Visual.actedChipOffset : cls;
        }

        public Sprite MapUnitSprite(int chip)
        {
            int s = Visual.mapUnitSize, cols = Visual.mapUnitColumns;
            return DB.Sprite(Visual.mapUnitSheet, new RectInt(chip % cols * s, chip / cols * s, s, s), new Vector2(0.5f, 0.5f), s);
        }

        public Sprite IconSprite(int index)
        {
            int s = Visual.iconSize, cols = Visual.iconColumns;
            return DB.Sprite(Visual.iconSheet, new RectInt(index % cols * s, index / cols * s, s, s), new Vector2(0.5f, 0.5f), s);
        }

        public Sprite TileSprite(string tileset, int index)
        {
            int s = Visual.tileSize, cols = Visual.tileColumns;
            return DB.Sprite("Tilesets/" + tileset + ".png", new RectInt(index % cols * s, index / cols * s, s, s), new Vector2(0.5f, 0.5f), s);
        }
    }
}

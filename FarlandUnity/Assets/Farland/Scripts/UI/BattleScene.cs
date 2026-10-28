using System.Collections;
using Farland.Content;
using Farland.Game;
using UnityEngine;
using UnityEngine.UI;

namespace Farland.UI
{
    /// 교전 연출 화면(원작 전투 화면을 640×400 기준으로 재구성). 배경·스프라이트·클립은 모두 Config/battle_scene.json 과
    /// 콘텐츠 팩의 BattleMaps/SpriteDefs 에서 오므로 교체할 수 있다.
    /// 마법 이펙트는 원작처럼 시전자 SPZ 안의 이펙트 프레임을 별도 오브젝트로 띄우고(DS:0x6118 기록), 효과음은 원작 번호로 낸다.
    /// 클립 타이밍은 정적 추출 근사다.
    public class BattleScene : MonoBehaviour
    {
        FarlandContent content;
        BattleSceneConfig cfg;
        GameUI ui;
        GameObject root;
        RectTransform stage;
        RawImage background;
        Side left, right, effect;
        BitmapText centerText;
        SfxPlayer sfx;

        class Side
        {
            public RectTransform rect; public Image image; public BitmapText name, level; public RectTransform bar;
            public GameUnit unit; public string sheet; public SpriteSheetDef def; public Vector2 home;
            public bool flip;
        }

        public bool Enabled => cfg != null && cfg.enabled;

        public void Init(FarlandContent content, Transform canvas, GameUI ui)
        {
            this.content = content; this.ui = ui;
            cfg = content.BattleScene;
            root = new GameObject("BattleScene", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(canvas, false);
            var rr = (RectTransform)root.transform; rr.anchorMin = Vector2.zero; rr.anchorMax = Vector2.one; rr.offsetMin = rr.offsetMax = Vector2.zero;
            root.GetComponent<Image>().color = Color.clear;                // 원작처럼 위아래로 맵이 보인다(클릭은 막는다)

            stage = new GameObject("Stage", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            stage.SetParent(rr, false);
            stage.sizeDelta = new Vector2(cfg.screenWidth, cfg.screenHeight);
            stage.localScale = Vector3.one * 2f;                          // 캔버스 기준 1280×800 = 원작 640×400 ×2

            background = new GameObject("Background", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            background.rectTransform.SetParent(stage, false);
            Place(background.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, cfg.backgroundY), new Vector2(cfg.screenWidth, 240));
            foreach (var ly in new[] { cfg.backgroundY - 2, cfg.backgroundY + 240 })    // 띠 위아래의 흰 선
            {
                var line = new GameObject("Line", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                line.rectTransform.SetParent(stage, false); line.raycastTarget = false; line.color = new Color(0.93f, 0.93f, 0.93f);
                Place(line.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, ly), new Vector2(cfg.screenWidth, 2));
            }

            left = MakeSide("Left", true);
            right = MakeSide("Right", false);
            var fx = new GameObject("Effect", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            fx.raycastTarget = false; fx.rectTransform.SetParent(stage, false); fx.enabled = false;
            effect = new Side { image = fx, rect = fx.rectTransform };
            sfx = gameObject.AddComponent<SfxPlayer>(); sfx.Init(content);
            // 아래쪽 창 세 개(원작): 왼쪽 유닛 / 메시지 / 오른쪽 유닛
            var center = (RectTransform)ui.Window(rr, "Message", "normal", GameUI.P(194, 284), GameUI.S(252, 68), new Vector2(0, 1)).transform;
            centerText = ui.MakeText(center, "Text", TextAnchor.UpperLeft, Vector2.zero, Vector2.zero, stretch: true, pad: 12);
            Panel(left, rr, 18); Panel(right, rr, 450);
            root.SetActive(false);
        }

        // 원작 화면 좌표(좌상단 원점, 640×400) 기준 배치
        void Place(RectTransform r, Vector2 pivot, Vector2 anchorTopLeft, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = new Vector2(0, 1);
            r.pivot = pivot;
            r.anchoredPosition = new Vector2(pos.x, -pos.y);
            r.sizeDelta = size;
        }

        /// 유닛 창: 이름, LV, HP 게이지(초록 = 남은 HP, 빨강 = 잃은 HP)
        void Panel(Side s, RectTransform parent, int x)
        {
            var w = (RectTransform)ui.Window(parent, "Unit", "normal", GameUI.P(x, 284), GameUI.S(172, 68), new Vector2(0, 1)).transform;
            s.name = ui.MakeText(w, "Name", TextAnchor.UpperLeft, GameUI.P(12, 10), GameUI.S(100, 16));
            s.name.wrap = false;
            s.level = ui.MakeText(w, "Lv", TextAnchor.UpperRight, GameUI.P(112, 10), GameUI.S(48, 16));
            var g = content.Visual.hpGauge;
            var back = new GameObject("Gauge", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            back.rectTransform.SetParent(w, false); back.raycastTarget = false;
            back.color = VisualConfig.ParseColor(g?.lost, new Color(0.87f, 0.13f, 0.27f));
            var br = back.rectTransform; br.anchorMin = br.anchorMax = br.pivot = new Vector2(0, 1);
            br.anchoredPosition = GameUI.P(12, 36); br.sizeDelta = GameUI.S(64, 10);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            fill.rectTransform.SetParent(br, false); fill.raycastTarget = false;
            fill.color = VisualConfig.ParseColor(g?.fill, new Color(0.4f, 0.73f, 0.13f));
            s.bar = fill.rectTransform; s.bar.anchorMin = Vector2.zero; s.bar.anchorMax = Vector2.one; s.bar.offsetMin = s.bar.offsetMax = Vector2.zero;
            s.bar.pivot = new Vector2(0, 0.5f);
        }

        Side MakeSide(string name, bool isLeft)
        {
            var s = new Side { flip = isLeft };
            var feet = isLeft ? cfg.enemyFeet : cfg.allyFeet;
            s.home = new Vector2(feet[0], feet[1]);
            s.image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            s.image.raycastTarget = false;
            s.rect = s.image.rectTransform; s.rect.SetParent(stage, false);
            return s;
        }

        void Bind(Side s, GameUnit u)
        {
            s.unit = u;
            s.sheet = string.Format(cfg.sheetPattern, u.stats.cls);
            s.def = content.DB.ReadJson<SpriteSheetDef>(s.sheet + ".json");
            s.name.text = u.Name;
            s.level.text = $"LV {u.stats.level,2}";
            SetHp(s, u.stats.hp);
            SetPos(s, s.home);
            SetFrame(s, cfg.common.stance);
        }

        void SetHp(Side s, int hp)
        {
            s.bar.localScale = new Vector3(Mathf.Clamp01(hp / (float)Mathf.Max(1, s.unit.stats.maxHp)), 1, 1);
        }

        void SetPos(Side s, Vector2 feet) => s.rect.anchoredPosition = new Vector2(feet.x, -feet.y);

        /// 프레임의 그려진 부분 중심을 center(원작 화면 좌표)에 맞춘다 — 화면 절대 위치로 그려진 이펙트용
        void SetFrameCentered(Side s, int frame, Vector2 center)
        {
            SetFrame(s, frame);
            if (s.def == null) return;
            var c = s.def.PartsCenter(Mathf.Clamp(frame, 0, s.def.frameCount - 1));
            s.rect.pivot = new Vector2(c.x / s.def.frameWidth, 1f - c.y / s.def.frameHeight);   // 뒤집어도 피벗 점은 제자리
            SetPos(s, center);
        }

        void SetFrame(Side s, int frame)
        {
            if (s.def == null) { s.image.enabled = false; return; }
            frame = Mathf.Clamp(frame, 0, s.def.frameCount - 1);
            // 캐릭터 폴더의 전투 프레임(Characters/NN/battle/000.png)이 있으면 클래스 그림 대신. 크기·기준점은 클래스 정의와 같게
            var sp = s.unit != null && s != effect ? content.CharacterBattleFrame(s.unit.stats.nameId, s.unit.stats.cls, frame, new Vector2(s.def.pivotX, s.def.pivotY)) : null;
            sp ??= content.DB.Sprite(s.sheet + "_frames.png", new RectInt(frame * s.def.frameWidth, 0, s.def.frameWidth, s.def.frameHeight),
                                       new Vector2(s.def.pivotX, s.def.pivotY), 1f);
            s.image.enabled = sp != null;
            s.image.sprite = sp;
            s.rect.anchorMin = s.rect.anchorMax = new Vector2(0, 1);
            s.rect.pivot = new Vector2(s.def.pivotX, s.def.pivotY);
            s.rect.sizeDelta = new Vector2(s.def.frameWidth, s.def.frameHeight);
            s.rect.localScale = new Vector3(s.flip ? -1 : 1, 1, 1);      // SPZ 는 왼쪽을 본다 → 왼편(적)은 뒤집는다
        }

        float Step => cfg.frameTime / (Input.GetKey(KeyCode.Space) || Input.GetMouseButton(1) ? 3f : 1f);

        public IEnumerator Play(CombatEvent ce, int stageNo)
        {
            var ally = ce.attacker.IsPlayerSide ? ce.attacker : ce.defender;
            var enemy = ally == ce.attacker ? ce.defender : ce.attacker;
            root.SetActive(true);
            bool demonKing = ce.attacker.stats.cls == 41 || ce.defender.stats.cls == 41;
            int hp0Ally = ce.strikes.Count > 0 ? HpBefore(ce, ally) : ally.stats.hp, hp0Enemy = ce.strikes.Count > 0 ? HpBefore(ce, enemy) : enemy.stats.hp;
            var letter = cfg.BackgroundLetter(stageNo, ce.defenderTerrain, demonKing);
            var tex = content.DB.Texture(string.Format(cfg.background, letter));
            background.texture = tex;
            background.uvRect = tex != null ? new Rect(0.5f - cfg.screenWidth / 2f / tex.width, 0, cfg.screenWidth / (float)tex.width, 1) : new Rect(0, 0, 1, 1);
            Bind(left, ally); Bind(right, enemy);                      // 원작: 아군 왼쪽(창도 왼쪽), 적 오른쪽
            SetHp(left, hp0Ally); SetHp(right, hp0Enemy);
            centerText.text = "";
            yield return sfx.Preload(SoundsFor(ce));
            yield return Wait(4);

            foreach (var st in ce.strikes)
            {
                var a = st.attacker == ally ? left : right;
                var t = a == right ? left : right;
                var clip = ClipOf(st.attacker, st.target, ce.mode) ?? new AnimClip { frames = new[] { cfg.common.stance }, hitIndex = 0 };
                int hit = Mathf.Clamp(clip.hitIndex, 0, clip.frames.Length - 1);
                Vector2 target = new Vector2(t.home.x + (a == right ? cfg.meleeGap : -cfg.meleeGap), a.home.y);
                bool fxDone = clip.effectAt < 0 || clip.effectFrames == null || clip.effectFrames.Length == 0;
                for (int i = 0; i < hit; i++)                                   // 피해 전: 달려가며 준비 동작
                {
                    if (!fxDone && i == clip.effectAt) { fxDone = true; yield return Cast(a, t, InlineEffect(clip)); }
                    SetFrame(a, clip.frames[i]); Cue(clip, i);
                    if (clip.run) SetPos(a, Vector2.Lerp(a.home, target, (i + 1f) / hit));
                    yield return Wait(1);
                }
                SetFrame(a, clip.frames[hit]); Cue(clip, hit);
                if (!fxDone) yield return Cast(a, t, InlineEffect(clip));         // 클래스 고유 이펙트(창·화살 등)
                var spell = SpellOf(clip, st.attacker);
                if (spell != null) yield return Cast(a, t, spell);
                var hs = cfg.hitSounds ?? new HitSounds();
                var T = content.Text;
                if (st.heal > 0 || st.song)                                   // 같은 편: 회복(0x1CBCC)·격려의 노래
                {
                    SetHp(t, st.hpAfter);
                    centerText.text = st.song ? st.attacker.Name + FarlandStageScript.Particle(st.attacker.stats.nameId) + (T.song ?? " 격려의 노래를 연주하다")
                        : st.hpAfter >= t.unit.stats.maxHp ? string.Format(T.fullyHealed, t.unit.Name)
                        : string.Format(T.healed, t.unit.Name, $"<color=#FFE08A>{st.heal}</color>");
                    for (int i = hit + 1; i < clip.frames.Length; i++) { SetFrame(a, clip.frames[i]); Cue(clip, i); yield return Wait(1); }
                    SetFrame(a, cfg.common.stance);
                    yield return Wait(6);
                    continue;
                }
                sfx.Play(st.damage == 0 ? hs.miss : st.critical ? hs.critical : hs.hit);
                SetFrame(t, cfg.common.hit);
                SetHp(t, st.hpAfter);
                // 원작: 가운데 창에 'OO에게 20 의 DAMAGE'
                centerText.text = (st.critical ? T.critical + "\n" : "") +
                                  (st.damage > 0 ? string.Format(T.damage, t.unit.Name, $"<color=#FFE08A>{st.damage}</color>") : T.miss);
                yield return Flash(t, st.damage > 0);
                for (int i = hit + 1; i < clip.frames.Length; i++) { SetFrame(a, clip.frames[i]); Cue(clip, i); yield return Wait(1); }
                if (st.hpAfter <= 0)
                {
                    SetFrame(t, cfg.common.fall); yield return Wait(3);
                    SetFrame(t, cfg.common.down); yield return Wait(4);
                }
                else SetFrame(t, cfg.common.stance);
                if (clip.run)                                                   // 제자리로
                    for (int i = 1; i <= 4; i++) { SetPos(a, Vector2.Lerp(target, a.home, i / 4f)); yield return Wait(1); }
                SetFrame(a, cfg.common.stance);
                yield return Wait(3);
            }
            yield return Wait(5);
            root.SetActive(false);
        }

        AnimClip ClipOf(GameUnit u, GameUnit target, int mode)
        {
            var w = Weapon(u.stats.item);
            return cfg.Clip(u.stats.cls, u.stats.item, mode, BattleSceneConfig.SpiritIndex(w) >= 0, target.stats.cls < 18, rng);
        }

        FarlandRules.Item Weapon(int id) => id > 0 && id <= content.Rules.items.Length ? content.Rules.items[id - 1] : null;

        /// 공용 마법. "spirit" 은 무기의 정령 번호로 고른다(엘프 계열, 0x1EB2D)
        SpellDef SpellOf(AnimClip clip, GameUnit caster)
        {
            if (clip?.spell == "spirit")
            {
                int k = BattleSceneConfig.SpiritIndex(Weapon(caster.stats.item));
                return cfg.spiritSpells != null && k >= 0 && k < cfg.spiritSpells.Length ? cfg.Spell(cfg.spiritSpells[k]) : null;
            }
            return cfg.Spell(clip?.spell);
        }

        static SpellDef InlineEffect(AnimClip clip) => new SpellDef { name = "inline", frames = clip.effectFrames, move = clip.effectMove };

        readonly System.Random rng = new System.Random();

        void Cue(AnimClip clip, int index)
        {
            if (clip.sounds == null) return;
            foreach (var c in clip.sounds) if (c.at == index) sfx.Play(c.sound);
        }

        IEnumerable SoundsForClip(AnimClip clip)
        {
            if (clip?.sounds != null) foreach (var c in clip.sounds) yield return c.sound;
            var sp = cfg.Spell(clip?.spell);
            if (sp != null && sp.sound >= 0) yield return sp.sound;
        }

        System.Collections.Generic.IEnumerable<int> SoundsFor(CombatEvent ce)
        {
            var hs = cfg.hitSounds ?? new HitSounds();
            yield return hs.hit; yield return hs.miss; yield return hs.critical;
            foreach (var (u, o) in new[] { (ce.attacker, ce.defender), (ce.defender, ce.attacker) })
            {
                var clip = ClipOf(u, o, ce.mode);
                foreach (int n in SoundsForClip(clip)) yield return n;
                var sp = SpellOf(clip, u);
                if (sp != null && sp.sound >= 0) yield return sp.sound;
            }
        }

        /// 마법: 시전자 시트의 이펙트 프레임을 띄운다. move 면 시전자 → 상대로 날아가고, 아니면 상대 위에서 재생
        IEnumerator Cast(Side caster, Side target, SpellDef sp)
        {
            effect.sheet = caster.sheet; effect.def = caster.def; effect.flip = caster.flip;
            if (!string.IsNullOrEmpty(sp.sheet))                               // 정령: 자기 SPZ 를 따로 쓴다
            {
                effect.sheet = sp.sheet;
                effect.def = content.DB.ReadJson<SpriteSheetDef>(sp.sheet + ".json");
                if (effect.def == null) yield break;
                if (sp.frames == null || sp.frames.Length == 0)
                {
                    sp = new SpellDef { name = sp.name, sheet = sp.sheet, sound = sp.sound, move = false, soundFrames = sp.soundFrames, launch = sp.launch,
                                        frames = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(0, effect.def.frameCount)) };
                }
            }
            if (sp.sound >= 0 && (sp.soundFrames == null || sp.soundFrames.Length == 0)) sfx.Play(sp.sound);
            var from = caster.home; var to = target.home;
            if (sp.launch != null)
                foreach (var f in sp.launch) { SetPos(effect, from); SetFrame(effect, f); yield return Wait(1); }
            var frames = sp.frames != null && sp.frames.Length > 0 ? sp.frames : new[] { cfg.common.stance };
            if (sp.move)
            {
                int steps = Mathf.Max(3, Mathf.RoundToInt(Mathf.Abs(to.x - from.x) / cfg.projectileSpeed / Mathf.Max(0.01f, cfg.frameTime)));
                for (int i = 0; i <= steps; i++)
                {
                    SetPos(effect, Vector2.Lerp(from, to, i / (float)steps));
                    SetFrame(effect, frames[i % frames.Length]);
                    yield return Wait(1);
                }
            }
            else
            {
                var body = to + new Vector2(0, -cfg.bodyHeight);              // 상대 몸 중심
                foreach (var f in frames)
                {
                    SetFrameCentered(effect, f, body);
                    if (sp.soundFrames != null && System.Array.IndexOf(sp.soundFrames, f) >= 0) sfx.Play(sp.sound);
                    yield return Wait(1);
                }
            }
            effect.image.enabled = false;
        }

        static int HpBefore(CombatEvent ce, GameUnit u)
        {
            foreach (var s in ce.strikes) if (s.target == u) return s.hpBefore;
            return u.stats.hp;
        }

        IEnumerator Wait(int frames) { for (float t = 0; t < frames * Step; t += Time.deltaTime) yield return null; }

        IEnumerator Flash(Side s, bool damaged)
        {
            for (int i = 0; i < (damaged ? 3 : 1); i++)
            {
                s.image.color = new Color(1, 1, 1, 0.2f); yield return Wait(1);
                s.image.color = Color.white; yield return Wait(1);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Farland.Content;
using Farland.Game;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Farland.UI
{
    /// 코드로 만드는 UGUI. 배치는 원작 화면(640×400)을 2배 한 1280×800 캔버스 기준이다.
    /// 창 그림(WINCHIP)·비트맵 글꼴·색·문구는 콘텐츠 팩(visuals.json, strings.json, UI/, Fonts/)에서 온다.
    public class GameUI : MonoBehaviour
    {
        public const int K = 2;                    // 원작 1픽셀 = 캔버스 2픽셀

        FarlandContent content;
        Canvas canvas;
        RectTransform rootRect;
        BitmapText phaseText, infoText, logText, helpText, hoverText;
        GameObject phasePanel, infoPanel, actionPanel, dialoguePanel, titlePanel, endPanel, listPanel, listDim, helpBar, hoverPanel;
        BitmapText listTitle, listSub;
        RectTransform listGrid, listButtons;
        Image leftFace, rightFace, titleImage, nextMark;
        BitmapText dialogueName, dialogueText, titleText, endText;
        readonly List<string> log = new List<string>();
        bool advance, helpAllowed, helpOn, turnControls;
        float phaseHideAt, logHideAt;

        public bool DialogueOpen => dialoguePanel.activeSelf;
        public BattleScene Battle { get; private set; }
        public Action onEndTurn, onSave;

        // 원작 좌표(좌상단 원점) → 캔버스
        public static Vector2 P(float x, float y) => new Vector2(x * K, -y * K);
        public static Vector2 S(float w, float h) => new Vector2(w * K, h * K);

        public void Init(FarlandContent content)
        {
            this.content = content;
            BitmapFont.Load(content);

            if (FindObjectOfType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            canvas = new GameObject("UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = true;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0.5f;
            rootRect = (RectTransform)canvas.transform;
            SetCursorImage();

            // 맵 위 표시: 단계(잠깐), 메시지, 마우스가 올라간 유닛의 HP(원작처럼 유닛 옆 '48/ 48')
            phasePanel = Window(rootRect, "Phase", "normal", P(320 - 88, 8), S(176, 32), new Vector2(0, 1));
            phaseText = MakeText((RectTransform)phasePanel.transform, "Text", TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, stretch: true);
            phasePanel.SetActive(false);
            logText = MakeText(rootRect, "Log", TextAnchor.UpperLeft, P(8, 8), S(400, 100));
            logText.gameObject.AddComponent<Outline>().effectColor = Color.black;
            hoverPanel = new GameObject("Hover", typeof(RectTransform));
            hoverPanel.transform.SetParent(rootRect, false);
            hoverText = MakeText((RectTransform)hoverPanel.transform, "Text", TextAnchor.MiddleLeft, Vector2.zero, S(80, 16));
            var ho = hoverText.gameObject.AddComponent<Outline>(); ho.effectColor = Color.black; ho.effectDistance = new Vector2(2, -2);
            hoverPanel.SetActive(false);

            helpBar = Window(rootRect, "Help", "normal", P(8, 400 - 8 - 28), S(624, 28), new Vector2(0, 1));
            helpText = MakeText((RectTransform)helpBar.transform, "Text", TextAnchor.MiddleLeft, Vector2.zero, Vector2.zero, stretch: true, pad: 10);
            helpText.scale = 1; helpText.text = content.Text.help;
            helpBar.SetActive(false);

            infoPanel = Window(rootRect, "Info", "normal", P(640 - 8 - 184, 8), S(184, 196), new Vector2(0, 1));
            infoText = MakeText((RectTransform)infoPanel.transform, "Text", TextAnchor.UpperLeft, Vector2.zero, Vector2.zero, stretch: true, pad: 12);
            infoText.lineGap = 2;
            infoPanel.SetActive(false);

            actionPanel = Window(rootRect, "Actions", "normal", P(0, 0), S(104, 40), new Vector2(0, 1));
            var vl = actionPanel.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(10 * K, 10 * K, 8 * K, 9 * K); vl.spacing = 0;
            vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
            actionPanel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            actionPanel.SetActive(false);

            // 대사: 원작 FSMAIN 대화창 (66,244)-(574,352). 얼굴은 테두리 없이 창 위에, 이름은 왼쪽 칸, 대사는 「」 부터
            dialoguePanel = new GameObject("Dialogue", typeof(RectTransform), typeof(Image));
            dialoguePanel.transform.SetParent(rootRect, false);
            Stretch((RectTransform)dialoguePanel.transform);
            dialoguePanel.GetComponent<Image>().color = Color.clear;          // 화면 어디를 눌러도 넘긴다
            leftFace = MakeImage((RectTransform)dialoguePanel.transform, "LeftFace", P(88, 132), S(112, 112));
            rightFace = MakeImage((RectTransform)dialoguePanel.transform, "RightFace", P(440, 132), S(112, 112));
            var dw = (RectTransform)Window((RectTransform)dialoguePanel.transform, "Window", "normal", P(66, 244), S(508, 108), new Vector2(0, 1)).transform;
            dialogueName = MakeText(dw, "Name", TextAnchor.UpperLeft, P(10, 10), S(120, 20));
            dialogueText = MakeText(dw, "Text", TextAnchor.UpperLeft, P(138, 10), S(356, 92));
            dialogueText.lineGap = 6;
            nextMark = MakeImage(dw, "Next", P(474, 76), S(24, 24));
            var click = dialoguePanel.AddComponent<Button>();
            click.transition = Selectable.Transition.None;
            click.onClick.AddListener(() => advance = true);
            dialoguePanel.SetActive(false);

            endPanel = Window(rootRect, "End", "normal", P(320 - 96, 200 - 28), S(192, 56), new Vector2(0, 1));
            endText = MakeText((RectTransform)endPanel.transform, "Text", TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, stretch: true);
            endPanel.SetActive(false);

            titlePanel = new GameObject("Title", typeof(RectTransform), typeof(Image));
            titlePanel.transform.SetParent(rootRect, false);
            Stretch((RectTransform)titlePanel.transform);
            titlePanel.GetComponent<Image>().color = Color.black;
            titleImage = MakeImage((RectTransform)titlePanel.transform, "Image", P(0, 0), S(640, 400));
            titleImage.preserveAspect = true;
            titleText = MakeText((RectTransform)titlePanel.transform, "Text", TextAnchor.MiddleCenter, P(0, 150), S(640, 40));
            titlePanel.SetActive(false);

            // 목록 창(상점·소지품·대상 고르기·세이브). 뒤를 어둡게 덮어 맵을 가린다
            listDim = new GameObject("ListDim", typeof(RectTransform), typeof(Image));
            listDim.transform.SetParent(rootRect, false);
            Stretch((RectTransform)listDim.transform);
            listDim.GetComponent<Image>().color = new Color(0, 0, 0, 0.45f);
            listDim.SetActive(false);
            listPanel = Window(rootRect, "List", "solid", P(20, 12), S(600, 376), new Vector2(0, 1));
            var lr = (RectTransform)listPanel.transform;
            listTitle = MakeText(lr, "Title", TextAnchor.UpperLeft, P(14, 10), S(380, 20));
            listSub = MakeText(lr, "Sub", TextAnchor.UpperRight, P(600 - 14 - 220, 10), S(220, 20));
            listSub.color = new Color(1f, 0.88f, 0.54f);
            listGrid = new GameObject("Grid", typeof(RectTransform), typeof(GridLayoutGroup)).GetComponent<RectTransform>();
            listGrid.SetParent(lr, false);
            listGrid.anchorMin = listGrid.anchorMax = listGrid.pivot = new Vector2(0, 1);
            listGrid.anchoredPosition = P(12, 36); listGrid.sizeDelta = S(576, 300);
            var grid = listGrid.GetComponent<GridLayoutGroup>();
            grid.spacing = Vector2.zero; grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            listButtons = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
            listButtons.SetParent(lr, false);
            listButtons.anchorMin = listButtons.anchorMax = listButtons.pivot = new Vector2(1, 0);
            listButtons.anchoredPosition = new Vector2(-12 * K, 10 * K); listButtons.sizeDelta = S(560, 20);
            var hl = listButtons.GetComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.LowerRight; hl.spacing = 12 * K; hl.childForceExpandWidth = false; hl.childControlWidth = true; hl.childControlHeight = true;
            listPanel.SetActive(false);

            Battle = gameObject.AddComponent<BattleScene>();
            Battle.Init(content, rootRect, this);
        }

        void Update()
        {
            if (phasePanel.activeSelf && phaseHideAt > 0 && Time.unscaledTime > phaseHideAt) phasePanel.SetActive(false);
            if (logHideAt > 0 && Time.unscaledTime > logHideAt) { ClearMessages(); logHideAt = 0; }
            if (nextMark.gameObject.activeInHierarchy) nextMark.sprite = CursorSprite(5 + (int)(Time.unscaledTime * 3) % 2);
            if (helpAllowed && Input.GetKeyDown(KeyCode.F1)) { helpOn = !helpOn; helpBar.SetActive(helpOn); }
        }

        // ---- 공용 생성 ----
        static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }

        public BitmapText MakeText(RectTransform parent, string name, TextAnchor align, Vector2 pos, Vector2 size, bool stretch = false, float pad = 0)
        {
            var t = new GameObject(name, typeof(RectTransform), typeof(BitmapText)).GetComponent<BitmapText>();
            t.transform.SetParent(parent, false);
            var r = t.rectTransform;
            if (stretch) { Stretch(r); r.offsetMin = new Vector2(pad * K, pad * K * 0.75f); r.offsetMax = new Vector2(-pad * K, -pad * K * 0.75f); }
            else { r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = pos; r.sizeDelta = size; }
            t.alignment = align; t.color = Color.white; t.raycastTarget = false;
            return t;
        }

        Image MakeImage(RectTransform parent, string name, Vector2 pos, Vector2 size)
        {
            var img = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            img.transform.SetParent(parent, false);
            var r = img.rectTransform; r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = pos; r.sizeDelta = size;
            img.raycastTarget = false;
            return img;
        }

        readonly Dictionary<string, Sprite> skins = new Dictionary<string, Sprite>();

        /// 원작 창(9조각, 가운데·가장자리는 바둑판 무늬가 깨지지 않게 타일). style: normal(반투명) / solid / menu(보라)
        public Sprite Skin(string style)
        {
            if (skins.TryGetValue(style, out var s)) return s;
            var w = content.Visual.windows;
            var def = style == "solid" ? w?.solid : style == "menu" ? w?.menu : w?.normal;
            var tex = def != null ? content.DB.Texture(def.image) : null;
            if (tex != null)
            {
                var b = def.border != null && def.border.Length == 4 ? new Vector4(def.border[0], def.border[1], def.border[2], def.border[3]) : Vector4.zero;
                s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, b);
            }
            skins[style] = s;
            return s;
        }

        public GameObject Window(RectTransform parent, string name, string style, Vector2 pos, Vector2 size, Vector2 pivot)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform; r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
            StyleWindow(go.GetComponent<Image>(), style);
            return go;
        }

        public void StyleWindow(Image img, string style)
        {
            var sp = Skin(style);
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Tiled; img.pixelsPerUnitMultiplier = 1f / K; img.color = Color.white; }
            else img.color = new Color(0.2f, 0.4f, 0.73f, 0.85f);
        }

        /// 글자만 있는 메뉴 줄(원작 메뉴처럼 배경 없이, 마우스가 올라가면 밝은 띠)
        Button MakeRow(Transform parent, string label, Action onClick, float height = 20)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement), typeof(RectMask2D));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.color = Color.white;
            var b = go.GetComponent<Button>();
            var cb = b.colors;
            cb.normalColor = new Color(1, 1, 1, 0); cb.highlightedColor = new Color(1, 1, 1, 0.22f); cb.pressedColor = new Color(1, 1, 1, 0.35f);
            cb.selectedColor = new Color(1, 1, 1, 0.22f);                  // 패드 초점도 마우스 올림과 같은 띠 cb.disabledColor = new Color(1, 1, 1, 0); cb.fadeDuration = 0;
            b.colors = cb;
            go.GetComponent<LayoutElement>().preferredHeight = height * K;
            var t = MakeText((RectTransform)go.transform, "Label", TextAnchor.MiddleLeft, Vector2.zero, Vector2.zero, stretch: true, pad: 4);
            t.wrap = false; t.text = label;
            go.GetComponent<LayoutElement>().preferredWidth = t.preferredWidth + 8 * K;
            b.onClick.AddListener(() => onClick());
            return b;
        }

        // ---- 커서(MCSRFONT: 24×24 아이콘 13개) ----
        Sprite CursorSprite(int i)
        {
            int n = content.Visual.cursorSize;
            return string.IsNullOrEmpty(content.Visual.cursorImage) ? null : content.DB.Sprite(content.Visual.cursorImage, new RectInt(i * n, 0, n, n), new Vector2(0.5f, 0.5f), 100);
        }

        void SetCursorImage()
        {
            var sp = CursorSprite(0);                                   // 낱장(MCSRFONT/000.png)이 있으면 그것
            if (sp == null) return;
            var src = sp.texture; var tr = sp.textureRect;
            int w = (int)tr.width, h = (int)tr.height;
            var cur = new Texture2D(w * K, h * K, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            for (int y = 0; y < h * K; y++)
                for (int x = 0; x < w * K; x++)
                    cur.SetPixel(x, y, src.GetPixel((int)tr.x + x / K, (int)tr.y + y / K));
            cur.Apply();
            Cursor.SetCursor(cur, Vector2.zero, CursorMode.Auto);
        }

        // ---- 화면 요소 ----
        public bool PointerOverUI => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        /// 메뉴·목록이 떠 있어 패드 A/B 를 UI 가 받아야 할 때
        public bool MenuOpen => actionPanel.activeSelf || listPanel.activeSelf || titlePanel.activeSelf;

        /// 패드로 고를 수 있게 첫 줄에 초점(EventSystem 의 Submit = A, Cancel = B)
        void FocusFirst(Transform parent)
        {
            if (EventSystem.current == null) return;
            foreach (Transform c in parent)
            {
                var b = c.GetComponent<Button>();
                if (b != null && b.interactable && c.gameObject.activeSelf) { EventSystem.current.SetSelectedGameObject(c.gameObject); return; }
            }
        }

        public void ToggleHelp() { helpOn = !helpOn; helpBar.SetActive(helpAllowed && helpOn); }

        /// 단계 표시(스테이지 제목·아군 턴 등). 잠깐 보였다 사라진다
        public void SetPhase(string text)
        {
            phasePanel.SetActive(!string.IsNullOrEmpty(text));
            phaseText.text = text ?? "";
            var r = (RectTransform)phasePanel.transform;
            int w = Mathf.Max(120, Mathf.CeilToInt(phaseText.preferredWidth / K) + 32);
            w += w & 1;
            r.sizeDelta = S(w, 32); r.anchoredPosition = P(320 - w / 2, 8);
            phaseHideAt = Time.unscaledTime + 1.6f;
        }

        /// 대화 한 페이지 넘기기(개발용 자동 진행)
        public void AdvanceDialogue() => advance = true;

        /// 아군 턴 조작 가능 여부. 원작처럼 버튼은 두지 않고 오른쪽 클릭 메뉴(시스템 메뉴)로 턴 종료·저장을 한다
        public void SetEndTurnVisible(bool on) { turnControls = on; if (!on && systemMenuOpen) HideActions(); }

        /// 조작 설명 띠(F1 또는 시스템 메뉴로 켜고 끈다). 전투 맵에서만
        public void SetHelpVisible(bool on) { helpAllowed = on; helpBar.SetActive(on && helpOn); }

        public void ClearMessages() { log.Clear(); logText.text = ""; }

        public void Message(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            log.Add(text);
            if (log.Count > 4) log.RemoveAt(0);
            logText.text = string.Join("\n", log);
            logHideAt = Time.unscaledTime + 6f;
        }

        /// 마우스가 올라간 유닛 옆에 HP 를 표시한다(원작 '48/ 48'). screen: 유닛 칸 오른쪽 가장자리의 화면 좌표
        public void ShowHover(GameUnit u, Vector2 screen)
        {
            if (u == null || dialoguePanel.activeSelf || listPanel.activeSelf) { hoverPanel.SetActive(false); return; }
            hoverPanel.SetActive(true);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRect, screen, null, out var local);
            var r = (RectTransform)hoverPanel.transform; r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(Mathf.Round(local.x / K) * K + 16 * K, Mathf.Round(local.y / K) * K - 16 * K);   // 유닛 오른쪽, 세로 가운데
            hoverText.text = $"{Mathf.Max(0, u.stats.hp),3}/{u.stats.maxHp,3}";
        }

        /// 고른 유닛의 능력치 창(원작 도움말 HELP09 의 항목 순서). 화면 반대편 위쪽에 둔다
        public void ShowUnit(GameUnit u, FarlandRules.Item weapon, string className)
        {
            if (u == null || listPanel.activeSelf || dialoguePanel.activeSelf) { infoPanel.SetActive(false); return; }
            infoPanel.SetActive(true);
            var r = (RectTransform)infoPanel.transform;
            bool left = Input.mousePosition.x > Screen.width * 0.5f;
            r.anchoredPosition = P(left ? 8 : 640 - 8 - 184, 8);
            var T = content.Text; var s = u.stats;
            var sb = new StringBuilder();                                                    // 파란 창 위에서 읽히는 밝은 색
            sb.Append(u.Name).Append("  <color=").Append(u.IsPlayerSide ? "#B8E0FF" : "#FFB8C0").Append('>').Append(className).Append("</color>\n");
            sb.AppendFormat("{0} {1,2}  {2} {3}/{4}\n", T.lv, s.level, T.hp, Math.Max(0, s.hp), s.maxHp);
            sb.AppendFormat("{0}  {1}\n", Pad(T.atk), s.attack);
            sb.AppendFormat("{0}  {1}\n", Pad(T.def_), s.defense);
            sb.AppendFormat("{0}  {1}\n", Pad(T.know), s.knowledge);
            sb.AppendFormat("{0}  {1}\n", Pad(T.agi), s.agility);
            sb.AppendFormat("{0}  {1}\n", Pad(T.mdef), s.magicDefense);
            if (u.IsPlayerSide) sb.AppendFormat("{0}  {1}\n", Pad(T.exp), s.exp);
            if (weapon != null && !string.IsNullOrEmpty(weapon.name)) sb.Append("<color=#FFE08A>").Append(weapon.name).Append("</color>");
            infoText.text = sb.ToString();
        }

        static string Pad(string label) { int w = 0; foreach (var c in label) w += c >= 0xAC00 ? 2 : 1; return label + new string(' ', Math.Max(0, 10 - w)); }

        bool systemMenuOpen;
        public bool SystemMenuOpen => systemMenuOpen && actionPanel.activeSelf;

        /// 유닛 명령 메뉴. screen 이 있으면 그 화면 위치 오른쪽에 띄운다
        public void ShowActions(IList<(string label, Action action)> items, Vector2? screen = null)
        {
            systemMenuOpen = false;
            foreach (Transform c in actionPanel.transform) Destroy(c.gameObject);
            float w = 64;
            foreach (var (label, action) in items) { var b = MakeRow(actionPanel.transform, label, action); w = Mathf.Max(w, b.GetComponent<LayoutElement>().preferredWidth / K); }
            actionPanel.SetActive(items.Count > 0);
            var r = (RectTransform)actionPanel.transform;
            int width = Mathf.CeilToInt(w) + 20; width += width & 1;
            r.sizeDelta = new Vector2(width * K, r.sizeDelta.y);
            Vector2 pos = P(640 - 8 - width, 400 - 8 - (items.Count * 20 + 17));
            if (screen.HasValue)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRect, screen.Value, null, out var local);
                var half = rootRect.rect.size / 2f;
                float x = local.x + half.x + 8 * K, y = local.y - half.y + 20 * K;
                if (x + width * K > rootRect.rect.width - 8 * K) x = local.x + half.x - 40 * K - width * K;
                float h = (items.Count * 20 + 17) * K;
                x = Mathf.Clamp(x, 8 * K, rootRect.rect.width - width * K - 8 * K);
                y = Mathf.Clamp(y, -rootRect.rect.height + h + 8 * K, -8 * K);
                pos = new Vector2(Mathf.Round(x / K) * K, Mathf.Round(y / K) * K);
            }
            r.anchoredPosition = pos;
            StartCoroutine(FocusNextFrame(actionPanel.transform));
        }

        IEnumerator FocusNextFrame(Transform parent) { yield return null; FocusFirst(parent); }

        /// 오른쪽 클릭 메뉴(아군 턴, 아무것도 고르지 않았을 때): 턴 종료 / 저장 / 조작 설명 / 닫기
        public void ToggleSystemMenu()
        {
            if (SystemMenuOpen) { HideActions(); return; }
            if (!turnControls) return;
            var T = content.Text;
            ShowActions(new List<(string, Action)>
            {
                (T.endTurn, () => { HideActions(); onEndTurn?.Invoke(); }),
                (T.saveGame, () => { HideActions(); onSave?.Invoke(); }),
                (T.controls ?? "조작 설명", () => { HideActions(); helpOn = !helpOn; helpBar.SetActive(helpAllowed && helpOn); }),
                (T.close, HideActions),
            }, Input.mousePosition);
            systemMenuOpen = true;
        }

        public void HideActions() { actionPanel.SetActive(false); systemMenuOpen = false; }

        public bool ListOpen => listPanel.activeSelf;

        /// 목록 창. rows: (글자, 고를 수 있나, 고를 때). buttons: 아래쪽 버튼
        public void ShowList(string title, string sub, IList<(string label, bool enabled, Action pick)> rows, IList<(string label, Action click)> buttons, int columns = 3)
        {
            listDim.SetActive(true);
            listPanel.SetActive(true);
            hoverPanel.SetActive(false); infoPanel.SetActive(false);
            const int maxLines = 17;                                                               // 창 안에 들어가는 줄 수
            if (columns == 3 && rows.Count <= maxLines * 2) columns = 2;                          // 글자가 길어 되도록 2열
            var grid = listGrid.GetComponent<GridLayoutGroup>();
            grid.constraintCount = columns;
            grid.cellSize = new Vector2(576f / columns * K, 18 * K);
            listTitle.text = title; listSub.text = sub;
            foreach (Transform c in listGrid) Destroy(c.gameObject);
            foreach (Transform c in listButtons) Destroy(c.gameObject);
            foreach (var (label, enabled, pick) in rows)
            {
                var b = MakeRow(listGrid, label, () => pick?.Invoke(), 18);
                b.interactable = enabled && pick != null;
                b.GetComponentInChildren<BitmapText>().color = enabled ? Color.white : new Color(0.62f, 0.66f, 0.78f);
            }
            foreach (var (label, click) in buttons) MakeRow(listButtons, label, () => click?.Invoke());
            int lines = (rows.Count + columns - 1) / columns;                                       // 줄이 적으면 창을 줄인다(확인 창 등)
            int h = Mathf.Clamp(36 + lines * 18 + 40, 110, 384);
            var r = (RectTransform)listPanel.transform;
            r.sizeDelta = S(600, h); r.anchoredPosition = P(20, Mathf.Round((400 - h) / 2f));
            listGrid.sizeDelta = S(576, h - 76);
            StartCoroutine(FocusNextFrame(listGrid.childCount > 0 && rows.Any(x => x.enabled && x.pick != null) ? listGrid : listButtons));
        }

        /// 개발용: 목록 창의 i번째 줄 / 아래쪽 i번째 버튼을 누른다
        public void DebugPickRow(int i) => listGrid.GetChild(i).GetComponent<Button>().onClick.Invoke();
        public void DebugPickButton(int i) => listButtons.GetChild(i).GetComponent<Button>().onClick.Invoke();

        public void HideList() { listPanel.SetActive(false); listDim.SetActive(false); }

        public void ShowEnd(bool victory)
        {
            endPanel.SetActive(true);
            endText.text = victory ? content.Text.victory : content.Text.defeat;
        }

        /// 영상 재생(콘텐츠 팩의 mp4). 클릭·Enter·Esc·Space 로 건너뛴다
        public static bool DebugSkip;          // 개발용(DebugCapture): 영상 건너뛰기
        public static int DebugPick = -1;      // 개발용: 타이틀 메뉴 자동 선택

        public IEnumerator PlayVideo(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) yield break;
            var panel = new GameObject("Video", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(rootRect, false);
            var pr = (RectTransform)panel.transform; Stretch(pr);
            panel.GetComponent<Image>().color = Color.black;
            var raw = new GameObject("Image", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter)).GetComponent<RawImage>();
            raw.rectTransform.SetParent(pr, false);
            Stretch(raw.rectTransform);
            var fit = raw.GetComponent<AspectRatioFitter>(); fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fit.aspectRatio = 640f / 400f;
            var hint = MakeText(pr, "Skip", TextAnchor.LowerRight, P(240, 400 - 24), S(392, 16));
            hint.scale = 1; hint.text = content.Text.skip; hint.color = new Color(1, 1, 1, 0.5f);

            var vp = panel.AddComponent<VideoPlayer>();
            var audio = panel.AddComponent<AudioSource>();
            vp.source = VideoSource.Url; vp.url = new System.Uri(fullPath).AbsoluteUri;
            vp.renderMode = VideoRenderMode.APIOnly; vp.playOnAwake = false; vp.isLooping = false;
            vp.audioOutputMode = VideoAudioOutputMode.AudioSource; vp.SetTargetAudioSource(0, audio);
            bool done = false;
            vp.loopPointReached += _ => done = true;
            vp.errorReceived += (_, msg) => { Debug.LogWarning("[Video] " + msg); done = true; };
            vp.Prepare();
            for (float t0 = Time.unscaledTime; !vp.isPrepared && !done; )
            {
                if (Time.unscaledTime - t0 > 8f) { Debug.LogWarning("[Video] 준비 시간 초과, 건너뜀: " + fullPath); done = true; }
                yield return null;
            }
            if (!done)
            {
                if (vp.width > 0 && vp.height > 0) fit.aspectRatio = vp.width / (float)vp.height;
                vp.Play();
            }
            yield return null;
            while (!done)
            {
                raw.texture = vp.texture;
                if (DebugSkip || Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Space) || PadInput.AnyConfirm || PadInput.B) { DebugSkip = false; break; }
                yield return null;
            }
            vp.Stop();
            Destroy(panel);
        }

        /// 타이틀 메뉴(원작: 타이틀 그림 위 보라색 창). 고른 번호(0 처음부터, 1 이어하기, 2 오프닝, 3 엔딩)를 돌려준다
        public IEnumerator TitleMenu(Action<int> chosen)
        {
            titlePanel.SetActive(true);
            titleImage.sprite = content.DB.Sprite(content.Game.titleImage);
            titleImage.enabled = titleImage.sprite != null;
            titleText.text = titleImage.enabled ? "" : content.Text.title ?? "";     // 그림이 있으면 글자 제목은 숨김
            int pick = -1;
            var labels = new[] { content.Text.menuStart, content.Text.menuContinue, content.Text.menuOpening, content.Text.menuEnding };
            var box = Window((RectTransform)titlePanel.transform, "Menu", "menu", P(320 - 100, 290), S(200, labels.Length * 20 + 18), new Vector2(0, 1));
            var vl = box.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(14 * K, 12 * K, 8 * K, 10 * K); vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
            for (int i = 0; i < labels.Length; i++) { int k = i; MakeRow(box.transform, labels[i], () => pick = k); }
            yield return null;
            FocusFirst(box.transform);
            while (pick < 0)
            {
                if (Input.GetKeyDown(KeyCode.Return)) pick = 0;
                if (DebugPick >= 0) { pick = DebugPick; DebugPick = -1; }
                yield return null;
            }
            Destroy(box);
            titlePanel.SetActive(false);
            chosen(pick);
        }

        public IEnumerator WaitClick()
        {
            yield return null;
            while (!(Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space) || PadInput.AnyConfirm)) yield return null;
        }

        public void HideEnd() => endPanel.SetActive(false);

        /// 에필로그 등 맵 없이 대사만 보일 때의 배경
        public void SetBackdrop(Sprite sprite)
        {
            if (backdrop == null)
            {
                backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                backdrop.rectTransform.SetParent(rootRect, false); backdrop.rectTransform.SetAsFirstSibling();
                Stretch(backdrop.rectTransform);
                backdrop.preserveAspect = true; backdrop.raycastTarget = false;
            }
            backdrop.sprite = sprite;
            backdrop.color = sprite != null ? Color.white : Color.black;
            backdrop.gameObject.SetActive(true);
        }

        public void HideBackdrop() { if (backdrop != null) backdrop.gameObject.SetActive(false); }

        Image backdrop;

        /// 대화 블록을 페이지 단위로 보여 준다(클릭·Enter·Space 로 넘김)
        public IEnumerator Dialogue(FarlandDialogue.Script script, string block, object[] args, System.Random rng)
        {
            if (script == null) yield break;
            var pages = new List<FarlandDialogue.Page>(FarlandDialogue.Run(script, block, args, rng));
            if (pages.Count == 0) yield break;
            dialoguePanel.SetActive(true);
            hoverPanel.SetActive(false); infoPanel.SetActive(false); HideActions();
            foreach (var p in pages)
            {
                SetFace(leftFace, p.leftFace);
                SetFace(rightFace, p.rightFace);
                var sb = new StringBuilder();
                string name = "";
                foreach (var l in p.lines)
                {
                    if (l.gray) { sb.Append("<color=#A0A0A0>").Append(Quotes(l.text)).Append("</color>\n"); continue; }
                    if (!string.IsNullOrEmpty(l.label))
                    {
                        if (name == "") name = l.label;
                        else if (l.label != name) sb.Append("<color=#FFE08A>").Append(l.label).Append("</color>\n");
                    }
                    sb.Append(Quotes(l.text)).Append('\n');
                }
                dialogueName.text = name;
                dialogueText.text = sb.ToString().TrimEnd('\n');
                advance = false;
                yield return null;
                while (!advance && !Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.Space) && !PadInput.A && !PadInput.B) yield return null;
            }
            dialoguePanel.SetActive(false);
        }

        /// 대본의 [ ] 는 원작 화면의 낫표 「 」 로 보인다
        static string Quotes(string s) => s == null ? "" : s.Replace('[', '「').Replace(']', '」').Replace('［', '「').Replace('］', '」');

        void SetFace(Image img, int face)
        {
            var path = content.FaceFile(face);                                 // Characters/NN/face.png 가 있으면 그것
            img.sprite = path != null && content.DB.Exists(path) ? content.DB.Sprite(path) : null;
            img.enabled = img.sprite != null;
        }
    }
}

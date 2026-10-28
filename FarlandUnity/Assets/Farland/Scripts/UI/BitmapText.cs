using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Farland.UI
{
    /// 원작 비트맵 글꼴로 그리는 글자(UGUI). Text 대신 쓰며 &lt;color=#RRGGBB&gt; 태그와 자동 줄바꿈을 지원한다.
    /// 원작 1픽셀 = 캔버스 scale 픽셀(기본 2: 1280×800 캔버스 = 640×400 ×2).
    public class BitmapText : MaskableGraphic, ILayoutElement
    {
        [SerializeField] string m_Text = "";
        public TextAnchor alignment = TextAnchor.UpperLeft;
        public int scale = 2;
        public int lineGap = 4;              // 원작 픽셀 단위 줄 사이(글자 16 + 4 = 20)
        public bool wrap = true;

        public string text
        {
            get => m_Text;
            set { value ??= ""; if (m_Text == value) return; m_Text = value; SetVerticesDirty(); SetLayoutDirty(); }
        }

        /// Text 와 맞춘 호환용: 18 이상이면 2배, 아니면 1배
        public int fontSize { get => scale * 9; set { scale = value >= 18 ? 2 : 1; SetVerticesDirty(); } }

        public override Texture mainTexture => BitmapFont.Instance?.Atlas ?? s_WhiteTexture;

        struct Run { public char c; public Color32 color; }

        List<List<Run>> Layout(float maxWidth)
        {
            var font = BitmapFont.Instance;
            var lines = new List<List<Run>> { new List<Run>() };
            var colors = new Stack<Color32>();
            Color32 cur = color;
            float x = 0; int lastSpace = -1;
            string s = m_Text ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int end = s.IndexOf('>', i);
                    if (end > i)
                    {
                        var tag = s.Substring(i + 1, end - i - 1);
                        if (tag.StartsWith("color="))
                        {
                            colors.Push(cur);
                            if (ColorUtility.TryParseHtmlString(tag.Substring(6).Trim('"'), out var col)) { col.a *= color.a; cur = col; }
                            i = end; continue;
                        }
                        if (tag == "/color") { cur = colors.Count > 0 ? colors.Pop() : (Color32)color; i = end; continue; }
                        if (tag == "b" || tag == "/b" || tag == "i" || tag == "/i" || tag.StartsWith("size") || tag == "/size") { i = end; continue; }
                    }
                }
                var line = lines[lines.Count - 1];
                if (c == '\n') { lines.Add(new List<Run>()); x = 0; lastSpace = -1; continue; }
                if (c == '\r') continue;
                int w = (BitmapFont.IsWide(c) || c >= 0xAC00 ? 16 : 8) * scale;
                if (wrap && maxWidth > 0 && x + w > maxWidth + 0.5f && line.Count > 0)
                {
                    var nl = new List<Run>();
                    if (lastSpace >= 0 && lastSpace < line.Count - 1)          // 띄어쓰기에서 끊는다
                    {
                        nl.AddRange(line.GetRange(lastSpace + 1, line.Count - lastSpace - 1));
                        line.RemoveRange(lastSpace, line.Count - lastSpace);
                    }
                    lines.Add(nl); line = nl; lastSpace = -1;
                    x = 0; foreach (var r in nl) x += Width(r.c);
                    if (c == ' ') continue;
                }
                if (c == ' ') lastSpace = line.Count;
                line.Add(new Run { c = c, color = cur });
                x += w;
                if (font != null) font.Get(c);
            }
            font?.Commit();
            return lines;
        }

        float Width(char c) => (BitmapFont.IsWide(c) || c >= 0xAC00 ? 16 : 8) * scale;

        float LineWidth(List<Run> l) { float w = 0; foreach (var r in l) w += Width(r.c); return w; }

        float LineHeight => (BitmapFont.Cell + lineGap) * scale;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var font = BitmapFont.Instance;
            if (font == null || string.IsNullOrEmpty(m_Text)) return;
            var rect = rectTransform.rect;
            var lines = Layout(rect.width);
            float total = lines.Count * LineHeight - lineGap * scale;
            int h = (int)alignment % 3, v = (int)alignment / 3;          // 0 왼/위, 1 가운데, 2 오른/아래
            float y = v == 0 ? rect.yMax : v == 1 ? rect.center.y + total / 2f : rect.yMin + total;
            y = Mathf.Round(y);
            foreach (var line in lines)
            {
                float lw = LineWidth(line);
                float x = h == 0 ? rect.xMin : h == 1 ? rect.center.x - lw / 2f : rect.xMax - lw;
                x = Mathf.Round(x / scale) * scale;
                foreach (var r in line)
                {
                    var g = font.Get(r.c);
                    float gw = g.width * scale, gh = BitmapFont.Cell * scale;
                    if (r.c != ' ')
                    {
                        int k = vh.currentVertCount;
                        var uv = g.uv;
                        vh.AddVert(new Vector3(x, y - gh), r.color, new Vector2(uv.xMin, uv.yMin));
                        vh.AddVert(new Vector3(x, y), r.color, new Vector2(uv.xMin, uv.yMax));
                        vh.AddVert(new Vector3(x + gw, y), r.color, new Vector2(uv.xMax, uv.yMax));
                        vh.AddVert(new Vector3(x + gw, y - gh), r.color, new Vector2(uv.xMax, uv.yMin));
                        vh.AddTriangle(k, k + 1, k + 2); vh.AddTriangle(k + 2, k + 3, k);
                    }
                    x += Width(r.c);
                }
                y -= LineHeight;
            }
            font.Commit();
        }

        // ---- ILayoutElement ----
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }
        public float minWidth => 0;
        public float preferredWidth { get { float w = 0; foreach (var l in Layout(0)) w = Mathf.Max(w, LineWidth(l)); return w; } }
        public float flexibleWidth => -1;
        public float minHeight => 0;
        public float preferredHeight => Layout(rectTransform.rect.width).Count * LineHeight - lineGap * scale;
        public float flexibleHeight => -1;
        public int layoutPriority => 0;
    }
}

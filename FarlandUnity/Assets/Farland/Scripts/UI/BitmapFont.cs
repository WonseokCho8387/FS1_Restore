using System.Collections.Generic;
using Farland.Content;
using UnityEngine;

namespace Farland.UI
{
    /// 원작 비트맵 글꼴. 한글은 8x4x4벌 조합형(.HAN: 16x16 글자 조각 360개), 영문은 8x16(.ENG: 256자).
    /// 쓰인 글자만 그때그때 조합해 아틀라스(1024x1024, 16x16 칸)에 그린다. 파일은 콘텐츠 팩의 visuals.json bitmapFont 로 바꿀 수 있다.
    public class BitmapFont
    {
        public const int Cell = 16;
        const int AtlasSize = 1024, PerRow = AtlasSize / Cell;

        // 초성 벌(받침 없음/있음)·종성 벌은 중성(1..21)으로 고른다. 중성 벌은 초성 ㄱ·ㅋ 여부와 받침으로 고른다.
        static readonly int[] Cho1 = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 3, 3, 3, 1, 2, 4, 4, 4, 2, 1, 3, 0 };
        static readonly int[] Cho2 = { 0, 5, 5, 5, 5, 5, 5, 5, 5, 6, 7, 7, 7, 6, 6, 7, 7, 7, 6, 6, 7, 5 };
        static readonly int[] Jong = { 0, 0, 2, 0, 2, 1, 2, 1, 2, 3, 0, 2, 1, 3, 3, 1, 2, 1, 3, 3, 1, 1 };

        public struct Glyph { public Rect uv; public int width; }

        readonly byte[] han, eng;
        readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        readonly Color32[] cellPixels = new Color32[Cell * Cell];
        int next;
        bool dirty;
        public Texture2D Atlas { get; private set; }

        static BitmapFont instance;
        public static BitmapFont Instance => instance;

        public static void Load(FarlandContent content)
        {
            var cfg = content.Visual.bitmapFont;
            if (cfg == null) { instance = null; return; }
            var hp = content.DB.Resolve(cfg.han); var ep = content.DB.Resolve(cfg.eng);
            if (hp == null || ep == null) { Debug.LogWarning("[Font] 비트맵 글꼴 파일이 없어 OS 글꼴로 대신합니다"); instance = null; return; }
            instance = new BitmapFont(System.IO.File.ReadAllBytes(hp), System.IO.File.ReadAllBytes(ep));
        }

        BitmapFont(byte[] han, byte[] eng)
        {
            this.han = han; this.eng = eng;
            Atlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "BitmapFont" };
            Atlas.SetPixels32(new Color32[AtlasSize * AtlasSize]);
            Atlas.Apply();
        }

        public static bool IsWide(char c) => c >= 0x3000 || (c >= 0x2010 && c <= 0x2027) || c == '·';

        /// 글자를 아틀라스에 준비해 둔다(없으면 그림). 바뀐 게 있으면 Commit 으로 올린다
        public Glyph Get(char c)
        {
            if (glyphs.TryGetValue(c, out var g)) return g;
            if (next >= PerRow * PerRow) { glyphs.Clear(); next = 0; }       // 가득 차면 처음부터(드묾)
            var rows = new int[Cell];
            int w = Draw(c, rows);
            for (int y = 0; y < Cell; y++)
                for (int x = 0; x < Cell; x++)
                    cellPixels[(Cell - 1 - y) * Cell + x] = (rows[y] >> (15 - x) & 1) != 0 ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            int cx = next % PerRow * Cell, cy = next / PerRow * Cell;
            Atlas.SetPixels32(cx, cy, Cell, Cell, cellPixels);
            next++; dirty = true;
            g = new Glyph { uv = new Rect(cx / (float)AtlasSize, cy / (float)AtlasSize, w / (float)AtlasSize, Cell / (float)AtlasSize), width = w };
            glyphs[c] = g;
            return g;
        }

        public void Commit() { if (dirty) { Atlas.Apply(); dirty = false; } }

        /// 16행 비트(왼쪽이 최상위 비트)로 그리고 글자 폭(8 또는 16)을 돌려준다
        int Draw(char c, int[] rows)
        {
            if (c >= 0xAC00 && c <= 0xD7A3)
            {
                int s = c - 0xAC00, cho = s / 588 + 1, jung = s % 588 / 28 + 1, jong = s % 28;
                int cb = (jong != 0 ? Cho2 : Cho1)[jung];
                int jb = (cho == 1 || cho == 16 ? 0 : 1) + (jong != 0 ? 2 : 0);
                Or(rows, cb * 20 + cho);
                Or(rows, 160 + jb * 22 + jung);
                if (jong != 0) Or(rows, 248 + Jong[jung] * 28 + jong);
                return 16;
            }
            switch (c)
            {
                case '「': case '『': case '［': for (int y = 3; y < 11; y++) rows[y] = 0x0800; rows[3] = 0x0FC0; return 16;     // 원작 대사의 낫표
                case '」': case '』': case '］': for (int y = 6; y < 14; y++) rows[y] = 0x0010; rows[13] = 0x03F0; return 16;
                case '―': case '—': case '─': rows[8] = 0xFFFF; return 16;
                case '…': rows[13] = 0x6DB0; return 16;
                case '・': case '·': rows[8] = 0x0180; rows[7] = 0x0180; return 16;
                case '　': return 16;
            }
            int b = c < 256 ? c : FullWidthToAscii(c);
            if (b < 0) b = '?';
            bool wide = c >= 256;
            for (int y = 0; y < Cell; y++) rows[y] = eng[b * 16 + y] << (wide ? 12 : 8);
            return wide ? 16 : 8;
        }

        static int FullWidthToAscii(char c) => c >= 0xFF01 && c <= 0xFF5E ? c - 0xFF01 + 0x21 : -1;

        void Or(int[] rows, int part)
        {
            int o = part * 32;
            if (o + 32 > han.Length) return;
            for (int y = 0; y < Cell; y++) rows[y] |= han[o + y * 2] << 8 | han[o + y * 2 + 1];
        }
    }
}

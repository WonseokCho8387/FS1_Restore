using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Farland.Content
{
    /// 사람이 고치기 쉬운 형식의 콘텐츠 읽기:
    ///   Maps/*.tmj            Tiled 맵(타일 번호 = 맵칩 번호 + firstgid)
    ///   Dialogue/*.tsv        대사 표 — '고친 대사' 칸이 있으면 그 문장
    ///   Config/glossary.json  고유명사 일괄 바꾸기
    ///   Characters/NN/        캐릭터(이름 id NN)별 그림: map.png, map_acted.png, face.png, battle/000.png…
    public sealed partial class FarlandContent
    {
        // ---- Tiled 맵 ----
        [Serializable] class TiledLayer { public string type, name; public long[] data; }
        [Serializable] class TiledTilesetRef { public int firstgid; public string source; }
        [Serializable] class TiledMap { public int width, height, tilewidth; public string orientation; public TiledLayer[] layers; public TiledTilesetRef[] tilesets; }

        /// 맵 파일(.tmj 또는 예전 .json)을 읽는다. .tmj 는 첫 타일 레이어를 지형으로 쓴다
        public MapData LoadMap(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.EndsWith(".tmj", StringComparison.OrdinalIgnoreCase)) return DB.ReadJson<MapData>(path);
            var t = DB.ReadJson<TiledMap>(path);
            var layer = t?.layers?.FirstOrDefault(l => l.type == "tilelayer" && l.data != null);
            if (layer == null) { Debug.LogError("[Content] Tiled 맵에 타일 레이어가 없음: " + path); return null; }
            var ts = t.tilesets != null && t.tilesets.Length > 0 ? t.tilesets[0] : new TiledTilesetRef { firstgid = 1, source = "MAPCHIP_tiles.tsj" };
            var m = new MapData
            {
                name = Path.GetFileNameWithoutExtension(path), layout = "hex-even-r", tileSize = t.tilewidth > 0 ? t.tilewidth : 32,
                width = t.width, height = t.height, tileset = Path.GetFileNameWithoutExtension(ts.source ?? "MAPCHIP_tiles"),
                tiles = new int[t.width * t.height],
            };
            for (int i = 0; i < m.tiles.Length && i < layer.data.Length; i++)
            {
                long gid = layer.data[i] & 0x1FFFFFFF;                      // 뒤집기 비트는 무시
                m.tiles[i] = gid == 0 ? 0 : (int)gid - ts.firstgid;
            }
            return m;
        }

        // ---- 용어집 ----
        [Serializable] class GlossaryTerm { public string kind, from, to; }
        [Serializable] class Glossary { public GlossaryTerm[] terms; }
        List<(string from, string to)> glossary;

        void LoadGlossary()
        {
            var g = DB.Exists("Config/glossary.json") ? DB.ReadJson<Glossary>("Config/glossary.json") : null;
            glossary = g?.terms == null ? new List<(string, string)>()
                : g.terms.Where(x => !string.IsNullOrEmpty(x.from) && x.to != null && x.from != x.to)
                         .Select(x => (x.from, x.to)).OrderByDescending(x => x.from.Length).ToList();
        }

        /// 용어집 적용(긴 말부터 바꿈, 바꾼 결과는 다시 바꾸지 않음)
        public string Term(string s)
        {
            if (string.IsNullOrEmpty(s) || glossary == null || glossary.Count == 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length;)
            {
                bool hit = false;
                foreach (var (from, to) in glossary)
                    if (string.CompareOrdinal(s, i, from, 0, from.Length) == 0) { sb.Append(to); i += from.Length; hit = true; break; }
                if (!hit) sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        void ApplyGlossary()
        {
            LoadGlossary();
            if (glossary.Count == 0) return;
            foreach (var k in names.Keys.ToList()) names[k] = Term(names[k]);
            if (Rules?.classes != null) foreach (var c in Rules.classes) c.name = Term(c.name);
            if (Rules?.items != null) foreach (var it in Rules.items) if (it != null) it.name = Term(it.name);
        }

        // ---- 대사 ----
        /// 대사 파일(JSON) + 같은 이름의 .tsv(고친 대사) + 용어집
        public FarlandDialogue.Script LoadScript(string path)
        {
            var text = DB.ReadText(path);
            if (text == null) return null;
            var sc = FarlandDialogue.Load(text);
            var tsv = Path.ChangeExtension(path, ".tsv").Replace('\\', '/');
            if (DB.Exists(tsv)) ApplyTable(sc, DB.ReadText(tsv), tsv);
            if (glossary != null && glossary.Count > 0 && sc?.blocks != null)
                foreach (var b in sc.blocks) if (b.commands != null) foreach (var c in b.commands) c.text = Term(c.text);
            return sc;
        }

        static void ApplyTable(FarlandDialogue.Script sc, string table, string path)
        {
            if (sc?.blocks == null || table == null) return;
            var lines = table.Replace("\r\n", "\n").Split('\n');
            for (int n = 1; n < lines.Length; n++)                            // 0행은 머리글
            {
                if (lines[n].Length == 0) continue;
                var f = lines[n].Split('\t');
                if (f.Length < 5) continue;
                var edited = Unquote(f[4]);
                if (string.IsNullOrEmpty(edited)) continue;
                var b = Array.Find(sc.blocks, x => x.id == Unquote(f[0]));
                if (b == null || !int.TryParse(Unquote(f[1]), out int i) || i < 0 || i >= b.commands.Length || b.commands[i].cmd != "text")
                { Debug.LogWarning($"[Content] {path} {n + 1}행: 블록·줄 번호가 대사와 맞지 않음"); continue; }
                b.commands[i].text = edited;
            }
        }

        static string Unquote(string s)
        {
            s = s.Trim('\r');
            return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2).Replace("\"\"", "\"") : s;
        }

        // ---- 캐릭터별 그림 ----
        public static string CharacterDir(int nameId) => $"Characters/{nameId:00}/";

        /// Characters/NN/{file} 이 있으면 그 그림(맵 그림은 원본과 같은 32px = 1칸)
        public Sprite CharacterSprite(int nameId, string file, Vector2 pivot, float pixelsPerUnit)
        {
            if (nameId <= 0) return null;
            var path = CharacterDir(nameId) + file;
            if (!DB.Exists(path)) return null;
            var tex = DB.Texture(path);
            return tex == null ? null : DB.Sprite(path, new RectInt(0, 0, tex.width, tex.height), pivot, pixelsPerUnit);
        }

        /// 맵 그림: map_cNN.png(그 클래스일 때, 전직 대비) → map.png. 행동한 뒤는 map_acted(_cNN).png, 없으면 null 과 tint=true
        public Sprite CharacterMapSprite(int nameId, int cls, bool acted, out bool tint)
        {
            tint = false;
            var pivot = new Vector2(0.5f, 0.5f); float ppu = Visual.mapUnitSize;
            Sprite Find(string stem) => CharacterSprite(nameId, $"{stem}_c{cls:00}.png", pivot, ppu) ?? CharacterSprite(nameId, stem + ".png", pivot, ppu);
            if (acted) { var a = Find("map_acted"); if (a != null) return a; }
            var s = Find("map");
            tint = acted && s != null;
            return s;
        }

        /// 전투 프레임: battle_cNN/000.png → battle/000.png (크기·기준점은 그 클래스 SpriteDefs 정의와 같게)
        public Sprite CharacterBattleFrame(int nameId, int cls, int frame, Vector2 pivot) =>
            CharacterSprite(nameId, $"battle_c{cls:00}/{frame:000}.png", pivot, 1f) ?? CharacterSprite(nameId, $"battle/{frame:000}.png", pivot, 1f);

        readonly Dictionary<int, int> faceOwner = new Dictionary<int, int>();

        /// 얼굴 그림: 그 얼굴을 쓰는 캐릭터 폴더의 face.png 가 있으면 그것
        public string FaceFile(int face)
        {
            if (face < 0) return null;
            if (faceOwner.TryGetValue(face, out int id) && DB.Exists(CharacterDir(id) + "face.png")) return CharacterDir(id) + "face.png";
            return FacePath(face);
        }
    }
}

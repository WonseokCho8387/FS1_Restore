using System.Collections.Generic;
using System.IO;
using Farland.Content;
using UnityEditor;
using UnityEngine;

namespace Farland.EditorTools
{
    /// 리소스 편집 도우미(메뉴 Farland/리소스).
    /// 한 장에 여러 칸이 묶인 스프라이트 시트를 칸마다 낱장 PNG 로 나눈다. 게임은 낱장이 있으면 시트 대신 낱장을 쓴다(ContentDB.CellFile).
    ///   Sprites/MAPCHAR.png              → Sprites/MAPCHAR/000.png …   맵 유닛(32×32)
    ///   Sprites/ICONCHIP.png             → Sprites/ICONCHIP/000.png …  아이콘·보물 상자(32×32)
    ///   Sprites/MCSRFONT.png             → Sprites/MCSRFONT/000.png …  마우스 커서·대사 ▼(24×24)
    ///   Tilesets/MAPCHIP_tiles.png       → Tilesets/MAPCHIP_tiles/000.png … 맵 지형(32×32, 맵칩 번호 = 파일 번호)
    ///   SpriteDefs/CHAR_00_frames.png    → SpriteDefs/CHAR_00_frames/000.png … 전투 연출 프레임(클래스 번호 = 00)
    public static class ContentTools
    {
        static string ContentRoot => Path.Combine(Application.dataPath, "StreamingAssets", "Content");
        static string BasePack => Path.Combine(ContentRoot, "base");

        public struct Sheet { public string path; public int w, h, count; }

        /// 팩 폴더의 시트 목록(visuals.json 과 SpriteDefs 에서 칸 크기를 읽는다)
        public static List<Sheet> Sheets(string pack)
        {
            var list = new List<Sheet>();
            var vpath = Path.Combine(pack, "Config", "visuals.json");
            var v = File.Exists(vpath) ? JsonUtility.FromJson<VisualConfig>(File.ReadAllText(vpath)) : new VisualConfig();
            void Add(string rel, int w, int h, int count = -1) { if (!string.IsNullOrEmpty(rel) && File.Exists(Path.Combine(pack, rel))) list.Add(new Sheet { path = rel, w = w, h = h, count = count }); }
            Add(v.mapUnitSheet, v.mapUnitSize, v.mapUnitSize);
            Add(v.iconSheet, v.iconSize, v.iconSize);
            Add(v.cursorImage, v.cursorSize, v.cursorSize);
            var tiles = Path.Combine(pack, "Tilesets");
            if (Directory.Exists(tiles))
                foreach (var f in Directory.GetFiles(tiles, "*.png")) Add("Tilesets/" + Path.GetFileName(f), v.tileSize, v.tileSize);
            var defs = Path.Combine(pack, "SpriteDefs");
            if (Directory.Exists(defs))
                foreach (var f in Directory.GetFiles(defs, "*.json"))
                {
                    var d = JsonUtility.FromJson<SpriteSheetDef>(File.ReadAllText(f));
                    if (d != null && d.frameWidth > 0) Add("SpriteDefs/" + Path.GetFileNameWithoutExtension(f) + "_frames.png", d.frameWidth, d.frameHeight, d.frameCount);
                }
            return list;
        }

        /// PNG 를 읽는다. Unity 한계(16384px)를 넘는 등 못 읽으면 null
        static Texture2D Load(string full)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (t.LoadImage(File.ReadAllBytes(full)) && t.width > 2) return t;
            Object.DestroyImmediate(t);
            Debug.LogWarning("[Content] 읽을 수 없는 그림(너무 크거나 손상): " + full);
            return null;
        }

        /// 시트 → 낱장. overwrite 가 false 면 이미 있는 낱장(편집한 것)은 건드리지 않는다. 완전히 빈 칸은 만들지 않는다
        public static int Split(string pack, bool overwrite)
        {
            int written = 0;
            foreach (var sh in Sheets(pack))
            {
                var tex = Load(Path.Combine(pack, sh.path));
                if (tex == null) continue;                              // 너무 넓은 시트는 tools/build_content.py 가 나눈다
                int cols = tex.width / sh.w, rows = tex.height / sh.h;
                int count = sh.count > 0 ? Mathf.Min(sh.count, cols * rows) : cols * rows;
                for (int i = 0; i < count; i++)
                {
                    var file = Path.Combine(pack, ContentDB.CellFile(sh.path, i));
                    if (!overwrite && File.Exists(file)) continue;
                    var px = tex.GetPixels(i % cols * sh.w, tex.height - (i / cols + 1) * sh.h, sh.w, sh.h);
                    bool empty = true;
                    foreach (var c in px) if (c.a > 0) { empty = false; break; }
                    if (empty) continue;
                    var cell = new Texture2D(sh.w, sh.h, TextureFormat.RGBA32, false);
                    cell.SetPixels(px); cell.Apply();
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, cell.EncodeToPNG());
                    Object.DestroyImmediate(cell);
                    written++;
                }
                Object.DestroyImmediate(tex);
            }
            return written;
        }

        /// 낱장 → 시트(낱장을 고친 뒤 시트 한 장으로 다시 묶을 때). 낱장 크기가 칸과 다르면 왼쪽 위에 맞춰 잘라 넣는다
        public static int Merge(string pack)
        {
            int sheets = 0;
            foreach (var sh in Sheets(pack))
            {
                var dir = Path.Combine(pack, Path.ChangeExtension(sh.path, null));
                if (!Directory.Exists(dir)) continue;
                var full = Path.Combine(pack, sh.path);
                var tex = Load(full);
                if (tex == null) continue;                              // 시트를 못 읽으면 낱장만 쓴다(덮어쓰지 않음)
                int cols = tex.width / sh.w;
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int i)) continue;
                    var cell = Load(f);
                    if (cell == null) continue;
                    int w = Mathf.Min(sh.w, cell.width), h = Mathf.Min(sh.h, cell.height);
                    int x = i % cols * sh.w, y = tex.height - (i / cols + 1) * sh.h;
                    if (y < 0) continue;
                    tex.SetPixels(x, y + sh.h - h, w, h, cell.GetPixels(0, cell.height - h, w, h));
                    Object.DestroyImmediate(cell);
                }
                tex.Apply();
                File.WriteAllBytes(full, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                sheets++;
            }
            return sheets;
        }

        /// 낱장 폴더를 지운다(시트 그림으로 되돌림)
        public static int RemoveCells(string pack)
        {
            int n = 0;
            foreach (var sh in Sheets(pack))
            {
                var dir = Path.Combine(pack, Path.ChangeExtension(sh.path, null));
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, true);
                if (File.Exists(dir + ".meta")) File.Delete(dir + ".meta");
                n++;
            }
            return n;
        }

        [MenuItem("Farland/리소스/콘텐츠 폴더 열기 (base 팩)", priority = 100)]
        static void OpenBase() => EditorUtility.RevealInFinder(Path.Combine(BasePack, "pack.json"));

        [MenuItem("Farland/리소스/사용자 모드 폴더 열기 (빌드 후 교체용)", priority = 101)]
        static void OpenMods()
        {
            var dir = Path.Combine(Application.persistentDataPath, "Mods");
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir + Path.DirectorySeparatorChar);
        }

        [MenuItem("Farland/리소스/스프라이트 시트 → 낱장으로 나누기", priority = 200)]
        static void SplitMenu()
        {
            int n = Split(BasePack, false);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("낱장으로 나누기", $"낱장 {n}개를 만들었습니다(이미 있던 낱장은 그대로 둠).\n\n예: Sprites/MAPCHAR/000.png, SpriteDefs/CHAR_00_frames/000.png", "확인");
        }

        [MenuItem("Farland/리소스/스프라이트 시트 → 낱장으로 다시 나누기 (낱장 덮어씀)", priority = 201)]
        static void ResplitMenu()
        {
            if (!EditorUtility.DisplayDialog("낱장 덮어쓰기", "시트 그림으로 모든 낱장을 다시 만듭니다. 낱장에서 고친 내용은 사라집니다.", "덮어쓰기", "취소")) return;
            int n = Split(BasePack, true);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("낱장으로 나누기", $"낱장 {n}개를 다시 만들었습니다.", "확인");
        }

        [MenuItem("Farland/리소스/낱장 → 스프라이트 시트로 합치기", priority = 202)]
        static void MergeMenu()
        {
            int n = Merge(BasePack);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("시트로 합치기", $"시트 {n}장에 낱장을 반영했습니다.", "확인");
        }

        [MenuItem("Farland/리소스/낱장 모두 지우기 (시트만 사용)", priority = 203)]
        static void RemoveMenu()
        {
            if (!EditorUtility.DisplayDialog("낱장 지우기", "낱장 폴더를 모두 지웁니다. 고친 낱장이 있다면 먼저 '시트로 합치기'를 하세요.", "지우기", "취소")) return;
            int n = RemoveCells(BasePack);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("낱장 지우기", $"낱장 폴더 {n}개를 지웠습니다.", "확인");
        }

        /// 아군(이름 id 1~16)마다 Characters/NN/ 폴더를 만들고 지금 그림을 복사해 둔다(이미 있는 파일은 두고).
        /// 클래스별 이름(map_cNN.png, battle_cNN/)으로 복사하므로 전직하면 새 클래스 그림으로 바뀐다. 모든 클래스에 같은 그림을 쓰려면 map.png·battle/ 로 이름을 바꾼다
        public static int MakeCharacterFolders(string pack)
        {
            var bd = JsonUtility.FromJson<BattlePlacements>(File.ReadAllText(Path.Combine(pack, "Battle", "battle_data.json")));
            var chars = JsonUtility.FromJson<CharList>(File.ReadAllText(Path.Combine(pack, "Dialogue", "characters.json")));
            int made = 0;
            void Copy(string from, string to) { if (File.Exists(from) && !File.Exists(to)) { Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(from, to); } }
            for (int id = 1; id <= 16; id++)
            {
                int cls = -1;
                foreach (var st in bd.stages) foreach (var u in st.units) if (cls < 0 && u.nameId == id && u.cls < 18) cls = u.cls;
                if (cls < 0 && bd.recruits != null) foreach (var r in bd.recruits) if (r.units != null) foreach (var u in r.units) if (cls < 0 && u.nameId == id) cls = u.cls;
                if (cls < 0) continue;
                var dir = Path.Combine(pack, FarlandContent.CharacterDir(id));
                Directory.CreateDirectory(dir);
                var ch = System.Array.Find(chars.characters, c => c.id == id);
                File.WriteAllText(Path.Combine(dir, "_이름.txt"), $"{id:00} {ch?.name} (처음 클래스 {cls})\n" +
                    "map_cNN.png / map_acted_cNN.png : 맵 그림(32x32, 클래스 NN 일 때). 클래스 상관없이 쓰려면 map.png / map_acted.png\n" +
                    "battle_cNN/000.png… : 전투 프레임(SpriteDefs/CHAR_NN.json 의 frameWidth x frameHeight). 클래스 상관없이 battle/\n" +
                    "face.png : 얼굴(112x112)\n", System.Text.Encoding.UTF8);
                if (ch != null && ch.face >= 0) Copy(Path.Combine(pack, "Sprites", $"FACE{ch.face:00}.png"), Path.Combine(dir, "face.png"));
                Copy(Path.Combine(pack, "Sprites", "MAPCHAR", $"{cls:000}.png"), Path.Combine(dir, $"map_c{cls:00}.png"));
                Copy(Path.Combine(pack, "Sprites", "MAPCHAR", $"{cls + 42:000}.png"), Path.Combine(dir, $"map_acted_c{cls:00}.png"));
                var frames = Path.Combine(pack, "SpriteDefs", $"CHAR_{cls:00}_frames");
                if (Directory.Exists(frames))
                    foreach (var f in Directory.GetFiles(frames, "*.png")) Copy(f, Path.Combine(dir, $"battle_c{cls:00}", Path.GetFileName(f)));
                made++;
            }
            return made;
        }

        [System.Serializable] class CharList { public Character[] characters; }

        [MenuItem("Farland/리소스/캐릭터별 그림 폴더 만들기 (아군 16명)", priority = 300)]
        static void CharactersMenu()
        {
            int n = MakeCharacterFolders(BasePack);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("캐릭터별 그림", $"Characters/01~16 폴더 {n}개에 지금 그림(맵·얼굴·전투 프레임)을 복사했습니다.\n폴더 안 그림을 고치면 그 캐릭터만 바뀝니다. 설명은 각 폴더의 _이름.txt", "확인");
        }

        /// 명령줄: -executeMethod Farland.EditorTools.ContentTools.BatchCharacters
        public static void BatchCharacters() { Debug.Log("[Content] 캐릭터 폴더 " + MakeCharacterFolders(BasePack)); EditorApplication.Exit(0); }

        /// 명령줄: -executeMethod Farland.EditorTools.ContentTools.BatchSplit
        public static void BatchSplit()
        {
            int n = Split(BasePack, false);
            Debug.Log($"[Content] 낱장 {n}개 생성");
            EditorApplication.Exit(0);
        }
    }
}

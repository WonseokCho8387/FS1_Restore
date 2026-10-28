using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Farland.Content
{
    /// 콘텐츠 팩 하나 = 폴더 하나 + pack.json. 같은 상대 경로의 파일은 우선순위가 높은 팩이 덮어쓴다.
    [Serializable]
    public class ContentPack
    {
        public string id, name, description;
        public int priority;
        public bool enabled = true;
        [NonSerialized] public string root;
    }

    /// 모든 리소스·데이터 접근의 단일 창구. 게임 코드는 파일 경로를 직접 열지 않고 상대 경로로 이곳에 묻는다.
    /// 팩 검색 위치: StreamingAssets/Content/*, persistentDataPath/Mods/* (빌드 후 사용자 모드)
    public sealed class ContentDB
    {
        public readonly List<ContentPack> Packs = new List<ContentPack>();
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        public static IEnumerable<string> DefaultRoots()
        {
            yield return Path.Combine(Application.streamingAssetsPath, "Content");
            yield return Path.Combine(Application.persistentDataPath, "Mods");
        }

        public static ContentDB Load(IEnumerable<string> roots = null)
        {
            var db = new ContentDB();
            foreach (var root in roots ?? DefaultRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var manifest = Path.Combine(dir, "pack.json");
                    if (!File.Exists(manifest)) continue;
                    var p = JsonUtility.FromJson<ContentPack>(File.ReadAllText(manifest));
                    if (p == null || !p.enabled) continue;
                    p.root = dir;
                    if (string.IsNullOrEmpty(p.id)) p.id = Path.GetFileName(dir);
                    db.Packs.Add(p);
                }
            }
            db.Packs.Sort((a, b) => b.priority.CompareTo(a.priority));    // 높은 우선순위가 먼저
            if (db.Packs.Count == 0) Debug.LogError("[Content] 콘텐츠 팩이 없습니다: StreamingAssets/Content/base 를 확인하세요");
            return db;
        }

        /// 상대 경로 → 실제 파일 경로(가장 높은 우선순위 팩). 없으면 null
        public string Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            foreach (var p in Packs)
            {
                var full = Path.Combine(p.root, relative);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        public bool Exists(string relative) => Resolve(relative) != null;

        /// 어느 팩에서 왔는지 (디버그·UI 표시용)
        public string SourceOf(string relative)
        {
            var full = Resolve(relative);
            if (full == null) return null;
            foreach (var p in Packs) if (full.StartsWith(p.root)) return p.id;
            return null;
        }

        public string ReadText(string relative)
        {
            var full = Resolve(relative);
            if (full == null) { Debug.LogWarning("[Content] 없음: " + relative); return null; }
            return File.ReadAllText(full);
        }

        public T ReadJson<T>(string relative) where T : class
        {
            var text = ReadText(relative);
            return text == null ? null : JsonUtility.FromJson<T>(text);
        }

        public Texture2D Texture(string relative)
        {
            if (textures.TryGetValue(relative, out var tex)) return tex;
            var full = Resolve(relative);
            if (full != null)
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = relative };
                if (!tex.LoadImage(File.ReadAllBytes(full))) tex = null;
            }
            else Debug.LogWarning("[Content] 이미지 없음: " + relative);
            textures[relative] = tex;
            return tex;
        }

        /// 이미지 전체를 스프라이트로 (1 유닛 = pixelsPerUnit 픽셀)
        public Sprite Sprite(string relative, float pixelsPerUnit = 32f)
        {
            var tex = Texture(relative);
            return tex == null ? null : Sprite(relative, new RectInt(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        }

        /// 이미지의 일부(좌상단 원점 픽셀 좌표)를 스프라이트로. 같은 요청은 캐시
        public Sprite Sprite(string relative, RectInt topLeftRect, Vector2 pivot, float pixelsPerUnit = 32f)
        {
            string key = relative + "|" + topLeftRect + "|" + pivot + "|" + pixelsPerUnit;
            if (sprites.TryGetValue(key, out var s)) return s;
            var cell = CellPath(relative, topLeftRect);                       // 낱장 파일이 있으면 그것을 쓴다(시트를 읽지 않아도 된다)
            if (cell != null)
            {
                var ct = Texture(cell);
                if (ct != null)
                {
                    s = UnityEngine.Sprite.Create(ct, new Rect(0, 0, ct.width, ct.height), pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
                    s.name = key;
                    sprites[key] = s;
                    return s;
                }
            }
            var tex = Texture(relative);                                        // 16384px 를 넘는 시트는 Unity 가 못 읽는다 → 낱장 필요
            if (tex == null) return null;
            var r = new Rect(topLeftRect.x, tex.height - topLeftRect.y - topLeftRect.height, topLeftRect.width, topLeftRect.height);
            if (r.xMin < 0 || r.yMin < 0 || r.xMax > tex.width || r.yMax > tex.height) return null;
            s = UnityEngine.Sprite.Create(tex, r, pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
            s.name = key;
            sprites[key] = s;
            return s;
        }

        /// 시트의 칸 번호(왼쪽 위부터 가로로 0, 1, 2…). 칸 크기에 맞지 않는 영역이면 -1
        public static int CellIndex(int sheetWidth, RectInt r)
        {
            if (r.width <= 0 || r.height <= 0 || r.x % r.width != 0 || r.y % r.height != 0) return -1;
            return r.y / r.height * (sheetWidth / r.width) + r.x / r.width;
        }

        /// 낱장 파일 경로: 시트 'Sprites/MAPCHAR.png' 의 12번 칸 → 'Sprites/MAPCHAR/012.png'
        public static string CellFile(string sheet, int index) =>
            sheet.Substring(0, sheet.Length - Path.GetExtension(sheet).Length) + "/" + index.ToString("000") + ".png";

        /// 시트 칸을 대신할 낱장 파일. 시트보다 우선순위가 낮은 팩의 낱장은 쓰지 않는다(상위 팩이 시트를 통째로 바꾼 경우)
        string CellPath(string sheet, RectInt r)
        {
            var sf = Resolve(sheet);
            int width = sf != null ? PngWidth(sf) : r.y == 0 ? int.MaxValue : 0;      // 시트가 없어도 한 줄짜리(전투 프레임)는 번호를 안다
            int i = width > 0 ? CellIndex(width, r) : -1;
            if (i < 0) return null;
            var cell = CellFile(sheet, i);
            var cf = Resolve(cell);
            if (cf == null) return null;
            return PackRank(cf) <= PackRank(sf) ? cell : null;
        }

        readonly Dictionary<string, int> pngWidths = new Dictionary<string, int>();

        /// PNG 머리(IHDR)에서 폭만 읽는다(그림 전체를 풀지 않음)
        int PngWidth(string full)
        {
            if (pngWidths.TryGetValue(full, out int w)) return w;
            w = 0;
            try
            {
                using (var f = File.OpenRead(full))
                {
                    var b = new byte[24];
                    if (f.Read(b, 0, 24) == 24 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') w = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19];
                }
            }
            catch (IOException) { }
            pngWidths[full] = w;
            return w;
        }

        int PackRank(string full)
        {
            if (full == null) return int.MaxValue;
            for (int i = 0; i < Packs.Count; i++)
            {
                var root = Packs[i].root;
                if (full.Length > root.Length && full.StartsWith(root) && (full[root.Length] == '/' || full[root.Length] == '\\')) return i;
            }
            return int.MaxValue;
        }

        /// 오디오(ogg/wav/mp3)를 비동기로 읽는다
        public IEnumerator LoadAudio(string relative, Action<AudioClip> done)
        {
            var full = Resolve(relative);
            if (full == null) { Debug.LogWarning("[Content] 오디오 없음: " + relative); done(null); yield break; }
            var ext = Path.GetExtension(full).ToLowerInvariant();
            var type = ext == ".ogg" ? AudioType.OGGVORBIS : ext == ".mp3" ? AudioType.MPEG : AudioType.WAV;
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(full).AbsoluteUri, type))
            {
                ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = type == AudioType.OGGVORBIS;   // 음악만 스트리밍, 효과음은 메모리
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[Content] 오디오 실패: " + relative + " " + req.error); done(null); }
                else { var clip = DownloadHandlerAudioClip.GetContent(req); clip.name = relative; done(clip); }
            }
        }
    }
}

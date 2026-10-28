using System.Collections;
using System.Collections.Generic;
using Farland.Content;
using UnityEngine;

namespace Farland.Game
{
    /// 맵·유닛·하이라이트를 그리는 쪽. 원작 헥스(짝수 행이 반 칸 오른쪽) 배치를 1칸 = 1 유닛(32px)으로 놓는다.
    public class BattleView : MonoBehaviour
    {
        FarlandContent content;
        BattleState state;
        SpriteRenderer[] tiles;
        readonly Dictionary<GameUnit, SpriteRenderer> unitViews = new Dictionary<GameUnit, SpriteRenderer>();
        readonly Dictionary<GameUnit, SpriteRenderer> sideViews = new Dictionary<GameUnit, SpriteRenderer>();
        readonly Dictionary<Chest, SpriteRenderer> chestViews = new Dictionary<Chest, SpriteRenderer>();
        readonly List<SpriteRenderer> marks = new List<SpriteRenderer>();
        int markUsed;
        SpriteRenderer cursor;
        Sprite square;
        Camera cam;
        Transform root;

        public static Vector3 CellToWorld(Vector2Int p) => new Vector3(p.x + ((p.y & 1) == 0 ? 0.5f : 0f), -p.y, 0f);

        public static Vector2Int WorldToCell(Vector3 w)
        {
            int y = Mathf.RoundToInt(-w.y);
            int x = Mathf.RoundToInt(w.x - ((y & 1) == 0 ? 0.5f : 0f));
            return new Vector2Int(x, y);
        }

        public void Build(FarlandContent content, BattleState state, Camera cam)
        {
            this.content = content; this.state = state; this.cam = cam;
            if (root != null) Destroy(root.gameObject);
            root = new GameObject("Battle").transform;
            unitViews.Clear(); sideViews.Clear(); chestViews.Clear(); marks.Clear(); markUsed = 0;

            var tex = new Texture2D(32, 32) { filterMode = FilterMode.Point };
            var px = new Color[32 * 32];
            for (int i = 0; i < px.Length; i++) { int x = i % 32, y = i / 32; px[i] = x == 0 || y == 0 || x == 31 || y == 31 ? new Color(1, 1, 1, 0.9f) : Color.white; }
            tex.SetPixels(px); tex.Apply();
            square = Sprite.Create(tex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 32);

            var m = state.map;
            tiles = new SpriteRenderer[m.width * m.height];
            var tileRoot = new GameObject("Tiles").transform; tileRoot.SetParent(root);
            for (int y = 0; y < m.height; y++)
                for (int x = 0; x < m.width; x++)
                {
                    var sr = new GameObject().AddComponent<SpriteRenderer>();
                    sr.transform.SetParent(tileRoot);
                    sr.transform.position = CellToWorld(new Vector2Int(x, y));
                    tiles[y * m.width + x] = sr;
                    SetTile(new Vector2Int(x, y));
                }
            foreach (var c in state.chests)
            {
                var sr = new GameObject("Chest " + c.x + "," + c.y).AddComponent<SpriteRenderer>();
                sr.transform.SetParent(root); sr.sortingOrder = 1;
                sr.transform.position = CellToWorld(new Vector2Int(c.x, c.y));
                chestViews[c] = sr;
                RefreshChest(c);
            }
            foreach (var u in state.units) if (u.Alive) AddUnit(u);

            cursor = NewMark("Cursor", 6);
            cursor.enabled = false;
            ApplyZoom();
            var frame = content.Visual.sideMarks != null ? content.IconSprite(content.Visual.sideMarks.cursor) : null;
            if (frame != null) { cursor.sprite = frame; cursor.color = Color.white; }                   // 원작: 흰 모서리 틀
            else cursor.color = VisualConfig.ParseColor(content.Visual.colors?.cursor, new Color(1, 1, 1, 0.7f));
        }

        /// 전투 화면 정리(타이틀로 돌아갈 때)
        public void Clear()
        {
            if (root != null) Destroy(root.gameObject);
            root = null; state = null;
            unitViews.Clear(); chestViews.Clear(); marks.Clear(); markUsed = 0;
        }

        public void SetTile(Vector2Int p)
        {
            var sr = tiles[p.y * state.map.width + p.x];
            sr.sprite = content.TileSprite(state.map.tileset, state.Chip(p));
            sr.name = "Tile " + p;
        }

        /// 상자: 2 닫힘 아이콘, 3 열림 아이콘, 그 외(숨김) 안 보임 (0x6907)
        public void RefreshChest(Chest c)
        {
            if (!chestViews.TryGetValue(c, out var sr)) return;
            sr.enabled = c.state == 2 || c.state == 3;
            if (sr.enabled) sr.sprite = content.IconSprite(c.state == 3 ? content.Visual.chestOpenIcon : content.Visual.chestClosedIcon);
        }

        public void AddUnit(GameUnit u)
        {
            var sr = new GameObject(u.Name + " #" + u.slot).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(root);
            sr.sortingOrder = 5;
            unitViews[u] = sr;
            var sm = content.Visual.sideMarks;
            if (sm != null)
            {
                var mark = new GameObject("Side").AddComponent<SpriteRenderer>();
                mark.transform.SetParent(sr.transform, false);
                mark.sortingOrder = 7;
                sideViews[u] = mark;
            }
            Refresh(u);
            sr.transform.position = CellToWorld(u.pos);
        }

        public void RemoveUnit(GameUnit u)
        {
            if (unitViews.TryGetValue(u, out var sr)) { Destroy(sr.gameObject); unitViews.Remove(u); }
            sideViews.Remove(u);
        }

        public void Refresh(GameUnit u)
        {
            if (!unitViews.TryGetValue(u, out var sr)) return;
            bool grey = u.IsPlayerSide && (u.acted || u.stats.hp <= 0);
            // 캐릭터 폴더(Characters/NN/map.png, map_acted.png)가 있으면 클래스 그림 대신
            var own = content.CharacterMapSprite(u.stats.nameId, u.stats.cls, grey, out bool tintGrey);
            sr.sprite = own ?? content.MapUnitSprite(content.MapChip(u.stats.cls, grey));
            sr.color = tintGrey ? new Color(0.55f, 0.55f, 0.55f) : !u.IsPlayerSide && u.acted ? new Color(0.75f, 0.75f, 0.75f) : Color.white;   // 행동한 적은 약간 어둡게(원작에 없음)
            var sm = content.Visual.sideMarks;
            if (sm != null && sideViews.TryGetValue(u, out var mark))
            {
                bool rescue = u.stats.cls >= 60;
                mark.sprite = content.IconSprite(rescue ? sm.rescue : u.IsPlayerSide ? sm.ally : sm.enemy);
                mark.color = VisualConfig.ParseColor(rescue ? sm.rescueColor : u.IsPlayerSide ? sm.allyColor : sm.enemyColor, Color.white);
            }
        }

        public void RefreshAll() { foreach (var u in unitViews.Keys) Refresh(u); }

        public IEnumerator MoveUnit(GameUnit u, Vector2Int from, Vector2Int to, float time = 0.18f)
        {
            if (!unitViews.TryGetValue(u, out var sr)) yield break;
            Vector3 a = CellToWorld(from), b = CellToWorld(to);
            for (float t = 0; t < time; t += Time.deltaTime)
            {
                sr.transform.position = Vector3.Lerp(a, b, t / time);
                yield return null;
            }
            sr.transform.position = b;
        }

        public IEnumerator Flash(GameUnit u)
        {
            if (!unitViews.TryGetValue(u, out var sr)) yield break;
            for (int i = 0; i < 3; i++)
            {
                sr.enabled = false; yield return new WaitForSeconds(0.05f);
                if (sr == null) yield break;
                sr.enabled = true; yield return new WaitForSeconds(0.05f);
            }
        }

        // ---- 하이라이트 ----
        SpriteRenderer NewMark(string name, int order)
        {
            var sr = new GameObject(name).AddComponent<SpriteRenderer>();
            sr.transform.SetParent(root);
            sr.sprite = square; sr.sortingOrder = order;
            return sr;
        }

        public void ClearMarks() { for (int i = 0; i < markUsed; i++) marks[i].enabled = false; markUsed = 0; }

        public void Mark(IEnumerable<Vector2Int> cells, string colorKey)
        {
            var c = colorKey == "attack" ? VisualConfig.ParseColor(content.Visual.colors?.attack, new Color(1, 0.2f, 0.2f, 0.45f))
                                         : VisualConfig.ParseColor(content.Visual.colors?.move, new Color(0.2f, 0.5f, 1, 0.45f));
            foreach (var p in cells)
            {
                if (markUsed == marks.Count) marks.Add(NewMark("Mark", 2));
                var sr = marks[markUsed++];
                sr.enabled = true; sr.color = c;
                sr.transform.position = CellToWorld(p);
            }
        }

        /// 칸 오른쪽 가장자리 가운데의 화면 좌표(UI 를 유닛 옆에 띄울 때)
        public Vector2 CellToScreen(Vector2Int p) => cam.WorldToScreenPoint(CellToWorld(p) + new Vector3(0.5f, 0, 0));

        public void SetCursor(Vector2Int p, bool visible)
        {
            if (cursor == null) return;
            cursor.enabled = visible && state.Inside(p);
            cursor.transform.position = CellToWorld(p);
        }

        // ---- 카메라: 정수 배율(원본 1픽셀 = 화면 k픽셀)로 타일 이음새가 생기지 않게 한다 ----
        int zoom = 2, screenH;
        Vector3 camTarget;

        void ApplyZoom()
        {
            screenH = Screen.height;
            cam.orthographicSize = screenH / (2f * content.Visual.tileSize * zoom);
            Clamp();
        }

        public void Focus(Vector2Int p) { var w = CellToWorld(p); camTarget = new Vector3(w.x, w.y, -10); Clamp(); }

        /// 패드 커서가 화면 가장자리 2칸 안쪽에 머물도록 카메라를 민다
        public void KeepVisible(Vector2Int p)
        {
            var w = CellToWorld(p);
            float halfH = cam.orthographicSize - 2f, halfW = cam.orthographicSize * cam.aspect - 2f;
            if (w.x < camTarget.x - halfW) camTarget.x = w.x + halfW; else if (w.x > camTarget.x + halfW) camTarget.x = w.x - halfW;
            if (w.y < camTarget.y - halfH) camTarget.y = w.y + halfH; else if (w.y > camTarget.y + halfH) camTarget.y = w.y - halfH;
            Clamp();
        }

        public void Pan(Vector2 delta) { camTarget += (Vector3)delta; Clamp(); }

        public void Zoom(float delta)
        {
            int z = Mathf.Clamp(zoom + (delta > 0 ? 1 : -1), 1, 4);
            if (z != zoom) { zoom = z; ApplyZoom(); }
        }

        void Clamp()
        {
            if (state == null) return;
            float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
            float minX = -0.5f + halfW, maxX = state.map.width - halfW, minY = -state.map.height + 0.5f + halfH, maxY = 0.5f - halfH;
            camTarget.x = minX > maxX ? (state.map.width - 0.5f) / 2f : Mathf.Clamp(camTarget.x, minX, maxX);
            camTarget.y = minY > maxY ? (-state.map.height + 1) / 2f : Mathf.Clamp(camTarget.y, minY, maxY);
            camTarget.z = -10;
            float unitPx = 1f / (content.Visual.tileSize * zoom);                // 화면 1픽셀 크기
            cam.transform.position = new Vector3(Mathf.Round(camTarget.x / unitPx) * unitPx, Mathf.Round(camTarget.y / unitPx) * unitPx, -10);
        }

        void LateUpdate() { if (cam != null && state != null && Screen.height != screenH) ApplyZoom(); }

        public Vector2Int MouseCell() => WorldToCell(cam.ScreenToWorldPoint(Input.mousePosition));
    }

    /// 배경 음악. 같은 곡이면 다시 틀지 않는다(원작 [0x1544] 비교와 같음)
    public class MusicPlayer : MonoBehaviour
    {
        AudioSource source;
        FarlandContent content;
        int current;
        public int Current => current;

        public void Init(FarlandContent content)
        {
            this.content = content;
            source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
        }

        public void Stop() { current = 0; if (source != null) source.Stop(); }

        public void Play(int music)
        {
            if (music <= 0 || music == current) return;
            current = music;
            var path = content.MusicPath(music);
            StartCoroutine(content.DB.LoadAudio(path, clip =>
            {
                if (clip == null || current != music) return;
                source.clip = clip; source.Play();
            }));
        }
    }
}

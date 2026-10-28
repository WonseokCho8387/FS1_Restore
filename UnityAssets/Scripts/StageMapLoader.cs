using UnityEngine;
using UnityEngine.Tilemaps;

// Maps/STAGExx.json 을 읽어 Tilemap 에 배치한다.
// Tilesets/MAPCHIP_tiles.png 는 Sprite Mode=Multiple, 32x32 Grid 슬라이스, PPU=32, Filter=Point 로 임포트.
//
// 원작 맵은 헥스(벽돌형) 격자다: 짝수 행이 반 칸 오른쪽으로 밀린 'even-r' 오프셋 (FSMAIN 0x9A5A).
// Unity 의 Hexagon(Point Top) 레이아웃은 홀수 행을 밀기 때문에 원작 행 r 을 y = -(r+1) 에 두어 홀짝을 맞추고,
// 행 간격이 셀 높이의 0.75배인 Unity 규칙에 맞춰 cellSize.y = 4/3 으로 사각 타일이 빈틈없이 쌓이게 한다.
public class StageMapLoader : MonoBehaviour
{
    [System.Serializable]
    class StageData { public string name; public int width; public int height; public int tileSize; public string layout; public string tileset; public int[] tiles; }

    public TextAsset stageJson;
    public Sprite[] tileSprites;   // 슬라이스된 타일 스프라이트(인덱스 0..119 순서)
    public Tilemap tilemap;

    void Start() { Load(); }

    public void Load()
    {
        var data = JsonUtility.FromJson<StageData>(stageJson.text);
        var grid = tilemap.layoutGrid;
        grid.cellLayout = GridLayout.CellLayout.Hexagon;
        grid.cellSwizzle = GridLayout.CellSwizzle.XYZ;
        grid.cellSize = new Vector3(1f, 4f / 3f, 0f);

        var cache = new Tile[tileSprites.Length];
        tilemap.ClearAllTiles();
        for (int i = 0; i < data.tiles.Length; i++)
        {
            int id = data.tiles[i];
            if (cache[id] == null)
            {
                cache[id] = ScriptableObject.CreateInstance<Tile>();
                cache[id].sprite = tileSprites[id];
            }
            tilemap.SetTile(ToCell(i % data.width, i / data.width), cache[id]);
        }
    }

    /// 원작 (x, y) → Tilemap 셀
    public static Vector3Int ToCell(int x, int y) => new Vector3Int(x, -(y + 1), 0);
    /// Tilemap 셀 → 원작 (x, y)
    public static Vector2Int FromCell(Vector3Int c) => new Vector2Int(c.x, -c.y - 1);
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Opening/opening_timeline.json 재생기. 원작 OP.EXE를 에뮬레이션해 뽑은 그리기 명령을
// 가상 VGA 메모리(64KB 바이트 주소 × 8픽셀)에 적용하고, 프레임마다 표시 시작주소·줄 수·팔레트로 화면을 만든다.
// tools/op_player.py 와 같은 규칙이며, 이 규칙으로 원작 화면과 픽셀 단위로 일치함을 검증했다.
//
// 설정: Opening/Indexed/*.bytes 를 indexedImages 에, opening_timeline.json 을 timelineJson 에,
// Audio/BGM/track02.wav 를 music 에 연결. target(RawImage)에 640x480 텍스처가 그려진다.
public class OpeningPlayer : MonoBehaviour
{
    [Serializable] class ImageInfo { public string name; public int w; public int h; public string file; }
    [Serializable] class Op { public int t; public string op; public string img; public int src; public int dst; public int rows; public int wb; public int len; public bool @double; }
    [Serializable] class State { public int t; public int start; public int lines; public bool blank; public string pal; }
    [Serializable] class Timeline { public int fps; public int totalFrames; public string track; public int[] scenes; public ImageInfo[] images; public Op[] ops; public State[] states; }

    const int VramPixels = 0x10000 * 8;
    const int Width = 640, Height = 480;

    public TextAsset timelineJson;
    public TextAsset[] indexedImages;
    public AudioSource music;            // 있으면 음악 재생 위치에 프레임을 맞춘다
    public RawImage target;
    public bool playOnStart = true;
    public event Action Finished;

    Timeline tl;
    readonly Dictionary<string, byte[]> images = new Dictionary<string, byte[]>();
    readonly byte[] vram = new byte[VramPixels];
    readonly Color32[] palette = new Color32[16];
    Color32[] pixels;
    Texture2D tex;
    int opIndex, stateIndex, frame = -1;
    State state;
    float startTime;
    bool playing;

    void Awake()
    {
        tl = JsonUtility.FromJson<Timeline>(timelineJson.text);
        var byFile = new Dictionary<string, TextAsset>();
        foreach (var ta in indexedImages) byFile[ta.name] = ta;
        foreach (var info in tl.images)
        {
            // Unity는 TextAsset 이름에서 마지막 확장자(.bytes)만 뗀다
            var key = info.file.Substring(0, info.file.Length - ".bytes".Length);
            if (byFile.TryGetValue(key, out var ta)) images[info.name] = ta.bytes;
            else Debug.LogWarning($"OpeningPlayer: missing indexed image {info.file}");
        }
        tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        pixels = new Color32[Width * Height];
        if (target != null) target.texture = tex;
    }

    void Start() { if (playOnStart) Play(); }

    public void Play()
    {
        Array.Clear(vram, 0, vram.Length);
        opIndex = stateIndex = 0; frame = -1; state = null;
        startTime = Time.time;
        if (music != null) music.Play();
        playing = true;
    }

    void Update()
    {
        if (!playing) return;
        float elapsed = music != null && music.isPlaying ? music.time : Time.time - startTime;
        int f = Mathf.Min((int)(elapsed * tl.fps), tl.totalFrames - 1);
        if (f != frame) { Advance(f); Render(); frame = f; }
        if (f >= tl.totalFrames - 1)
        {
            playing = false;
            if (music != null) music.Stop();
            Finished?.Invoke();
        }
    }

    void Advance(int t)
    {
        while (opIndex < tl.ops.Length && tl.ops[opIndex].t <= t) Apply(tl.ops[opIndex++]);
        bool palChanged = false;
        while (stateIndex < tl.states.Length && tl.states[stateIndex].t <= t)
        {
            var s = tl.states[stateIndex++];
            palChanged |= state == null || s.pal != state.pal;
            state = s;
        }
        if (palChanged) ParsePalette(state.pal);
    }

    void Apply(Op o)
    {
        switch (o.op)
        {
            case "clear":
                for (int i = 0; i < o.len * 8; i++) vram[(o.dst * 8 + i) % VramPixels] = 0;
                break;
            case "blit":
            {
                if (!images.TryGetValue(o.img, out var img)) break;
                int dstRows = o.@double ? o.rows * 2 : o.rows;
                for (int r = 0; r < dstRows; r++)
                {
                    int sr = o.@double ? r / 2 : r;
                    for (int b = 0; b < o.wb; b++)
                    {
                        int sp = (((o.src + sr * 80 + b) & 0xFFFF) * 8);
                        int dp = (((o.dst + r * 80 + b) & 0xFFFF) * 8);
                        for (int k = 0; k < 8; k++) vram[dp + k] = img[Math.Min(sp + k, img.Length - 1)];
                    }
                }
                break;
            }
            case "gem":
            {
                if (!images.TryGetValue(o.img, out var img)) break;
                var info = Array.Find(tl.images, x => x.name == o.img);
                int wb = info.w / 8;
                for (int r = 0; r < info.h; r++)
                    for (int b = 0; b < wb; b++)
                    {
                        int dp = ((o.dst + r * 80 + b) & 0xFFFF) * 8;
                        for (int k = 0; k < 8; k++)
                        {
                            byte v = img[r * info.w + b * 8 + k];
                            if (v != 0xFF) vram[dp + k] = v;      // 0xFF = 투명
                        }
                    }
                break;
            }
        }
    }

    void ParsePalette(string hex)
    {
        for (int i = 0; i < 16; i++)
            palette[i] = new Color32(Convert.ToByte(hex.Substring(i * 6, 2), 16),
                                     Convert.ToByte(hex.Substring(i * 6 + 2, 2), 16),
                                     Convert.ToByte(hex.Substring(i * 6 + 4, 2), 16), 255);
    }

    void Render()
    {
        var black = new Color32(0, 0, 0, 255);
        int lines = state == null || state.blank ? 0 : state.lines;
        int start = state == null ? 0 : state.start * 8;
        for (int y = 0; y < Height; y++)
        {
            int row = (Height - 1 - y) * Width;              // 텍스처는 아래가 원점
            if (y >= lines) { for (int x = 0; x < Width; x++) pixels[row + x] = black; continue; }
            int a = start + y * Width;
            for (int x = 0; x < Width; x++) pixels[row + x] = palette[vram[(a + x) % VramPixels] & 0x0F];
        }
        tex.SetPixels32(pixels);
        tex.Apply(false);
    }
}

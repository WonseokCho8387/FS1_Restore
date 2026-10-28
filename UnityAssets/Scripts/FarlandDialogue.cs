using System;
using System.Collections.Generic;
using UnityEngine;

// Dialogue/STAGExx.json 실행기. 원작 FSMAIN.EXE 0xE9C0 talk(이벤트, 유닛0, 유닛1, ...) 해석기와 같은 규칙:
//  - if/else/endif는 중첩되지 않는다. if는 직전 조건을 대체하고, 조건 줄은 스킵 중에도 평가된다.
//  - wait(k)가 붙은 줄 뒤에서 페이지가 끝나고 키 입력을 기다린다. clear(w)는 새 페이지를 시작한다.
//  - 화자 unitName = 인자 유닛의 캐릭터 이름, argString/argLine = 인자로 받은 문자열(아이템 이름 등).
// 어떤 이벤트가 어떤 유닛을 인자로 받는지는 Dialogue/talk_calls.json 참고.
public static class FarlandDialogue
{
    [Serializable] public class Command { public string cmd, side, cond, mode, speaker, text; public int face, arg; public bool clear, wait; }
    [Serializable] public class Block { public string id; public Command[] commands; }
    [Serializable] public class Script { public string name; public Block[] blocks; }

    public interface IUnit
    {
        string Name { get; }     // Dialogue/characters.json 의 name
        int Face { get; }        // FACE{nn}.GEM 번호 (유닛 +0x02 값 - 1)
        bool IsFemale { get; }   // 원작: (클래스/3*3) 이 6, 9, 15 이면 여성 (FSMAIN 0xE8E5)
    }

    public class Line { public string label; public string text; public bool gray; }

    public class Page
    {
        public int leftFace = -1, rightFace = -1;
        public readonly List<Line> lines = new List<Line>();
        public string endMode;   // 마지막 페이지에만: "q" 창 닫기, "a" 초상화 유지, "e" 맵 갱신
    }

    public static bool IsFemaleClass(int classId) { int c = classId / 3 * 3; return c == 6 || c == 9 || c == 15; }

    public static Script Load(TextAsset json) => Load(json.text);
    public static Script Load(string json) => JsonUtility.FromJson<Script>(json);

    public static IEnumerable<Page> Run(Script script, string eventId, object[] args, System.Random rng = null)
    {
        rng = rng ?? new System.Random();
        var block = Array.Find(script.blocks, b => b.id == eventId);
        if (block == null) yield break;
        bool skip = false;
        int left = -1, right = -1;
        var page = new Page();
        foreach (var c in block.commands)
        {
            switch (c.cmd)
            {
                case "if": skip = !Test(c, args, rng); continue;
                case "else": skip = !skip; continue;
                case "endif": skip = false; continue;
            }
            if (skip) continue;
            switch (c.cmd)
            {
                case "portrait":
                {
                    int f = c.arg >= 0 ? ((IUnit)args[c.arg]).Face : c.face;
                    if (c.side == "left") left = f; else right = f;
                    break;
                }
                case "text":
                {
                    if (c.clear && page.lines.Count > 0) { yield return Stamp(page, left, right); page = new Page(); }
                    string label = null;
                    if (c.speaker == "unitName") label = ((IUnit)args[c.arg]).Name;
                    else if (c.speaker == "argString") label = args[c.arg] as string;
                    else if (c.speaker == "argLine") page.lines.Add(new Line { text = args[c.arg] as string, gray = true });
                    page.lines.Add(new Line { label = label, text = c.text });
                    if (c.wait) { yield return Stamp(page, left, right); page = new Page(); }
                    break;
                }
                case "end":
                    page.endMode = c.mode;
                    yield return Stamp(page, left, right);
                    yield break;
            }
        }
        if (page.lines.Count > 0) yield return Stamp(page, left, right);
    }

    static Page Stamp(Page p, int left, int right) { p.leftFace = left; p.rightFace = right; return p; }

    static bool Test(Command c, object[] args, System.Random rng)
    {
        switch (c.cond)
        {
            case "random": return rng.Next(2) == 1;
            case "male": return !((IUnit)args[c.arg]).IsFemale;
            case "female": return ((IUnit)args[c.arg]).IsFemale;
            case "face": return ((IUnit)args[c.arg]).Face == c.face;
        }
        return true;
    }
}

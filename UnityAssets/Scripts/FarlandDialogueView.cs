using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// 간단한 UGUI 표시 예시: 초상화 2개 + 본문. 클릭/Enter로 다음 페이지.
public class FarlandDialogueView : MonoBehaviour
{
    public Image leftPortrait, rightPortrait;
    public Text body;
    public Sprite[] faces;                // FACE00..FACE29 순서
    public GameObject window;

    public IEnumerator Play(FarlandDialogue.Script script, string eventId, params object[] args)
    {
        window.SetActive(true);
        string end = null;
        foreach (var page in FarlandDialogue.Run(script, eventId, args))
        {
            SetFace(leftPortrait, page.leftFace);
            SetFace(rightPortrait, page.rightFace);
            var sb = new System.Text.StringBuilder();
            foreach (var l in page.lines)
            {
                if (l.gray) sb.Append("<color=#AAAAAA>").Append(l.text).Append("</color>\n");
                else if (l.label != null) sb.Append(l.label).Append(" : ").Append(l.text).Append('\n');
                else sb.Append("      ").Append(l.text).Append('\n');
            }
            body.text = sb.ToString();
            end = page.endMode;
            yield return null;
            while (!Input.GetMouseButtonDown(0) && !Input.GetKeyDown(KeyCode.Return)) yield return null;
        }
        if (end == "q") window.SetActive(false);
    }

    void SetFace(Image img, int face)
    {
        img.enabled = face >= 0 && face < faces.Length;
        if (img.enabled) img.sprite = faces[face];
    }
}

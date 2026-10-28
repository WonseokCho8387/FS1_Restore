using System.Collections;
using System.IO;
using UnityEngine;

namespace Farland.Game
{
    /// 개발용: 명령줄에 -farlandShot <폴더> 가 있을 때만 동작. 대화·맵·이동 범위 화면을 찍고 종료한다.
    public class DebugCapture : MonoBehaviour
    {
        static bool Has(string a) => System.Array.IndexOf(System.Environment.GetCommandLineArgs(), a) >= 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Arg("-farlandShot") != null) new GameObject("DebugCapture").AddComponent<DebugCapture>();
        }

        static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        IEnumerator Start()
        {
            var dir = Arg("-farlandShot");
            Directory.CreateDirectory(dir);
            var bc = FindObjectOfType<BattleController>();
            System.Action<string> shot = n => ScreenCapture.CaptureScreenshot(Path.Combine(dir, n + ".png"));
            var frame = new WaitForEndOfFrame();
            if (Has("-debugFlow"))                     // 오프닝 → 타이틀 → 프롤로그
            {
                yield return new WaitForSeconds(6f); shot("f1_opening");
                yield return new WaitForSeconds(20f); shot("f2_opening");
                UI.GameUI.DebugSkip = true;
                yield return new WaitForSeconds(1f); shot("f3_title");
                UI.GameUI.DebugPick = 0;
                yield return new WaitForSeconds(1.5f); shot("f4_prologue");
                for (int i = 0; i < 6; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.25f); }
                shot("f5_prologue2");
                for (int i = 0; i < 300 && !bc.Ready; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.1f); }
                yield return new WaitForSeconds(0.5f); shot("f6_stage");
                Application.Quit(); yield break;
            }
            if (Has("-debugLoad"))                     // 타이틀 → 이어하기 목록
            {
                yield return new WaitForSeconds(1.5f); shot("l1_title");
                UI.GameUI.DebugPick = 1;
                yield return new WaitForSeconds(0.8f); shot("l2_load");
                Application.Quit(); yield break;
            }
            if (Has("-debugEnding"))                   // 에필로그 → 엔딩 영상
            {
                yield return new WaitForSeconds(1.5f); shot("e1_epilogue");
                for (int i = 0; i < 400 && bc.UI.DialogueOpen; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.08f); if (i == 40) shot("e2_epilogue"); }
                yield return new WaitForSeconds(15f); shot("e3_ending");
                yield return new WaitForSeconds(30f); shot("e4_ending");
                Application.Quit(); yield break;
            }
            yield return new WaitForSeconds(3f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "1_dialogue.png"));
            yield return new WaitForSeconds(0.5f);
            for (int i = 0; i < 200 && !bc.Ready; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.1f); }
            yield return new WaitForSeconds(0.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "2_map.png"));
            yield return new WaitForSeconds(0.5f);
            bc.SelectSlot(0);
            yield return new WaitForSeconds(0.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "3_move.png"));
            yield return new WaitForSeconds(0.5f);
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-debugMisc") >= 0)
            {
                yield return bc.DebugMisc(n => ScreenCapture.CaptureScreenshot(Path.Combine(dir, n + ".png")));
                yield return new WaitForSeconds(0.5f);
                Application.Quit();
                yield break;
            }
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-debugShop") >= 0)
            {
                bc.StartCoroutine(bc.DebugShop());
                yield return new WaitForSeconds(0.6f);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "5_village_talk.png"));
                for (int i = 0; i < 20 && bc.UI.DialogueOpen; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.15f); }
                yield return new WaitForSeconds(0.4f);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "6_shop.png"));
                yield return new WaitForSeconds(0.3f);
                bc.DebugBag();
                yield return new WaitForSeconds(0.4f);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "7_bag.png"));
                yield return new WaitForSeconds(0.5f);
                Application.Quit();
                yield break;
            }
            if (Has("-debugStage"))                    // N스테이지 시작 화면
            {
                yield return new WaitForSeconds(2f); shot("s1_start");
                for (int i = 0; i < 300 && !bc.Ready; i++) { bc.UI.AdvanceDialogue(); yield return new WaitForSeconds(0.1f); }
                yield return new WaitForSeconds(0.5f); shot("s2_map");
                if (Has("-debugSysMenu"))                                      // 유닛 위 HP 표시 → 오른쪽 클릭 메뉴 → 명령 메뉴
                {
                    bc.DebugHoverSlot = 0; yield return new WaitForSeconds(0.3f); shot("m1_hover"); yield return frame; yield return null;
                    bc.DebugHoverSlot = -1;
                    bc.DebugIdle(); bc.UI.ToggleSystemMenu(); yield return new WaitForSeconds(0.3f); shot("m2_sysmenu"); yield return frame; yield return null;
                    bc.UI.HideActions(); bc.SelectSlot(0); yield return new WaitForSeconds(0.3f); shot("m3_selected"); yield return frame; yield return null;
                }
                if (Has("-debugBattleSave"))                                   // 저장 → 같은 칸에 다시 저장(덮어쓰기 확인)
                {
                    bc.DebugIdle(); bc.UI.onSave?.Invoke(); yield return new WaitForSeconds(0.6f); shot("s3_save"); yield return frame; yield return null;
                    bc.UI.DebugPickRow(0); yield return new WaitForSeconds(0.6f);
                    bc.DebugIdle(); bc.UI.onSave?.Invoke(); yield return new WaitForSeconds(0.6f); shot("s4_save_filled"); yield return frame; yield return null;
                    bc.UI.DebugPickRow(0); yield return new WaitForSeconds(0.6f); shot("s5_overwrite"); yield return frame; yield return null;
                    bc.UI.DebugPickButton(0); yield return new WaitForSeconds(0.6f); shot("s6_saved"); yield return frame; yield return null;
                }
                Application.Quit(); yield break;
            }
            var who = Arg("-debugAttacker");                       // "클래스,아이템" (예: 3,24 위저드+파이어 롯)
            int cls = -1, item = -1, mode = -1;
            if (who != null) { var p = who.Split(','); cls = int.Parse(p[0]); item = int.Parse(p[1]); if (p.Length > 2) mode = int.Parse(p[2]); }
            bc.StartCoroutine(bc.DebugCombat(cls, item, mode));
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(0.25f);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"4_battle_{i}.png"));
            }
            yield return new WaitForSeconds(4f);
            Application.Quit();
        }
    }
}

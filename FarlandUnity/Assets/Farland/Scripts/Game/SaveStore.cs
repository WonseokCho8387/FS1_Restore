using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Farland.Game
{
    [Serializable]
    public class UnitSnap
    {
        public int slot, x, y, face;
        public string name;
        public bool acted, engaged, removed;
        public FarlandRules.Stats stats;
    }

    /// 전투 도중 상태(아군 턴, 아무것도 고르지 않은 때). 맵은 바뀐 칩만, 이벤트는 stages.json 의 순서 번호로 남긴다
    [Serializable]
    public class BattleSnapshot
    {
        public int turn, music;
        public UnitSnap[] units;
        public int[] tiles;                // 바뀐 칸 (x, y, 맵칩) 반복
        public Content.Chest[] chests;
        public int[] firedEvents;
        public string[] firedGroups;
        // FarlandStageAI.State (원작 0x84E6 등)
        public int stFlag, stTriggerSlot = -1, stTrappedSlot = -1;
        public bool stReinforce, stSwitchTalked;
        public int[] stVars;
        public long rngState;             // FarlandRandom.state
    }

    /// 세이브 한 칸. 스테이지 사이(클리어 직후)의 파티 상태와 다음에 할 스테이지를 담는다.
    /// 원작은 설치 드라이브의 SAVE00~14.DAT(15칸)와 SAVEFS.IDX(목록)를 쓴다. 여기서는 같은 15칸을 JSON 으로 둔다.
    [Serializable]
    public class SaveData
    {
        public int version = 1;
        public int slot;
        public string savedAt;            // 저장 시각(ISO 8601)
        public string stageId;            // 다음에 시작할 스테이지
        public string stageTitle;
        public int gold, commandMode;
        public int[] bag;
        public FarlandRules.Stats[] members;   // 동료 능력치(슬롯 = 이름 id - 1)
        public float playSeconds;
        public bool inBattle;              // true 면 battle 로 전투 도중부터 이어 감
        public BattleSnapshot battle;

        public static SaveData From(Party p, string nextStage, string title, float playSeconds)
        {
            var d = new SaveData { stageId = nextStage, stageTitle = title, gold = p.gold, commandMode = p.commandMode, bag = p.bag.ToArray(),
                                   playSeconds = playSeconds, savedAt = DateTime.Now.ToString("s") };
            var list = new List<FarlandRules.Stats>();
            foreach (var m in p.members.Values) list.Add(m.Clone());
            d.members = list.ToArray();
            return d;
        }

        public Party ToParty()
        {
            var p = new Party { gold = gold, commandMode = commandMode };
            if (bag != null) p.bag.AddRange(bag);
            if (members != null) foreach (var m in members) p.Join(m);
            return p;
        }
    }

    /// 세이브 파일 저장소: persistentDataPath/Saves/slotNN.json
    public class SaveStore
    {
        public const int SlotCount = 15;
        readonly string dir;

        /// 명령줄 -farlandSaveDir <폴더> 로 저장 위치를 바꿀 수 있다(개발·캡처용)
        public SaveStore(string dir = null) { this.dir = dir ?? ArgDir() ?? Path.Combine(Application.persistentDataPath, "Saves"); }

        static string ArgDir()
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, "-farlandSaveDir");
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        string PathOf(int slot) => Path.Combine(dir, $"slot{slot:00}.json");

        public SaveData Load(int slot)
        {
            try
            {
                var p = PathOf(slot);
                return File.Exists(p) ? JsonUtility.FromJson<SaveData>(File.ReadAllText(p)) : null;
            }
            catch (Exception e) { Debug.LogWarning($"[Save] {slot} 읽기 실패: {e.Message}"); return null; }
        }

        public SaveData[] List()
        {
            var all = new SaveData[SlotCount];
            for (int i = 0; i < SlotCount; i++) all[i] = Load(i);
            return all;
        }

        public void Save(int slot, SaveData data)
        {
            Directory.CreateDirectory(dir);
            data.slot = slot;
            var tmp = PathOf(slot) + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(data, true));      // 임시 파일에 쓴 뒤 바꿔치기(쓰다 꺼져도 기존 세이브 보존)
            if (File.Exists(PathOf(slot))) File.Delete(PathOf(slot));
            File.Move(tmp, PathOf(slot));
        }

        public bool Any() { foreach (var d in List()) if (d != null) return true; return false; }
    }
}

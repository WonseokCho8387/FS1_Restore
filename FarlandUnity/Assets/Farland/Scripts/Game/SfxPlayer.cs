using System.Collections;
using System.Collections.Generic;
using Farland.Content;
using UnityEngine;

namespace Farland.Game
{
    /// 효과음. 번호 → 파일은 battle_scene.json 의 sounds 규칙(콘텐츠 팩에서 교체 가능). 읽은 클립은 캐시한다.
    public class SfxPlayer : MonoBehaviour
    {
        FarlandContent content;
        AudioSource source;
        readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();

        public void Init(FarlandContent content)
        {
            this.content = content;
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.volume = content.BattleScene.soundVolume;
        }

        /// 쓰기 전에 미리 읽어 둔다(첫 재생 지연 방지)
        public IEnumerator Preload(IEnumerable<int> numbers)
        {
            foreach (var n in numbers)
            {
                var path = content.BattleScene.SoundPath(content.DB, n);
                if (path == null || cache.ContainsKey(path)) continue;
                AudioClip clip = null;
                yield return content.DB.LoadAudio(path, c => clip = c);
                cache[path] = clip;
            }
        }

        public void Play(int n)
        {
            var path = content.BattleScene.SoundPath(content.DB, n);
            if (path == null) return;
            if (cache.TryGetValue(path, out var clip)) { if (clip != null) source.PlayOneShot(clip); }
            else StartCoroutine(LoadAndPlay(n));
        }

        IEnumerator LoadAndPlay(int n)
        {
            yield return Preload(new[] { n });
            var path = content.BattleScene.SoundPath(content.DB, n);
            if (cache.TryGetValue(path, out var clip) && clip != null) source.PlayOneShot(clip);
        }
    }
}

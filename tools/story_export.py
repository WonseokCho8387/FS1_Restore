"""본편 대화·엔딩 산출물을 UnityAssets/ 에 쓴다.

Dialogue/STAGExx.json   MSG 스크립트 (msg_parse)
Dialogue/talk_calls.json FSMAIN의 talk() 호출부 143곳: 이벤트, 인자(유닛 슬롯), 스테이지, 훅
Ending/ending_timeline.json, Ending/ending_preview.mp4   엔딩(fs_ending 에뮬레이션)

사용: python story_export.py <extracted/iso> <extracted/audio> <UnityAssets> [--no-video]
"""
import json, os, subprocess, sys
import msg_parse, talk_calls, talk_stage

# talk() 호출 함수 중 스테이지 훅 테이블에서 닿지 않는 이야기 전용 함수
STORY_FUNCS = {
    "0x10bed": dict(stage=[1], hook=["prologue"]),      # 1스테이지 직전 프롤로그 (STAGE01 #0~#4)
    "0x116b8": dict(stage=[99], hook=["epilogue"]),     # [0x568]=0x62 → STAGE99, 이어서 음악 12와 엔딩
}
# 훅 번호의 의미는 호출 위치와 대사 내용으로 추정한 것
HOOK_NAMES = {
    0: "stageInit (0x5B7E, 스테이지 준비)", 1: "stageStart (0x536A, 개시 대화 '9')",
    2: "unitAction (0x9DDC, 이동/접촉 판정)", 3: "unitAction2 (0x9E3C)", 4: "turnCheck (0xA0A1 등)",
    5: "battleEvent (0xB0B6 등, 교전 중 대사)", 6: "stageEnd (0xB2BB 등, 격파·종료 대사 'z')",
}


def export_dialogue(iso, out):
    d = os.path.join(out, "Dialogue")
    n = msg_parse.export(iso, d)
    exe = os.path.join(iso, "FSMAIN.EXE")
    rows, _ = talk_stage.annotate(exe, talk_calls.scan(exe))
    for r in rows:
        if r["function"] in STORY_FUNCS:
            r.update(STORY_FUNCS[r["function"]])
    json.dump(dict(hooks=HOOK_NAMES, calls=rows), open(os.path.join(d, "talk_calls.json"), "w", encoding="utf-8"),
              ensure_ascii=False, indent=1)
    return n, len(rows)


def ending_timeline(raw):
    """에뮬레이션 기록(ending_raw.json)에서 Unity가 재구현하기 쉬운 연출 단계로 정리한다."""
    staff = raw["staff"]
    roll_start = staff[0]["t"]
    return dict(
        fps=60, totalFrames=raw["totalFrames"], music="track13 (음악 번호 12, FSMAIN 0x11564)",
        screen=dict(width=640, height=400),
        phases=[
            dict(start=0, end=864, kind="castle",
                 image="END1", x=304, y=0, w=336, h=400,
                 note="검은 화면에서 END1 팔레트로 페이드인(6프레임마다 1단계) 하며 144단계×6프레임 세로 패닝. "
                      "좌측 304px는 검정"),
            dict(start=864, end=991, kind="fadeOut", note="팔레트를 검정으로 페이드아웃(4프레임 간격)"),
            dict(start=991, end=roll_start, kind="couple", image="END2", x=304, y=0, w=336, h=400,
                 note="END2를 그리고 7프레임 간격으로 페이드인"),
            dict(start=roll_start, end=staff[-1]["t"] + 40, kind="staffRoll",
                 area=dict(x=0, y=0, w=304, h=400), lineHeight=20, framesPerLine=40, textX=8,
                 note="한 줄을 y=380에 그리고 40프레임 동안 2프레임당 1px 위로 스크롤. 흰 글자 + 검은 그림자"),
            dict(start=staff[-1]["t"] + 40, end=raw["totalFrames"], kind="rollOut",
                 note="'/'(끝) 이후 120프레임 더 스크롤"),
            dict(start=raw["totalFrames"], end=None, kind="hold", note="원작은 여기서 무한 대기(jmp $)"),
        ],
        staff=staff,
    )


def export_ending(iso, audio, out, video=True):
    import fs_ending
    d = os.path.join(out, "Ending")
    raw = fs_ending.export(iso, d, video=video)
    os.remove(os.path.join(d, "ending_raw.json"))
    json.dump(ending_timeline(raw), open(os.path.join(d, "ending_timeline.json"), "w", encoding="utf-8"),
              ensure_ascii=False, indent=1)
    if video:
        mux(os.path.join(d, "ending_preview.mp4"), os.path.join(audio, "track13.wav"))
    return raw["totalFrames"]


def mux(mp4, wav):
    import imageio_ffmpeg
    tmp = mp4 + ".tmp.mp4"
    subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-loglevel", "error", "-i", mp4, "-i", wav,
                    "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-shortest", tmp],
                   check=True)
    os.replace(tmp, mp4)


if __name__ == "__main__":
    iso, audio, out = sys.argv[1:4]
    print("dialogue blocks/calls", export_dialogue(iso, out))
    print("ending frames", export_ending(iso, audio, out, video="--no-video" not in sys.argv))

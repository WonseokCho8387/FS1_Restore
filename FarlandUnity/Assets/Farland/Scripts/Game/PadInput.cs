using UnityEngine;

namespace Farland.Game
{
    /// 게임패드(스팀덱·Xbox 배치) 입력. Input Manager 의 Pad*/DPad*/Right* 축과 joystick button 0~7 을 쓴다.
    /// A 결정 · B 취소 · X 턴 종료 · Y 전투 연출 켜기/끄기 · LB/RB 축소/확대 · View 조작 설명 · Menu 메뉴
    /// 왼쪽 스틱·D패드 = 커서 이동(칸 단위), 오른쪽 스틱 = 화면 이동.
    public static class PadInput
    {
        public static bool A => Input.GetKeyDown(KeyCode.JoystickButton0);
        public static bool B => Input.GetKeyDown(KeyCode.JoystickButton1);
        public static bool X => Input.GetKeyDown(KeyCode.JoystickButton2);
        public static bool Y => Input.GetKeyDown(KeyCode.JoystickButton3);
        public static bool LB => Input.GetKeyDown(KeyCode.JoystickButton4);
        public static bool RB => Input.GetKeyDown(KeyCode.JoystickButton5);
        public static bool View => Input.GetKeyDown(KeyCode.JoystickButton6);
        public static bool Menu => Input.GetKeyDown(KeyCode.JoystickButton7);
        public static bool AnyConfirm => A || Menu;

        /// 마지막 입력이 패드였나(마우스를 움직이면 false). 패드일 때는 마우스 커서를 숨기고 칸 커서를 쓴다
        public static bool Active { get; private set; }

        static Vector3 lastMouse;
        static int polledFrame = -1;
        static float repeatAt;
        static Vector2Int held;

        static float Axis(string name)
        {
            try { return Input.GetAxisRaw(name); } catch (System.ArgumentException) { return 0; }     // 축이 없는 설정에서도 동작
        }

        public static Vector2 Stick
        {
            get
            {
                var v = new Vector2(Axis("PadX") + Axis("DPadX") + Axis("DPadXL"), Axis("PadY") + Axis("DPadY") + Axis("DPadYL"));
                return v.magnitude < 0.5f ? Vector2.zero : v;
            }
        }

        public static Vector2 Look
        {
            get { var v = new Vector2(Axis("RightX"), Axis("RightY")); return v.magnitude < 0.25f ? Vector2.zero : v; }
        }

        /// 매 프레임 한 번: 패드/마우스 중 무엇을 쓰는지 판정
        public static void Poll()
        {
            if (polledFrame == Time.frameCount) return;
            polledFrame = Time.frameCount;
            bool pad = Stick != Vector2.zero || Look != Vector2.zero;
            for (var k = KeyCode.JoystickButton0; k <= KeyCode.JoystickButton9 && !pad; k++) pad |= Input.GetKeyDown(k);
            if (pad && !Active) { Active = true; Cursor.visible = false; }
            if (Active && ((Input.mousePosition - lastMouse).sqrMagnitude > 4 || Input.GetMouseButtonDown(0)))
            { Active = false; Cursor.visible = true; }
            lastMouse = Input.mousePosition;
        }

        /// 칸 이동: 처음 누를 때 1칸, 계속 누르면 0.25초 뒤부터 0.08초마다 (헥스 맵이라 상하는 y 만 바꾼다)
        public static Vector2Int Step()
        {
            var s = Stick;
            var dir = new Vector2Int(Mathf.Abs(s.x) >= 0.5f ? (int)Mathf.Sign(s.x) : 0, Mathf.Abs(s.y) >= 0.5f ? -(int)Mathf.Sign(s.y) : 0);
            if (dir == Vector2Int.zero) { held = dir; return dir; }
            if (dir != held) { held = dir; repeatAt = Time.unscaledTime + 0.25f; return dir; }
            if (Time.unscaledTime < repeatAt) return Vector2Int.zero;
            repeatAt = Time.unscaledTime + 0.08f;
            return dir;
        }
    }
}

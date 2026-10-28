/// 상태를 저장·복원할 수 있는 난수(xorshift32). System.Random 자리에 그대로 쓴다.
/// 전투 중 저장에 state 를 넣어 두면 불러온 뒤에도 크리티컬·적 AI 선택이 저장 전과 같다.
public sealed class FarlandRandom : System.Random
{
    public uint state;

    public FarlandRandom(int seed)
    {
        state = seed != 0 ? (uint)seed : (uint)System.Environment.TickCount ^ 0x9E3779B9u;
        if (state == 0) state = 1;
    }

    uint NextU()
    {
        uint x = state;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return state = x;
    }

    protected override double Sample() => NextU() / 4294967296.0;
    public override double NextDouble() => Sample();
    public override int Next() => (int)(NextU() >> 1);
    public override int Next(int maxValue) => maxValue <= 0 ? 0 : (int)(Sample() * maxValue);
    public override int Next(int minValue, int maxValue) => minValue >= maxValue ? minValue : minValue + (int)(Sample() * ((long)maxValue - minValue));
    public override void NextBytes(byte[] buffer) { for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextU(); }
}

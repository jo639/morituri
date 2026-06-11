namespace Morituri.Sim.Core;

/// <summary>
/// 결정론적 시뮬레이션용 RNG (아키텍처 원칙 B).
/// - UnityEngine.Random / System.Random 사용 금지: 플랫폼·버전 간 결과가 보장되지 않음.
/// - xorshift64* 알고리즘: 같은 시드 → 모든 플랫폼에서 같은 수열.
/// - 경기당 시드 하나에서 파생된 인스턴스만 사용한다. (리플레이 = 시드 + 입력 데이터)
/// </summary>
public sealed class SimRandom
{
    private ulong _state;

    public SimRandom(ulong seed)
    {
        // 시드 0은 xorshift에서 고정점이므로 splitmix64로 한 번 섞어 회피
        _state = SplitMix64(seed == 0 ? 0x9E3779B97F4A7C15UL : seed);
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public ulong NextUInt64()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>[0, 1) 구간 float.</summary>
    public float NextFloat01()
    {
        // 상위 24비트만 사용 → float 가수부에 정확히 들어감
        return (NextUInt64() >> 40) * (1.0f / (1 << 24));
    }

    /// <summary>[min, max) 구간 균등분포. 예: 데미지 Variance U(0.92, 1.08)</summary>
    public float Range(float min, float max) => min + NextFloat01() * (max - min);

    /// <summary>확률 판정 (0~1). 성격 트리거 발동 등에 사용.</summary>
    public bool Roll(float probability) => NextFloat01() < probability;

    /// <summary>하위 시스템용 파생 RNG (예: 선수A/선수B 별도 스트림).</summary>
    public SimRandom Derive(ulong streamId) => new(SplitMix64(_state ^ streamId));
}

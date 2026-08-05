using Morituri.Sim.Core;

namespace Morituri.Sim.Data;

/// <summary>천부 재능 등급 (최초 스탯 버짓). 노예가 다수, 카이사르가 극소.</summary>
public enum TalentGrade { Slave, Fighter, Champion, Consul, Immortal, Caesar }

/// <summary>잠재력 등급 (최대 스탯 버짓 = 추후 성장 상한). 잿불~태양.</summary>
public enum PotentialGrade { Ember, Spark, Torch, Flame, Beacon, Sun }

/// <summary>한 모리투리의 천부 부여 결과: 등급 + 초기 스탯 + 성장 상한 버짓.</summary>
public readonly record struct Endowment(
    TalentGrade Talent, PotentialGrade Potential,
    FighterStats Stats, float TalentBudget, float PotentialBudget);

/// <summary>
/// 천부 기본스탯 분포·확률 시스템 (문서[4] 1장 확장).
/// 2축: 천부(최초 버짓) + 잠재력(최대 버짓, 추후 성장 상한). 천부 상위 3등급만 잠재력에 약한 상관.
/// 완만 기조: baseline 420 중심 ±~12%로 시작, 승률 데이터 보고 확대.
/// 결정론: 모든 추첨은 주입된 SimRandom으로만 (원칙 B).
/// </summary>
public static class StatGen
{
    // 천부: (확률, 버짓). 노예=다수·약 평균이하, 상위로 갈수록 강. baseline 420.
    private static readonly (TalentGrade G, float P, float Budget)[] Talents =
    {
        (TalentGrade.Slave,    0.500f, 395f),
        (TalentGrade.Fighter,  0.280f, 410f),
        (TalentGrade.Champion, 0.140f, 425f),
        (TalentGrade.Consul,   0.060f, 442f),
        (TalentGrade.Immortal, 0.018f, 460f),
        (TalentGrade.Caesar,   0.002f, 480f),
    };

    // 잠재력 최대버짓 (잿불~태양). 천부 < 잠재력일 때만 성장 여지.
    private static readonly float[] PotentialBudgets = { 420f, 445f, 470f, 495f, 520f, 545f };

    // 천부별 잠재력 확률 (행=천부 0~5, 열=잠재력 0~5). 각 행 합=1. 천부 높을수록 상위 잠재력 편향.
    private static readonly float[][] PotByTalent =
    {
        new[] { 0.35f, 0.30f, 0.20f, 0.10f, 0.04f, 0.01f }, // 노예
        new[] { 0.20f, 0.30f, 0.25f, 0.15f, 0.08f, 0.02f }, // 투사
        new[] { 0.10f, 0.20f, 0.30f, 0.25f, 0.12f, 0.03f }, // 챔피언
        new[] { 0.05f, 0.10f, 0.25f, 0.35f, 0.20f, 0.05f }, // 집정관
        new[] { 0.02f, 0.05f, 0.15f, 0.40f, 0.30f, 0.08f }, // 불멸자
        new[] { 0.00f, 0.03f, 0.12f, 0.35f, 0.40f, 0.10f }, // 카이사르
    };

    private const float StatMin = 20f, StatMax = 150f;  // 각 축 하한·상한
    private const float HpPerPoint = 10f;               // HP축: 포인트 70 = HP 700

    /// <summary>한 모리투리의 천부+잠재력+초기 스탯을 뽑는다.</summary>
    public static Endowment Roll(SimRandom rng)
    {
        int ti = PickIndex(rng, Talents.Length, i => Talents[i].P);
        // 잠재력: 천부 등급별 고유 확률 분포로 추첨 (천부 높을수록 상위 잠재력 편향).
        int pi = PickIndex(rng, 6, i => PotByTalent[ti][i]);

        float potBudget = MathF.Max(Talents[ti].Budget, PotentialBudgets[pi]);  // 성장 상한 ≥ 천부
        var stats = Distribute(rng, Talents[ti].Budget);

        return new Endowment(Talents[ti].G, (PotentialGrade)pi, stats, Talents[ti].Budget, potBudget);
    }

    /// <summary>가중 확률 추첨 → 인덱스 (확률 합 = 1 가정).</summary>
    private static int PickIndex(SimRandom rng, int n, Func<int, float> prob)
    {
        float r = rng.NextFloat01(), acc = 0f;
        for (int i = 0; i < n; i++)
        {
            acc += prob(i);
            if (r < acc) return i;
        }
        return n - 1;
    }

    /// <summary>버짓을 6축에 약한 특화(±15%)로 분배. HP는 ×10 환산. 각 축 [20,150] 클램프.</summary>
    private static FighterStats Distribute(SimRandom rng, float budget)
    {
        Span<float> w = stackalloc float[6];
        float sum = 0f;
        for (int i = 0; i < 6; i++) { w[i] = rng.Range(0.85f, 1.15f); sum += w[i]; }
        Span<float> s = stackalloc float[6];
        for (int i = 0; i < 6; i++)
            s[i] = Math.Clamp(budget * w[i] / sum, StatMin, StatMax);
        // s = [Atk, Def, HpPoint, Spd, Aspd, Rct]
        return new FighterStats(s[0], s[1], s[2] * HpPerPoint, s[3], s[4], s[5]);
    }
}

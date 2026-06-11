namespace Morituri.Sim.Data;

/// <summary>
/// 6대 기본 스탯 (문서[4] 1장). 범위: ATK/DEF/SPD/ASPD/RCT 1~150, HP 400~1200.
/// 전성기 평균 선수 = 모든 스탯 70 기준으로 밸런싱.
/// </summary>
public readonly record struct FighterStats(
    float Atk,
    float Def,
    float HpMax,
    float Spd,
    float Aspd,
    float Rct)
{
    /// <summary>밸런싱 기준점: 평균 선수 (스탯 70, HP는 1~150 스케일 70 ≈ 700).</summary>
    public static readonly FighterStats Baseline = new(70, 70, 700, 70, 70, 70);
}

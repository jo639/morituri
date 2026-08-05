using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Headless;

/// <summary>천부 스탯 분포·확률 시스템 검증: 등급 빈도(실측 vs 기대), 버짓·스탯 분포, 천부→잠재력 상관.</summary>
static class StatGenReport
{
    static readonly string[] TName = { "노예", "투사", "챔피언", "집정관", "불멸자", "카이사르" };
    static readonly string[] PName = { "잿불", "불씨", "횃불", "화염", "봉화", "태양" };
    static readonly float[] TExp = { 50f, 28f, 14f, 6f, 1.8f, 0.2f };

    public static void Run(int n)
    {
        var rng = new SimRandom(20260617);
        var tc = new int[6]; var pc = new int[6];
        double tBud = 0, pBud = 0;
        var pByT = new long[6]; var pByTn = new int[6];
        var ptm = new int[6, 6];
        var sSum = new double[6]; var sMin = new float[6]; var sMax = new float[6];
        for (int i = 0; i < 6; i++) { sMin[i] = float.MaxValue; sMax[i] = float.MinValue; }
        Span<float> a = stackalloc float[6];

        for (int i = 0; i < n; i++)
        {
            var e = StatGen.Roll(rng);
            tc[(int)e.Talent]++; pc[(int)e.Potential]++;
            tBud += e.TalentBudget; pBud += e.PotentialBudget;
            pByT[(int)e.Talent] += (int)e.Potential; pByTn[(int)e.Talent]++;
            ptm[(int)e.Talent, (int)e.Potential]++;
            var s = e.Stats;
            a[0] = s.Atk; a[1] = s.Def; a[2] = s.HpMax; a[3] = s.Spd; a[4] = s.Aspd; a[5] = s.Rct;
            for (int k = 0; k < 6; k++) { sSum[k] += a[k]; sMin[k] = MathF.Min(sMin[k], a[k]); sMax[k] = MathF.Max(sMax[k], a[k]); }
        }

        Console.WriteLine($"=== 천부 스탯 분포 ({n:N0}명, 시드 20260617) ===\n");
        Console.WriteLine("천부 등급     실측%   기대%   잠재력 평균(상관 확인)");
        for (int i = 0; i < 6; i++)
        {
            float avgPot = pByTn[i] > 0 ? (float)pByT[i] / pByTn[i] : 0f;
            Console.WriteLine($"  {TName[i],-8} {100f * tc[i] / n,6:F2}  {TExp[i],5:F1}   {PName[(int)MathF.Round(avgPot)]} ({avgPot:F2})");
        }

        Console.WriteLine("\n잠재력 등급   실측%");
        for (int i = 0; i < 6; i++)
            Console.WriteLine($"  {PName[i],-8} {100f * pc[i] / n,6:F2}");

        Console.WriteLine("\n천부＼잠재력  잿불  불씨  횃불  화염  봉화  태양  (실측%, 행=천부)");
        for (int t = 0; t < 6; t++)
        {
            Console.Write($"  {TName[t],-8}");
            for (int p = 0; p < 6; p++)
                Console.Write($" {(tc[t] > 0 ? 100f * ptm[t, p] / tc[t] : 0f),5:F0}");
            Console.WriteLine();
        }

        Console.WriteLine($"\n버짓 평균: 천부 {tBud / n:F1} / 잠재력 {pBud / n:F1} (성장여지 {(pBud - tBud) / n:F1})");
        string[] sn = { "Atk ", "Def ", "HP  ", "Spd ", "Aspd", "Rct " };
        Console.WriteLine("\n스탯축    평균     min    max");
        for (int k = 0; k < 6; k++)
            Console.WriteLine($"  {sn[k]} {sSum[k] / n,7:F1} {sMin[k],6:F0} {sMax[k],6:F0}");
    }
}

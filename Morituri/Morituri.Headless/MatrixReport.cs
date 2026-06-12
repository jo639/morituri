using System.Text;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 문서[4] 11장 상성 매트릭스 검증: 전술 5종 × 동일 무기(검)·동일 성격(냉철함) 25칸 배치.
/// 문서[4] 12장 스펙의 CSV 리포트(승률/KO율/경기시간/역전율/행동 엔트로피)를 출력한다.
/// 선공 편향(이슈 #4 미해결)이 상성 측정을 오염시키지 않도록 시드 홀짝으로 코너를 교대 배치.
/// </summary>
internal static class MatrixReport
{
    private static readonly (string Id, string Name)[] T =
    {
        ("TAC_PRESSURE", "압박"), ("TAC_COUNTER", "카운터"), ("TAC_ZONER", "견제"),
        ("TAC_BRAWLER", "난전"), ("TAC_DEFENDER", "방어"),
    };

    // 문서[4] 11장 초기 가설: 행 전술이 열 전술을 상대로 기대하는 승률(%).
    private static readonly float[,] Hypo =
    {
        { 50, 40, 60, 45, 55 },
        { 60, 50, 45, 58, 40 },
        { 40, 55, 50, 62, 48 },
        { 55, 42, 38, 50, 60 },
        { 45, 60, 52, 40, 50 },
    };

    private enum Act { Light, Heavy, Feint, Guard, Dodge }

    public static void Run(int gamesPerCell, string csvPath)
    {
        Console.WriteLine($"=== 상성 매트릭스 배치: 전술 5종 × 검 × 냉철함, 칸당 {gamesPerCell}경기 ===");
        Console.WriteLine("    (시드 홀짝 코너 교대로 선공 편향 상쇄)\n");

        var csv = new StringBuilder();
        csv.AppendLine("rowTactic,colTactic,games,winRow,winCol,draw,winRowPct,koPct,avgDurSec," +
                       "comebackPct,entropyRow,topActionRow,topActionRowPct,hypoPct,deviationPp");

        float[,] result = new float[5, 5];
        var warnings = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 5; i++)
        for (int j = 0; j < 5; j++)
        {
            var row = new FighterDef(T[i].Name, FighterStats.Baseline, "WPN_SWORD", T[i].Id, "PER_CALM");
            var col = new FighterDef(T[j].Name, FighterStats.Baseline, "WPN_SWORD", T[j].Id, "PER_CALM");

            int winRow = 0, winCol = 0, draw = 0, ko = 0, comebacks = 0, decided = 0;
            double durSum = 0;
            var actRow = new long[5];
            var events = new List<SimEvent>(1024);

            for (ulong seed = 1; seed <= (ulong)gamesPerCell; seed++)
            {
                bool rowFirst = (seed & 1) == 1;       // 코너 교대
                int rowIdx = rowFirst ? 0 : 1;
                events.Clear();
                var r = new MatchSim().Run(rowFirst ? row : col, rowFirst ? col : row, seed, events);

                if (r.Winner == -1) draw++;
                else
                {
                    decided++;
                    bool rowWon = r.Winner == rowIdx;
                    if (rowWon) winRow++; else winCol++;
                    var w = r.Winner == 0 ? r.StatsA : r.StatsB;
                    if (w.MinHpPct <= 0.30f) comebacks++;
                }
                if (r.Reason == "KO") ko++;
                durSum += r.DurationSec;

                foreach (var e in events)
                {
                    switch (e)
                    {
                        case AttackSwung s when s.FighterId == rowIdx:
                            actRow[(int)(s.IsFeint ? Act.Feint : s.MotionId.EndsWith("_H") ? Act.Heavy : Act.Light)]++;
                            break;
                        case StateChanged c when c.FighterId == rowIdx && c.To == FighterState.Guard:
                            actRow[(int)Act.Guard]++;
                            break;
                        case StateChanged c when c.FighterId == rowIdx && c.To == FighterState.Dodge:
                            actRow[(int)Act.Dodge]++;
                            break;
                    }
                }
            }

            float winPct = decided > 0 ? 100f * winRow / decided : 50f; // 전 경기 무승부 = 우열 없음
            result[i, j] = winPct;
            float dev = winPct - Hypo[i, j];

            (float entropy, Act top, float topPct) = Entropy(actRow);
            if (topPct >= 60f)
                warnings.Add($"{T[i].Name} vs {T[j].Name}: 행동 '{top}' {topPct:F0}% — 패턴화 경고 (≥60%)");

            csv.AppendLine($"{T[i].Id},{T[j].Id},{gamesPerCell},{winRow},{winCol},{draw}," +
                           $"{winPct:F1},{100f * ko / gamesPerCell:F1},{durSum / gamesPerCell:F1}," +
                           $"{100f * comebacks / Math.Max(1, decided):F1}," +
                           $"{entropy:F3},{top},{topPct:F1},{Hypo[i, j]:F0},{dev:F1}");
        }
        sw.Stop();

        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(true)); // BOM: 엑셀 한글 호환

        PrintMatrix(result, warnings);
        Console.WriteLine($"\nCSV 저장: {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"총 {25 * gamesPerCell}경기 / {sw.Elapsed.TotalSeconds:F0}초");
    }

    private static (float entropy, Act top, float topPct) Entropy(long[] counts)
    {
        long total = counts.Sum();
        if (total == 0) return (0f, Act.Light, 0f);
        double h = 0;
        int top = 0;
        for (int k = 0; k < counts.Length; k++)
        {
            if (counts[k] > counts[top]) top = k;
            if (counts[k] == 0) continue;
            double p = (double)counts[k] / total;
            h -= p * Math.Log(p);
        }
        return ((float)(h / Math.Log(counts.Length)), (Act)top, 100f * counts[top] / total);
    }

    private static void PrintMatrix(float[,] r, List<string> warnings)
    {
        Console.WriteLine("행 전술의 열 전술 상대 승률 % (괄호 = 가설 대비 편차, ±8%p 초과 시 ★)");
        Console.Write("        ");
        for (int j = 0; j < 5; j++) Console.Write($"{T[j].Name,8}");
        Console.WriteLine();

        int failCells = 0;
        for (int i = 0; i < 5; i++)
        {
            Console.Write($"{T[i].Name,6}  ");
            for (int j = 0; j < 5; j++)
            {
                float dev = r[i, j] - Hypo[i, j];
                bool fail = MathF.Abs(dev) > 8f;
                if (fail) failCells++;
                Console.Write($"{r[i, j],5:F1}{(fail ? "★" : " "),2} ");
            }
            Console.WriteLine();
        }

        // 가위바위보 순환: 압박 > 견제 > 난전 > 방어 > 카운터 > 압박 (문서[4] 11장)
        (int a, int b, string label)[] cycle = { (0, 2, "압박>견제"), (2, 3, "견제>난전"), (3, 4, "난전>방어"), (4, 1, "방어>카운터"), (1, 0, "카운터>압박") };
        Console.WriteLine("\n순환 검증: " + string.Join(" | ",
            cycle.Select(c => $"{c.label} {(r[c.a, c.b] > 50f ? "✅" : "❌")}({r[c.a, c.b]:F0}%)")));
        Console.WriteLine($"가설 ±8%p 초과: {failCells}/25칸");

        if (warnings.Count > 0)
        {
            Console.WriteLine("\n⚠ 행동 패턴화 경고:");
            foreach (var w in warnings) Console.WriteLine("   " + w);
        }
    }
}

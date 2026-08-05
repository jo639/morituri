using System.Diagnostics;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// M4-b 체계적 co-tuning 하니스. 여러 전역 레버를 동시에 그리드 스윕하고, 각 조합으로 5×5 매트릭스를 돌려
/// 가설(Hypo) 대비 불균형 점수를 매겨 최적 조합을 랭킹한다. 수동 단일 레버 반복이 불균형을 옮기기만 하는
/// 문제(벽-접선 탈출 도입 후 방어형 무적 → 카이팅비용 → 난전형 지배 …)를 동시 최적화로 푼다.
/// 벽-접선 탈출은 foundation이라 항상 켜진 상태(코드)로 스윕한다.
/// </summary>
internal static class Tune
{
    private static readonly string[] Tac =
        { "TAC_PRESSURE", "TAC_COUNTER", "TAC_ZONER", "TAC_BRAWLER", "TAC_DEFENDER" };

    // MatrixReport.Hypo와 동일 (행 전술이 열 전술 상대 기대 승률 %)
    private static readonly float[,] Hypo =
    {
        { 50, 40, 60, 45, 55 },
        { 60, 50, 45, 58, 40 },
        { 40, 55, 50, 62, 48 },
        { 55, 42, 38, 50, 60 },
        { 45, 60, 52, 40, 50 },
    };

    // 가위바위보 순환: 압박>견제>난전>방어>카운터>압박 (win[a,b] > 50 이어야 성립)
    private static readonly (int a, int b, string label)[] Cycle =
        { (0, 2, "압박>견제"), (2, 3, "견제>난전"), (3, 4, "난전>방어"), (4, 1, "방어>카운터"), (1, 0, "카운터>압박") };

    public static void Run(int gamesPerCell)
    {
        // 스윕 차원 (벽-접선 탈출 ON 전제). 핵심 레버: 카이팅 비용·과금범위·가드시킨공격 후딜·가드 칩딜.
        float[] kiteCost = { 0f, 0.8f, 1.5f, 2.5f };
        float[] kiteRange = { 0f, 2.0f, 3.0f };      // 0=전무기, 2.0=검(2.4)+장무기, 3.0=장사거리 전용
        float[] guardedRec = { 1.1f, 1.65f };
        float[] guardDmg = { 0.25f, 0.40f };

        var results = new List<(double score, double mad, int broken, int failCells, string desc)>();
        var sw = Stopwatch.StartNew();
        int combos = kiteCost.Length * kiteRange.Length * guardedRec.Length * guardDmg.Length;
        Console.WriteLine($"=== M4-b 튜닝 스윕: {combos}조합 × {gamesPerCell}경기/칸 (탈출 ON) ===\n");

        foreach (var kc in kiteCost)
        foreach (var kr in kiteRange)
        foreach (var gr in guardedRec)
        foreach (var gd in guardDmg)
        {
            var c = BalanceConstants.Default with
            {
                KiteStamCostPerSec = kc, KiteCostMinRange = kr,
                GuardedRecoveryMult = gr, GuardDmgMult = gd,
            };
            var (mad, broken, fail, _) = EvalMatrix(c, gamesPerCell);
            double score = mad + 15.0 * broken;   // 순환 깨짐은 강한 패널티 (밸런스 척추)
            results.Add((score, mad, broken, fail, $"kite={kc} range={kr} gRec={gr:F2} gDmg={gd:F2}"));
        }
        sw.Stop();

        results.Sort((x, y) => x.score.CompareTo(y.score));
        Console.WriteLine($"점수(낮을수록 좋음) = 평균절대편차(MAD) + 15×깨진순환수\n");
        Console.WriteLine("  score | MAD  | 순환깨짐 | ±8%초과칸 | 레버");
        foreach (var r in results.Take(12))
            Console.WriteLine($"  {r.score,5:F1} | {r.mad,4:F1} | {r.broken}/5      | {r.failCells,2}/25     | {r.desc}");
        Console.WriteLine($"\n총 {combos * 25 * gamesPerCell}경기 / {sw.Elapsed.TotalSeconds:F0}초");
        Console.WriteLine("최적 조합으로 matrix 풀검증 권장: dotnet run -- matrix 250 (해당 레버 적용 후)");
    }

    private static (double mad, int broken, int failCells, double polar) EvalMatrix(in BalanceConstants c, int n)
        => EvalMatrix(c, null, n);

    private static (double mad, int broken, int failCells, double polar) EvalMatrix(
        in BalanceConstants c, IReadOnlyDictionary<string, TacticsProfile>? tac, int n)
    {
        var win = new float[5, 5];
        for (int i = 0; i < 5; i++)
        for (int j = 0; j < 5; j++)
        {
            var row = new FighterDef(Tac[i], FighterStats.Baseline, "WPN_SWORD", Tac[i], "PER_CALM");
            var col = new FighterDef(Tac[j], FighterStats.Baseline, "WPN_SWORD", Tac[j], "PER_CALM");
            int wr = 0, dec = 0;
            for (ulong s = 1; s <= (ulong)n; s++)
            {
                bool rowFirst = (s & 1) == 1;
                int ri = rowFirst ? 0 : 1;
                var r = new MatchSim(c, null, tac).Run(rowFirst ? row : col, rowFirst ? col : row, s);
                if (r.Winner != -1) { dec++; if (r.Winner == ri) wr++; }
            }
            win[i, j] = dec > 0 ? 100f * wr / dec : 50f;
        }
        double mad = 0; int fail = 0; double polar = 0;
        for (int i = 0; i < 5; i++)
        for (int j = 0; j < 5; j++)
        {
            float d = MathF.Abs(win[i, j] - Hypo[i, j]);
            mad += d;
            if (d > 8f) fail++;
            if (i != j) polar += Math.Max(0f, MathF.Abs(win[i, j] - 50f) - 40f); // >90 또는 <10 극단 셀만 패널티
        }
        mad /= 25;
        int broken = 0;
        foreach (var cy in Cycle) if (win[cy.a, cy.b] <= 50f) broken++;
        return (mad, broken, fail, polar);
    }

    // 앵커 매치업: sword-매트릭스에 안 잡히는 무기/성격 매치업을 목표에 포함(co-tune 확장).
    // 전술 override는 tacticId로 적용되므로, 이 빌드들의 전술(BRAWLER/COUNTER/PRESSURE)도 스윕 영향을 받는다.
    private sealed record Anchor(string Label, FighterDef A, FighterDef B, float TargetWinAPct, float Weight);

    private static Anchor[] Anchors() => new[]
    {
        // 기획 필수 플래그십: 버서커(난전+도끼) vs 전술가(카운터+창) — 목표 전술가 ~57% = 버서커 ~43%
        new Anchor("버서커vs전술가", FighterDef.Berserker, FighterDef.Tactician, 43f, 0.5f),
        // 성격 가독성: 학살자(압박+잔혹) vs 허당(압박+겁쟁이) — 잔혹 압박이 분명히 우위(~70%)
        new Anchor("학살vs허당",
            new FighterDef("학살자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL"),
            new FighterDef("허당", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_COWARD"), 70f, 0.3f),
    };

    private static float EvalAnchor(in Anchor an, IReadOnlyDictionary<string, TacticsProfile>? tac, int n)
    {
        int wa = 0, dec = 0;
        for (ulong s = 1; s <= (ulong)n; s++)
        {
            var r = new MatchSim(null, null, tac).Run(an.A, an.B, s);
            if (r.Winner != -1) { dec++; if (r.Winner == 0) wa++; }
        }
        return dec > 0 ? 100f * wa / dec : 50f;
    }

    // 통합 점수 = sword매트릭스(MAD + 15×순환깨짐) + Σ 앵커가중×|승률 - 목표|
    private static double Score(IReadOnlyDictionary<string, TacticsProfile> tac, int n, out double mad, out int broken, out string anchorStr)
    {
        var (m, b, _, polar) = EvalMatrix(BalanceConstants.Default, tac, n);
        mad = m; broken = b;
        double s = m + 15.0 * b + 0.6 * polar;   // 양극화(0/100 극단 셀) 패널티 — 소프트 매치업 유도
        var parts = new List<string>();
        foreach (var an in Anchors())
        {
            float w = EvalAnchor(an, tac, n);
            s += an.Weight * Math.Abs(w - an.TargetWinAPct);
            parts.Add($"{an.Label} {w:F0}%(목표{an.TargetWinAPct:F0})");
        }
        anchorStr = string.Join(", ", parts);
        return s;
    }

    // 전술 파라미터 좌표하강: baseline에서 (전술,파라미터)별로 후보값을 훑어 점수를 낮추는 값으로 고정, 패스 반복.
    // 목표 = sword매트릭스 + 무기/성격 앵커. 벽-접선 탈출 ON 전제.
    private sealed record Knob(string Tac, string Name, float[] Vals, Func<TacticsProfile, float, TacticsProfile> Set);

    public static void RunDescent(int gamesPerCell, int passes)
    {
        var cur = new Dictionary<string, TacticsProfile>();
        foreach (var t in Tac) cur[t] = TacticsTable.Get(t);

        var knobs = new Knob[]
        {
            new("TAC_DEFENDER", "CounterWin",  new[]{0.10f,0.30f,0.50f},      (p,v)=>p with{CounterWindow=v}),
            new("TAC_DEFENDER", "GuardBias",   new[]{0.40f,0.60f,0.80f},      (p,v)=>p with{GuardBias=v}),
            new("TAC_DEFENDER", "Aggr",        new[]{0.20f,0.40f,0.60f},      (p,v)=>p with{Aggression=v}),
            new("TAC_DEFENDER", "PrefDist",    new[]{1.5f,2.1f,2.7f},         (p,v)=>p with{PreferredDistance=v}),
            new("TAC_COUNTER",  "CounterWin",  new[]{0.50f,0.65f,0.80f},      (p,v)=>p with{CounterWindow=v}),
            new("TAC_COUNTER",  "GuardBias",   new[]{0.35f,0.50f,0.65f},      (p,v)=>p with{GuardBias=v}),
            new("TAC_COUNTER",  "PrefDist",    new[]{2.4f,3.0f,3.6f},         (p,v)=>p with{PreferredDistance=v}),
            new("TAC_BRAWLER",  "Aggr",        new[]{0.60f,0.80f,0.95f},      (p,v)=>p with{Aggression=v}),
            new("TAC_BRAWLER",  "GuardBias",   new[]{0.10f,0.25f,0.40f},      (p,v)=>p with{GuardBias=v}),
            new("TAC_PRESSURE", "Aggr",        new[]{0.50f,0.70f,0.85f},      (p,v)=>p with{Aggression=v}),
            new("TAC_PRESSURE", "GuardBias",   new[]{0.10f,0.20f,0.35f},      (p,v)=>p with{GuardBias=v}),
            new("TAC_ZONER",    "PrefDist",    new[]{3.6f,4.2f,4.8f},         (p,v)=>p with{PreferredDistance=v}),
        };

        var sw = Stopwatch.StartNew();
        double best = Score(cur, gamesPerCell, out double mad0, out int br0, out string anch0);
        Console.WriteLine($"=== 전술 좌표하강 ({passes}패스 × {knobs.Length}노브 × {gamesPerCell}경기/칸, 탈출 ON, 앵커 포함) ===");
        Console.WriteLine($"시작: MAD {mad0:F1}, 순환깨짐 {br0}/5, score {best:F1} | {anch0}\n");

        for (int pass = 1; pass <= passes; pass++)
        {
            foreach (var k in knobs)
            {
                float curVal = GetVal(cur[k.Tac], k.Name);
                float bestVal = curVal; double bestScore = best;
                foreach (var v in k.Vals)
                {
                    var trial = new Dictionary<string, TacticsProfile>(cur) { [k.Tac] = k.Set(cur[k.Tac], v) };
                    double sc = Score(trial, gamesPerCell, out _, out _, out _);
                    if (sc < bestScore - 0.01) { bestScore = sc; bestVal = v; }
                }
                if (bestVal != curVal) { cur[k.Tac] = k.Set(cur[k.Tac], bestVal); best = bestScore; }
                Console.WriteLine($"  P{pass} {k.Tac.Replace("TAC_",""),-9} {k.Name,-10} {curVal:F2}→{bestVal:F2}  score {best:F1}");
            }
        }
        sw.Stop();

        double finalScore = Score(cur, gamesPerCell, out double madF, out int brF, out string anchF);
        var (_, _, failF, polarF) = EvalMatrix(BalanceConstants.Default, cur, gamesPerCell);
        Console.WriteLine($"\n최종: MAD {madF:F1}, 순환깨짐 {brF}/5, ±8%초과 {failF}/25, 양극화 {polarF:F0}, score {finalScore:F1}  ({sw.Elapsed.TotalSeconds:F0}s)");
        Console.WriteLine($"앵커: {anchF}");
        Console.WriteLine("권장 전술값(baseline 대비 변경):");
        foreach (var t in Tac)
        {
            var b = TacticsTable.Get(t); var p = cur[t];
            var diffs = new List<string>();
            if (p.PreferredDistance != b.PreferredDistance) diffs.Add($"PrefDist {b.PreferredDistance:F2}→{p.PreferredDistance:F2}");
            if (p.Aggression != b.Aggression) diffs.Add($"Aggr {b.Aggression:F2}→{p.Aggression:F2}");
            if (p.CounterWindow != b.CounterWindow) diffs.Add($"CounterWin {b.CounterWindow:F2}→{p.CounterWindow:F2}");
            if (p.GuardBias != b.GuardBias) diffs.Add($"GuardBias {b.GuardBias:F2}→{p.GuardBias:F2}");
            if (diffs.Count > 0) Console.WriteLine($"  {t.Replace("TAC_",""),-9}: {string.Join(", ", diffs)}");
        }
    }

    private static float GetVal(TacticsProfile p, string name) => name switch
    {
        "CounterWin" => p.CounterWindow,
        "GuardBias" => p.GuardBias,
        "Aggr" => p.Aggression,
        "PrefDist" => p.PreferredDistance,
        _ => 0f,
    };
}

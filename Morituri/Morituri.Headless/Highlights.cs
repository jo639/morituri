using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Data;
using Morituri.Sim.Match;
using Morituri.Sim.Serialization;

namespace Morituri.Headless;

/// <summary>명경기 한 건 = 재현용 시드 + 분류 태그 (영속 세계의 "역사 보존"은 이 목록 하나로 가벼워진다).</summary>
public sealed record HighlightEntry(ulong Seed, string Matchup, string Kind, int Winner, float WinnerMinHpPct);

public sealed record HighlightsReport(int SchemaVer, int GamesPerMatchup, IReadOnlyList<HighlightEntry> Highlights);

/// <summary>
/// 배치를 훑어 "역전 명경기" 시드를 자동 태깅해 highlights.json으로 수집한다(로드맵 M3.5).
/// - comeback: 승자가 사선(HP 10% 이하)까지 몰렸다 이김. 30%는 공격적 매치업서 90%+가 걸려 선별력이
///   없으므로(예: 버서커:전술가 89.5%) 명경기 목록은 사선 기준으로 큐레이션한다.
/// - taunt_reversal: 도발한 쪽이 졌다 (오만함의 방심이 처벌당한 경기 — M3-B 산물)
/// </summary>
public static class Highlights
{
    private const float ComebackHpPct = 0.10f;

    private static readonly (string Name, FighterDef A, FighterDef B)[] Matchups =
    {
        ("버서커vs전술가", FighterDef.Berserker, FighterDef.Tactician),
        ("오만챔피언vs도전자",
            new FighterDef("챔피언", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT"),
            new FighterDef("도전자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CALM")),
    };

    public static void Collect(int games, string outPath)
    {
        var entries = new List<HighlightEntry>();
        foreach (var (name, a, b) in Matchups)
        {
            for (ulong seed = 1; seed <= (ulong)games; seed++)
            {
                var r = new MatchSim().Run(a, b, seed);
                if (r.Winner < 0) continue;
                var winner = r.Winner == 0 ? r.StatsA : r.StatsB;
                var loser  = r.Winner == 0 ? r.StatsB : r.StatsA;

                if (winner.MinHpPct <= ComebackHpPct)
                    entries.Add(new HighlightEntry(seed, name, "comeback", r.Winner, winner.MinHpPct));
                if (loser.Taunted)
                    entries.Add(new HighlightEntry(seed, name, "taunt_reversal", r.Winner, winner.MinHpPct));
            }
        }

        var report = new HighlightsReport(MatchSerializer.SchemaVersion, games, entries);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글 매치업명 가독성 (사람이 보는 목록)
        }));

        Console.WriteLine($"명경기 {entries.Count}건 수집 → {outPath}  (매치업 {Matchups.Length} × {games}경기)");
        foreach (var g in entries.GroupBy(e => (e.Matchup, e.Kind)).OrderBy(g => g.Key.Matchup))
            Console.WriteLine($"   {g.Key.Matchup} / {g.Key.Kind}: {g.Count()}건");
    }
}

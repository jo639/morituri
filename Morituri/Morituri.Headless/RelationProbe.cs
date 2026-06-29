using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 관계(T11) 메타 데모 — 10성격 라운드로빈 N경기를 RelationLedger에 누적해 <b>창발한 관계 그래프</b>를 보여준다.
/// "관계는 경기 외적(메타) 영향"의 실증: 누가 누구의 원수/천적/라이벌이 되었나, 복수전 후보는 누구인가.
/// (영속 저장·자동 매치메이킹은 Phase 3 — 여기선 인메모리 그래프 + 메타 쿼리.)
/// </summary>
public static class RelationProbe
{
    public static void Run(int seedsPerPair)
    {
        var roster = PersonalityTable.All;                 // id = PER_* (성격이 곧 선수)
        string PersOf(string id) => id;                    // roster id가 곧 성격 id
        static string Short(string id) => id.Replace("PER_", "");

        var ledger = new RelationLedger();
        int matches = 0;
        for (int i = 0; i < roster.Length; i++)
            for (int j = i + 1; j < roster.Length; j++)
            {
                var a = new FighterDef(roster[i].Id, FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", roster[i].Id);
                var b = new FighterDef(roster[j].Id, FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", roster[j].Id);
                for (ulong s = 1; s <= (ulong)seedsPerPair; s++)
                {
                    var r = new MatchSim().Run(a, b, s);
                    ledger.RecordMatch(roster[i].Id, roster[j].Id, r.Winner, r.Reason == "KO", r.StatsA.MinHpPct, r.StatsB.MinHpPct);
                    matches++;
                }
            }

        var rels = ledger.AllRelations(PersOf).ToList();
        Console.WriteLine($"=== 관계(T11) 메타 데모 — {roster.Length}성격 라운드로빈 × {seedsPerPair}시드 = {matches}경기 ===");
        Console.WriteLine("  여러 경기 누적으로 형성된 선수 간 관계 그래프 (경기 외적 메타 — Phase 3 매치메이킹/명성 입력).\n");

        Console.WriteLine("  [관계 타입 분포] (방향성 관계 수)");
        foreach (var g in rels.GroupBy(x => x.Type).OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {RelationTable.Get(g.Key).Name,-6} {g.Count()}");
        Console.WriteLine();

        Console.WriteLine("  [원수·천적] (강한 적대 — A가 B를 두려워/원망)");
        foreach (var x in rels.Where(x => x.Type is RelationType.Nemesis or RelationType.Fear)
                               .OrderBy(x => x.State.Affinity).Take(8))
            Console.WriteLine($"    {Short(x.Self),-12} → {Short(x.Opp),-12} {RelationTable.Get(x.Type).Name}  (affinity {x.State.Affinity:F0}, {x.State.Losses}패)");
        Console.WriteLine();

        Console.WriteLine("  [최대 라이벌리] (양방향 관심도 = 매치메이킹 가중)");
        var pairs = new HashSet<(string, string)>();
        var ranked = new List<(string A, string B, float W)>();
        for (int i = 0; i < roster.Length; i++)
            for (int j = i + 1; j < roster.Length; j++)
            {
                float w = ledger.RivalryWeight(roster[i].Id, roster[j].Id, PersOf);
                if (w > 0) ranked.Add((roster[i].Id, roster[j].Id, w));
            }
        foreach (var x in ranked.OrderByDescending(x => x.W).Take(6))
            Console.WriteLine($"    {Short(x.A)} ↔ {Short(x.B)}  관심도 {x.W:F1}");
        Console.WriteLine();

        var revenge = ledger.RevengeCandidates(PersOf).OrderBy(x => x.State.Affinity).ToList();
        Console.WriteLine($"  [복수전 후보] {revenge.Count}건 (원수/천적 + 아직 못 갚음 = 패 > 승)");
        foreach (var x in revenge.Take(8))
            Console.WriteLine($"    {Short(x.Self),-12} vs {Short(x.Opp),-12} (affinity {x.State.Affinity:F0}, {x.State.Wins}승 {x.State.Losses}패)");
    }
}

using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// Phase 3 P3-A: 시즌 엔진 (ERD[2] §7). 영속 로스터 + 라운드로빈 자동 스케줄로,
/// 우리가 만든 감정·관계가 비로소 여러 경기에 걸쳐 작동한다:
///  - 관계(T11): RelationLedger에 누적 → 이후 경기에 트리거 게이트로 주입(영속).
///  - 감정(T10): 경기 결과로 생성 → 다음 1경기에만 실려 소멸(누적 없음, [2]§6-1).
///  - 순위·서사(챔피언·라이벌·복수극·이변)가 창발.
/// Headless 시작(스키마 검증 후 Meta 어셈블리로 분리 — ERD §4). 결정론(시드 고정).
/// </summary>
public static class Season
{
    /// <summary>시즌 로스터의 한 선수(영속). 경기간 상태(감정 대기열·전적)를 들고 다닌다.</summary>
    private sealed class Gladiator
    {
        public required string Id, Name, WeaponId, TacticsId, PersonalityId;
        public string[]? TraitIds;
        public readonly List<string> PendingEmotions = new();  // 다음 1경기 대기열(적용 후 소멸)
        public int W, L, D, KoW, Streak;
        public int Points => W * 3 + D;                        // 승 3 / 무 1 (초안, [2]§6-3 추후)
        public PersonalityDef Pers => PersonalityTable.Get(PersonalityId);
    }

    private static List<Gladiator> BuildCast() => new()
    {
        new() { Id = "GLA_MAXIMUS", Name = "막시무스",   WeaponId = "WPN_SWORD",      TacticsId = "TAC_PRESSURE", PersonalityId = "PER_BOLD" },
        new() { Id = "GLA_SPARTA",  Name = "스파르타쿠스", WeaponId = "WPN_AXE",        TacticsId = "TAC_BRAWLER",  PersonalityId = "PER_RECKLESS" },
        new() { Id = "GLA_CRIXUS",  Name = "크릭수스",   WeaponId = "WPN_DUALBLADES", TacticsId = "TAC_BRAWLER",  PersonalityId = "PER_CRUEL" },
        new() { Id = "GLA_GANNICUS",Name = "가니쿠스",   WeaponId = "WPN_SPEAR",      TacticsId = "TAC_COUNTER",  PersonalityId = "PER_CALM" },
        new() { Id = "GLA_OENOMAUS",Name = "오이노마우스", WeaponId = "WPN_HAMMER",     TacticsId = "TAC_PRESSURE", PersonalityId = "PER_WARY" },
        new() { Id = "GLA_AGRON",   Name = "아그론",     WeaponId = "WPN_GREATSWORD", TacticsId = "TAC_PRESSURE", PersonalityId = "PER_ARROGANT" },
        new() { Id = "GLA_BARCA",   Name = "바르카",     WeaponId = "WPN_WHIP",       TacticsId = "TAC_ZONER",    PersonalityId = "PER_OPPORTUNIST" },
        new() { Id = "GLA_NAEVIA",  Name = "나이비아",   WeaponId = "WPN_SHIELD",     TacticsId = "TAC_DEFENDER", PersonalityId = "PER_HONORABLE" },
    };

    public static void Run(int rounds, ulong seasonSeed)
    {
        var cast = BuildCast();
        var byId = cast.ToDictionary(g => g.Id);
        var ledger = new RelationLedger();
        string PersOf(string id) => byId[id].PersonalityId;
        var emoRng = new SimRandom(seasonSeed ^ 0x5EA5_04ED);   // 감정 발생 롤 전용 스트림(결정론)
        var story = new List<(int Round, string Kind, string Text)>();
        int matchIdx = 0, emoGen = 0;

        for (int r = 1; r <= rounds; r++)
            for (int i = 0; i < cast.Count; i++)
                for (int j = i + 1; j < cast.Count; j++)
                {
                    var A = cast[i]; var B = cast[j];

                    // 관계(영속): 지금까지 누적된 관계를 이 경기에 주입(트리거 게이트).
                    var relA = ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
                    var relB = ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
                    var defA = ToDef(A, relA, Intensity(ledger, A.Id, B.Id));
                    var defB = ToDef(B, relB, Intensity(ledger, B.Id, A.Id));
                    A.PendingEmotions.Clear(); B.PendingEmotions.Clear();   // 감정 소비(빌드에 반영됨) → 소멸

                    ulong seed = seasonSeed + (ulong)(++matchIdx);
                    var res = new MatchSim().Run(defA, defB, seed);
                    bool ko = res.Reason == "KO";

                    // 서사 감지 (기록 전 상태로 판정)
                    if (res.Winner >= 0)
                    {
                        var (win, lose) = res.Winner == 0 ? (A, B) : (B, A);
                        var winStats = res.Winner == 0 ? res.StatsA : res.StatsB;
                        var prior = ledger.Get(win.Id, lose.Id);
                        var priorRel = prior.Classify(win.PersonalityId);
                        if (prior.Losses > prior.Wins && priorRel is RelationType.Nemesis or RelationType.Fear)
                            story.Add((r, "revenge", $"R{r} ⚔ 복수! {win.Name}이(가) 숙적 {lose.Name}에게 설욕 (그간 {prior.Wins}승 {prior.Losses}패)"));
                        else if (win.Points < lose.Points)
                            story.Add((r, "upset", $"R{r} ★ 이변! 하위 {win.Name}이(가) 상위 {lose.Name}을(를) 격파"));
                        if (winStats.MinHpPct <= 0.10f)
                            story.Add((r, "comeback", $"R{r} 🔥 대역전! {win.Name} 사선(HP{winStats.MinHpPct*100:F0}%)에서 {lose.Name} 제압"));
                    }

                    // 순위 갱신
                    Record(A, B, res);
                    // 관계 누적(영속)
                    ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
                    // 감정 생성 → 다음 1경기 대기열
                    var eA = EmotionGen.Roll(emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers);
                    var eB = EmotionGen.Roll(emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers);
                    if (eA != null) { A.PendingEmotions.Add(eA); emoGen++; }
                    if (eB != null) { B.PendingEmotions.Add(eB); emoGen++; }
                }

        PrintReport(cast, ledger, PersOf, story, rounds, matchIdx, emoGen);
    }

    private static FighterDef ToDef(Gladiator g, RelationType? rel, float intensity) =>
        new(g.Name, FighterStats.Baseline, g.WeaponId, g.TacticsId, g.PersonalityId,
            g.TraitIds, g.PendingEmotions.Count > 0 ? g.PendingEmotions.ToArray() : null, rel, intensity);

    private static float Intensity(RelationLedger l, string self, string opp)
        => Math.Clamp(MathF.Abs(l.Get(self, opp).Affinity) / 100f, 0f, 1f);

    private static void Record(Gladiator a, Gladiator b, MatchResult r)
    {
        if (r.Winner == 0) { a.W++; b.L++; a.Streak = a.Streak >= 0 ? a.Streak + 1 : 1; b.Streak = b.Streak <= 0 ? b.Streak - 1 : -1; if (r.Reason == "KO") a.KoW++; }
        else if (r.Winner == 1) { b.W++; a.L++; b.Streak = b.Streak >= 0 ? b.Streak + 1 : 1; a.Streak = a.Streak <= 0 ? a.Streak - 1 : -1; if (r.Reason == "KO") b.KoW++; }
        else { a.D++; b.D++; a.Streak = 0; b.Streak = 0; }
    }

    private static void PrintReport(List<Gladiator> cast, RelationLedger ledger, Func<string, string> persOf,
                                    List<(int Round, string Kind, string Text)> story, int rounds, int matches, int emoGen)
    {
        static string Streak(int s) => s > 0 ? $"{s}연승" : s < 0 ? $"{-s}연패" : "-";
        Console.WriteLine($"=== MORITURI 시즌 (P3-A) — {cast.Count}인 라운드로빈 ×{rounds}회 = {matches}경기 ===");
        Console.WriteLine("  감정(다음 1경기)·관계(누적)가 시즌에 걸쳐 작동한 결과.\n");

        Console.WriteLine("  [최종 순위]");
        Console.WriteLine($"    {"선수",-12}{"승점",5}{"전적(승-패-무)",16}{"KO승",6}{"현재",8}");
        var ranked = cast.OrderByDescending(g => g.Points).ThenByDescending(g => g.W).ToList();
        for (int k = 0; k < ranked.Count; k++)
        {
            var g = ranked[k];
            string crown = k == 0 ? " 👑" : "";
            Console.WriteLine($"    {g.Name,-12}{g.Points,5}{$"{g.W}-{g.L}-{g.D}",14}{g.KoW,6}{Streak(g.Streak),8}{crown}");
        }
        Console.WriteLine($"\n  🏆 시즌 챔피언: {ranked[0].Name} ({ranked[0].W}승 {ranked[0].L}패, 승점 {ranked[0].Points})\n");

        var rels = ledger.AllRelations(persOf).ToList();
        Console.WriteLine("  [숙적 관계] (강한 적대 — 관계 그래프 창발)");
        foreach (var x in rels.Where(x => x.Type is RelationType.Nemesis or RelationType.Fear)
                               .OrderBy(x => x.State.Affinity).Take(6))
            Console.WriteLine($"    {Name(cast, x.Self),-12} → {Name(cast, x.Opp),-12} {RelationTable.Get(x.Type).Name} ({x.State.Wins}승 {x.State.Losses}패)");

        Console.WriteLine("\n  [최대 라이벌리] (매치메이킹 관심도)");
        var pairSeen = new HashSet<string>();
        var wr = new List<(string A, string B, float W)>();
        for (int i = 0; i < cast.Count; i++)
            for (int j = i + 1; j < cast.Count; j++)
            {
                float w = ledger.RivalryWeight(cast[i].Id, cast[j].Id, persOf);
                if (w > 0) wr.Add((cast[i].Name, cast[j].Name, w));
            }
        foreach (var x in wr.OrderByDescending(x => x.W).Take(4))
            Console.WriteLine($"    {x.A} ↔ {x.B}  (관심도 {x.W:F1})");

        int rev = story.Count(s => s.Kind == "revenge"), ups = story.Count(s => s.Kind == "upset"), cmb = story.Count(s => s.Kind == "comeback");
        Console.WriteLine($"\n  [시즌 서사] 복수 {rev} · 이변 {ups} · 대역전 {cmb} (감정·관계가 만든 이야기)");
        Console.WriteLine("   ★ 복수극 (관계 누적이 후속 라운드에 결실 — Phase 3의 핵심):");
        foreach (var s in story.Where(s => s.Kind == "revenge").Take(8)) Console.WriteLine($"    {s.Text}");
        if (rev == 0) Console.WriteLine("    (이번 시즌엔 없음 — 라운드·시드에 따라 창발)");
        Console.WriteLine("   그 외 명장면 (후반 라운드 표본):");
        foreach (var s in story.Where(s => s.Kind != "revenge" && s.Round >= Math.Max(1, rounds - 1)).Take(6)) Console.WriteLine($"    {s.Text}");

        Console.WriteLine($"\n  (감정 발생 {emoGen}회 / 총 {matches * 2}선수-경기 = {100.0 * emoGen / (matches * 2):F1}%)");
    }

    private static string Name(List<Gladiator> cast, string id) => cast.First(g => g.Id == id).Name;
}

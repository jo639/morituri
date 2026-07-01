using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// Phase 3 P3-A/B/C: 시즌 엔진 + 명성(Fame) + 영속(world.json). ERD[2].
///  - P3-A: 라운드로빈 자동 스케줄, 감정(다음 1경기)·관계(누적)·순위·서사.
///  - P3-B: 명성/인기 — 승·KO·역전·이변·연승·스펙터클로 누적 → 이벤트 매치 가중(rivalry×인기).
///  - P3-C: world.json 저장/로드 — season을 반복 실행할수록 세계가 누적(다시즌 커리어·깊어지는 관계·스타 탄생).
/// Sim 무변경(Meta가 MatchResult 소비). 결정론(시드 고정). constantsVer 박제([2]§6-7).
/// </summary>
public static class Season
{
    private const int SchemaVer = 1;
    private const int ConstantsVer = 1;   // BalanceConstants 버전 표시(수동 — 상수 바뀌면 올림). 과거 시즌 재현 게이트.
    private const string WorldPath = "world.json";

    private sealed class Gladiator
    {
        public required string Id, Name, WeaponId, TacticsId, PersonalityId;
        // 영속 (world.json)
        public int CW, CL, CD, CKoW;
        public float Fame, Popularity;
        // 이번 시즌 (휘발)
        public int W, L, D, Streak;
        public readonly List<string> PendingEmotions = new();
        public int SeasonPoints => W * 3 + D;
        public int CareerPoints => CW * 3 + CD;
        public PersonalityDef Pers => PersonalityTable.Get(PersonalityId);
    }

    private sealed record GladRec(string Id, int CW, int CL, int CD, int CKoW, float Fame, float Popularity);
    private sealed record WorldState(int SchemaVer, int ConstantsVer, int SeasonsPlayed,
                                     List<GladRec> Gladiators, List<RelationLedger.Entry> Relations);

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

    public static void Run(int rounds, ulong seasonSeed, bool fresh = false)
    {
        var cast = BuildCast();
        var byId = cast.ToDictionary(g => g.Id);
        var ledger = new RelationLedger();
        int seasonsPlayed = fresh ? 0 : LoadWorld(cast, byId, ledger);   // P3-C: 영속 로드
        foreach (var g in cast) g.Popularity *= 0.6f;                    // 시즌 사이 인기 감쇠(시간 경과)

        string PersOf(string id) => byId[id].PersonalityId;
        var emoRng = new SimRandom(seasonSeed ^ 0x5EA5_04ED);
        var story = new List<(int Round, string Kind, string Text)>();
        int matchIdx = 0, emoGen = 0;

        for (int r = 1; r <= rounds; r++)
            for (int i = 0; i < cast.Count; i++)
                for (int j = i + 1; j < cast.Count; j++)
                {
                    var A = cast[i]; var B = cast[j];
                    var relA = ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
                    var relB = ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
                    var defA = ToDef(A, relA, Intensity(ledger, A.Id, B.Id));
                    var defB = ToDef(B, relB, Intensity(ledger, B.Id, A.Id));
                    A.PendingEmotions.Clear(); B.PendingEmotions.Clear();

                    ulong seed = seasonSeed + (ulong)(++matchIdx);
                    var res = new MatchSim().Run(defA, defB, seed);
                    bool ko = res.Reason == "KO";

                    if (res.Winner >= 0)
                    {
                        var (win, lose) = res.Winner == 0 ? (A, B) : (B, A);
                        var (winStats, loseStats) = res.Winner == 0 ? (res.StatsA, res.StatsB) : (res.StatsB, res.StatsA);
                        bool upset = win.CareerPoints < lose.CareerPoints;
                        bool comeback = winStats.MinHpPct <= 0.10f;

                        var prior = ledger.Get(win.Id, lose.Id);
                        var priorRel = prior.Classify(win.PersonalityId);
                        if (prior.Losses > prior.Wins && priorRel is RelationType.Nemesis or RelationType.Fear)
                            story.Add((r, "revenge", $"R{r} ⚔ 복수! {win.Name}이(가) 숙적 {lose.Name}에게 설욕 (그간 {prior.Wins}승 {prior.Losses}패)"));
                        else if (upset)
                            story.Add((r, "upset", $"R{r} ★ 이변! {win.Name}이(가) 상위 {lose.Name}을(를) 격파"));
                        if (comeback)
                            story.Add((r, "comeback", $"R{r} 🔥 대역전! {win.Name} 사선(HP{winStats.MinHpPct * 100:F0}%)에서 {lose.Name} 제압"));

                        // P3-B 명성: 승·KO·역전·이변·스펙터클 → Fame·인기 누적
                        float wFame = 3f + (ko ? 2f : 0f) + (comeback ? 5f : 0f) + (upset ? 4f : 0f)
                                    + winStats.CleanHits * 0.1f + winStats.Knockdowns;
                        float lFame = 0.5f - (loseStats.Taunted ? 2f : 0f);   // 도발하고 지면(taunt_reversal) 망신
                        Award(win, wFame); Award(lose, lFame);
                    }

                    Record(A, B, res);
                    ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
                    var eA = EmotionGen.Roll(emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers);
                    var eB = EmotionGen.Roll(emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers);
                    if (eA != null) { A.PendingEmotions.Add(eA); emoGen++; }
                    if (eB != null) { B.PendingEmotions.Add(eB); emoGen++; }
                }

        int seasonNo = seasonsPlayed + 1;
        SaveWorld(cast, ledger, seasonNo);          // P3-C: 영속 저장
        PrintReport(cast, ledger, PersOf, story, rounds, matchIdx, emoGen, seasonNo);
    }

    private static void Award(Gladiator g, float delta) { g.Fame = MathF.Max(0f, g.Fame + delta); g.Popularity = MathF.Max(0f, g.Popularity + delta); }

    private static FighterDef ToDef(Gladiator g, RelationType? rel, float intensity) =>
        new(g.Name, FighterStats.Baseline, g.WeaponId, g.TacticsId, g.PersonalityId,
            null, g.PendingEmotions.Count > 0 ? g.PendingEmotions.ToArray() : null, rel, intensity);

    private static float Intensity(RelationLedger l, string self, string opp)
        => Math.Clamp(MathF.Abs(l.Get(self, opp).Affinity) / 100f, 0f, 1f);

    private static void Record(Gladiator a, Gladiator b, MatchResult r)
    {
        if (r.Winner == 0) { a.W++; b.L++; a.CW++; b.CL++; a.Streak = a.Streak >= 0 ? a.Streak + 1 : 1; b.Streak = b.Streak <= 0 ? b.Streak - 1 : -1; if (r.Reason == "KO") a.CKoW++; }
        else if (r.Winner == 1) { b.W++; a.L++; b.CW++; a.CL++; b.Streak = b.Streak >= 0 ? b.Streak + 1 : 1; a.Streak = a.Streak <= 0 ? a.Streak - 1 : -1; if (r.Reason == "KO") b.CKoW++; }
        else { a.D++; b.D++; a.CD++; b.CD++; a.Streak = 0; b.Streak = 0; }
    }

    // ── P3-C 영속 ──
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static int LoadWorld(List<Gladiator> cast, Dictionary<string, Gladiator> byId, RelationLedger ledger)
    {
        if (!File.Exists(WorldPath)) return 0;
        var w = JsonSerializer.Deserialize<WorldState>(File.ReadAllText(WorldPath), JsonOpts);
        if (w is null) return 0;
        if (w.ConstantsVer != ConstantsVer)
            Console.WriteLine($"  ⚠ world.json 상수버전 {w.ConstantsVer} ≠ 현재 {ConstantsVer} — 과거 시즌은 다른 밸런스로 치러짐(누적은 유지).");
        foreach (var rec in w.Gladiators)
            if (byId.TryGetValue(rec.Id, out var g))
            { g.CW = rec.CW; g.CL = rec.CL; g.CD = rec.CD; g.CKoW = rec.CKoW; g.Fame = rec.Fame; g.Popularity = rec.Popularity; }
        ledger.Load(w.Relations);
        return w.SeasonsPlayed;
    }

    private static void SaveWorld(List<Gladiator> cast, RelationLedger ledger, int seasonsPlayed)
    {
        var state = new WorldState(SchemaVer, ConstantsVer, seasonsPlayed,
            cast.Select(g => new GladRec(g.Id, g.CW, g.CL, g.CD, g.CKoW, g.Fame, g.Popularity)).ToList(),
            ledger.Snapshot().ToList());
        File.WriteAllText(WorldPath, JsonSerializer.Serialize(state, JsonOpts));
    }

    private static void PrintReport(List<Gladiator> cast, RelationLedger ledger, Func<string, string> persOf,
                                    List<(int Round, string Kind, string Text)> story, int rounds, int matches, int emoGen, int seasonNo)
    {
        static string Streak(int s) => s > 0 ? $"{s}연승" : s < 0 ? $"{-s}연패" : "-";
        Console.WriteLine($"=== MORITURI 시즌 {seasonNo} (P3-A/B/C) — {cast.Count}인 라운드로빈 ×{rounds}회 = {matches}경기 ===");
        Console.WriteLine($"  누적 {seasonNo}시즌 (world.json 영속). 감정(다음 1경기)·관계(누적)·명성이 시즌에 걸쳐 작동.\n");

        Console.WriteLine("  [이번 시즌 순위]");
        Console.WriteLine($"    {"선수",-12}{"승점",5}{"전적",12}{"현재",8}");
        var season = cast.OrderByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W).ToList();
        for (int k = 0; k < season.Count; k++)
        {
            var g = season[k];
            Console.WriteLine($"    {g.Name,-12}{g.SeasonPoints,5}{$"{g.W}-{g.L}-{g.D}",12}{Streak(g.Streak),8}{(k == 0 ? " 👑" : "")}");
        }
        Console.WriteLine($"\n  🏆 시즌 {seasonNo} 챔피언: {season[0].Name}\n");

        Console.WriteLine("  [통산 명성 리더보드] (P3-B — 여러 시즌 누적)");
        Console.WriteLine($"    {"선수",-12}{"명성",7}{"인기",7}{"통산전적",12}");
        foreach (var g in cast.OrderByDescending(g => g.Fame).Take(5))
            Console.WriteLine($"    {g.Name,-12}{g.Fame,7:F0}{g.Popularity,7:F0}{$"{g.CW}-{g.CL}-{g.CD}",12}");

        var rels = ledger.AllRelations(persOf).ToList();
        Console.WriteLine("\n  [숙적 관계] (누적 관계 그래프)");
        foreach (var x in rels.Where(x => x.Type is RelationType.Nemesis or RelationType.Fear)
                               .OrderBy(x => x.State.Affinity).Take(5))
            Console.WriteLine($"    {Name(cast, x.Self),-12} → {Name(cast, x.Opp),-12} {RelationTable.Get(x.Type).Name} (통산 {x.State.Wins}승 {x.State.Losses}패)");

        Console.WriteLine("\n  [다음 시즌 이벤트 매치 후보] (P3-B — 라이벌리 × 인기)");
        var cards = new List<(string A, string B, float Score)>();
        for (int i = 0; i < cast.Count; i++)
            for (int j = i + 1; j < cast.Count; j++)
            {
                float riv = ledger.RivalryWeight(cast[i].Id, cast[j].Id, persOf);
                if (riv <= 0) continue;
                float draw = riv * (1f + (cast[i].Popularity + cast[j].Popularity) / 40f);   // 관계 열기 × 스타파워
                cards.Add((cast[i].Name, cast[j].Name, draw));
            }
        foreach (var c in cards.OrderByDescending(c => c.Score).Take(4))
            Console.WriteLine($"    {c.A} vs {c.B}  (흥행지수 {c.Score:F1})");

        int rev = story.Count(s => s.Kind == "revenge"), ups = story.Count(s => s.Kind == "upset"), cmb = story.Count(s => s.Kind == "comeback");
        Console.WriteLine($"\n  [시즌 서사] 복수 {rev} · 이변 {ups} · 대역전 {cmb}");
        foreach (var s in story.Where(s => s.Kind == "revenge").Take(6)) Console.WriteLine($"    {s.Text}");
        foreach (var s in story.Where(s => s.Kind != "revenge" && s.Round >= Math.Max(1, rounds - 1)).Take(4)) Console.WriteLine($"    {s.Text}");

        Console.WriteLine($"\n  (감정 발생 {100.0 * emoGen / (matches * 2):F1}% · world.json 저장됨 — season 재실행 시 누적)");
    }

    private static string Name(List<Gladiator> cast, string id) => cast.First(g => g.Id == id).Name;
}

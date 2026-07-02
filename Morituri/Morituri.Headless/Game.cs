using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// Phase 3 시즌 엔진의 상태 기계 버전 (배포[12] W1 — 게임 클라이언트의 심장).
/// 구 Season.Run(일괄 CLI)을 흡수: 같은 로직(감정 다음-1경기·관계 누적·명성·자동 이벤트 매치·world.json 영속)을
/// "다음 경기 1판" 단위로 진행할 수 있다 → 클라이언트의 [다음 경기 ▶] 버튼이 이걸 호출.
///  - PlayNext(): 예정된 다음 경기 실행 → 결과 반영 → season.json(+interactive면 viewer.json) 갱신 → 요약 반환.
///  - 정규 라운드로빈 소진 → 흥행 이벤트 빅매치 자동 편성 → 마지막 경기 후 시즌 종료(world.json 저장).
///  - 시즌 종료 상태에서 PlayNext() → 다음 시즌 자동 개막 후 첫 경기.
/// Sim 무변경(Meta 소비층). 결정론(시즌 시드 + 경기 인덱스).
/// </summary>
public sealed class Game
{
    private const int SchemaVer = 1;
    private const int ConstantsVer = 1;   // BalanceConstants 버전 표시(수동). 과거 시즌 재현 게이트 ([2]§6-7).
    private const string WorldPath = "world.json";

    private sealed class Gladiator
    {
        public required string Id, Name, WeaponId, TacticsId, PersonalityId;
        public int CW, CL, CD, CKoW;              // 영속 커리어
        public float Fame, Popularity;            // 영속 명성/인기
        public int W, L, D, Streak;               // 이번 시즌(휘발)
        public readonly List<string> PendingEmotions = new();
        public int SeasonPoints => W * 3 + D;
        public int CareerPoints => CW * 3 + CD;
        public PersonalityDef Pers => PersonalityTable.Get(PersonalityId);
    }

    private sealed record GladRec(string Id, int CW, int CL, int CD, int CKoW, float Fame, float Popularity);
    private sealed record WorldState(int SchemaVer, int ConstantsVer, int SeasonsPlayed,
                                     List<GladRec> Gladiators, List<RelationLedger.Entry> Relations);

    // season.json = league.html/index.html(게임 셸) 공용 입력.
    private sealed record EventDoc(string A, string B, float Score, string Winner, bool Ko);
    private sealed record FighterDoc(string Id, string Name, string Weapon, string Tactic, string Personality,
        int W, int L, int D, int Points, int Streak, int CW, int CL, int CD, float Fame, float Popularity);
    private sealed record RelDoc(string Self, string Opp, string Type, float Affinity, int Wins, int Losses);
    private sealed record StoryDoc(int Round, string Kind, string Text);
    private sealed record SeasonDoc(int SchemaVer, int SeasonNo, int Rounds, int Matches, int TotalMatches, bool Completed,
        string? NextA, string? NextB, bool NextIsEvent, string Champion,
        List<FighterDoc> Fighters, List<RelDoc> Relations, List<EventDoc> Events, List<StoryDoc> Story);

    /// <summary>PlayNext 요약 (클라이언트 /api/next 응답).</summary>
    public sealed record MatchSummary(int SeasonNo, int Round, bool IsEvent, string A, string B,
        string Winner, string Reason, bool SeasonCompleted, bool NewSeasonStarted);

    private sealed record Sched(int Round, string AId, string BId, bool IsEvent, float Score);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<Gladiator> _cast;
    private readonly Dictionary<string, Gladiator> _byId;
    private readonly RelationLedger _ledger = new();
    private readonly int _rounds;
    private readonly bool _interactive;   // true=클라이언트(매 경기 viewer.json/season.json 갱신) / false=CLI(시즌 끝에만)
    private readonly ulong? _seedOverride;

    private int _seasonNo;                // 진행 중(또는 방금 끝난) 시즌 번호
    private ulong _seasonSeed;
    private SimRandom _emoRng = null!;
    private int _matchIdx, _emoGen;
    private readonly List<(int Round, string Kind, string Text)> _story = new();
    private readonly List<EventDoc> _eventDocs = new();
    private readonly List<Sched> _schedule = new();
    private int _cursor;
    private bool _eventsAppended;

    public bool SeasonComplete { get; private set; }

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

    public Game(int roundsPerSeason, ulong? seasonSeed = null, bool fresh = false, bool interactive = true)
    {
        _rounds = roundsPerSeason;
        _interactive = interactive;
        _seedOverride = seasonSeed;
        _cast = BuildCast();
        _byId = _cast.ToDictionary(g => g.Id);
        int played = fresh ? 0 : LoadWorld();
        StartSeason(played + 1);
    }

    private string PersOf(string id) => _byId[id].PersonalityId;

    // ── 시즌 개막/종료 ──

    private void StartSeason(int seasonNo)
    {
        _seasonNo = seasonNo;
        _seasonSeed = _seedOverride ?? (ulong)seasonNo;   // 시즌 번호 = 기본 시드 (CLI 관례: season N → seed N)
        _emoRng = new SimRandom(_seasonSeed ^ 0x5EA5_04ED);
        _matchIdx = 0; _emoGen = 0; _cursor = 0; _eventsAppended = false;
        _story.Clear(); _eventDocs.Clear(); _schedule.Clear();
        SeasonComplete = false;
        foreach (var g in _cast) { g.W = g.L = g.D = g.Streak = 0; g.PendingEmotions.Clear(); g.Popularity *= 0.6f; }

        for (int r = 1; r <= _rounds; r++)               // 정규: 라운드로빈 자동 스케줄
            for (int i = 0; i < _cast.Count; i++)
                for (int j = i + 1; j < _cast.Count; j++)
                    _schedule.Add(new Sched(r, _cast[i].Id, _cast[j].Id, false, 0f));

        if (_interactive) WriteSeasonJson();
    }

    private void FinalizeSeason()
    {
        SeasonComplete = true;
        SaveWorld(_seasonNo);
        var champ = Standings()[0];
        _story.Add((_rounds + 1, "season", $"🏆 시즌 {_seasonNo} 종료 — 챔피언 {champ.Name} ({champ.W}승 {champ.L}패)"));
        if (_interactive) WriteSeasonJson();
    }

    private List<Gladiator> Standings() =>
        _cast.OrderByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W).ToList();

    // ── 진행 ──

    /// <summary>다음 경기 1판. 시즌이 끝난 상태면 다음 시즌을 개막하고 첫 경기를 치른다.</summary>
    public MatchSummary PlayNext()
    {
        bool newSeason = SeasonComplete;
        if (newSeason) StartSeason(_seasonNo + 1);

        // 정규 소진 → 이벤트 빅매치 자동 편성(흥행지수 = 라이벌리 × 인기)
        if (_cursor >= _schedule.Count && !_eventsAppended)
        {
            foreach (var (a, b, score) in TopEventCards(_cast.Count / 2))
                _schedule.Add(new Sched(_rounds + 1, a, b, true, score));
            _eventsAppended = true;
        }

        var s = _schedule[_cursor++];
        var A = _byId[s.AId]; var B = _byId[s.BId];
        var res = Play(A, B, s.Round, s.IsEvent);
        if (s.IsEvent)
            _eventDocs.Add(new EventDoc(A.Name, B.Name, s.Score,
                res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason == "KO"));

        bool last = _cursor >= _schedule.Count && _eventsAppended;
        if (last) FinalizeSeason();
        else if (_interactive) WriteSeasonJson();

        return new MatchSummary(_seasonNo, s.Round, s.IsEvent, A.Name, B.Name,
            res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason, last, newSeason);
    }

    private MatchResult Play(Gladiator A, Gladiator B, int round, bool isEvent)
    {
        // 관계(영속): 누적 관계를 이 경기에 트리거 게이트로 주입.
        var relA = _ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
        var relB = _ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
        var defA = ToDef(A, relA, Intensity(A.Id, B.Id));
        var defB = ToDef(B, relB, Intensity(B.Id, A.Id));
        A.PendingEmotions.Clear(); B.PendingEmotions.Clear();   // 감정 소비(빌드 반영됨) → 소멸 ([2]§6-1)

        ulong seed = _seasonSeed + (ulong)(++_matchIdx);
        MatchResult res;
        if (_interactive)
        {
            // 관전용: 같은 실행에서 프레임·이벤트 수집 → viewer.json (재실행 불필요, 결정론)
            var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
            res = new MatchSim().Run(defA, defB, seed, events, frames);
            ViewerExport.WriteDoc(defA, defB, seed, res, frames, events, "viewer.json");
        }
        else res = new MatchSim().Run(defA, defB, seed);

        bool ko = res.Reason == "KO";
        if (res.Winner >= 0)
        {
            var (win, lose) = res.Winner == 0 ? (A, B) : (B, A);
            var (winStats, loseStats) = res.Winner == 0 ? (res.StatsA, res.StatsB) : (res.StatsB, res.StatsA);
            bool upset = win.CareerPoints < lose.CareerPoints;
            bool comeback = winStats.MinHpPct <= 0.10f;

            var prior = _ledger.Get(win.Id, lose.Id);
            var priorRel = prior.Classify(win.PersonalityId);
            if (prior.Losses > prior.Wins && priorRel is RelationType.Nemesis or RelationType.Fear)
                _story.Add((round, "revenge", $"R{round} ⚔ 복수! {win.Name}이(가) 숙적 {lose.Name}에게 설욕 (그간 {prior.Wins}승 {prior.Losses}패)"));
            else if (upset)
                _story.Add((round, "upset", $"R{round} ★ 이변! {win.Name}이(가) 상위 {lose.Name}을(를) 격파"));
            if (comeback)
                _story.Add((round, "comeback", $"R{round} 🔥 대역전! {win.Name} 사선(HP{winStats.MinHpPct * 100:F0}%)에서 {lose.Name} 제압"));

            // 명성(P3-B): 승·KO·역전·이변·스펙터클 → Fame·인기. 도발하고 지면 망신.
            float wFame = 3f + (ko ? 2f : 0f) + (comeback ? 5f : 0f) + (upset ? 4f : 0f)
                        + winStats.CleanHits * 0.1f + winStats.Knockdowns;
            Award(win, wFame); Award(lose, 0.5f - (loseStats.Taunted ? 2f : 0f));
        }

        Record(A, B, res, standing: !isEvent);
        _ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
        if (EmotionGen.Roll(_emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers) is { } eA) { A.PendingEmotions.Add(eA); _emoGen++; }
        if (EmotionGen.Roll(_emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers) is { } eB) { B.PendingEmotions.Add(eB); _emoGen++; }
        return res;
    }

    private List<(string A, string B, float Score)> TopEventCards(int count)
    {
        var cards = new List<(string A, string B, float Score)>();
        for (int i = 0; i < _cast.Count; i++)
            for (int j = i + 1; j < _cast.Count; j++)
            {
                float riv = _ledger.RivalryWeight(_cast[i].Id, _cast[j].Id, PersOf);
                if (riv <= 0) continue;
                float draw = riv * (1f + (_cast[i].Popularity + _cast[j].Popularity) / 40f);
                cards.Add((_cast[i].Id, _cast[j].Id, draw));
            }
        return cards.OrderByDescending(c => c.Score).Take(count).ToList();
    }

    private static void Award(Gladiator g, float delta) { g.Fame = MathF.Max(0f, g.Fame + delta); g.Popularity = MathF.Max(0f, g.Popularity + delta); }

    private static FighterDef ToDef(Gladiator g, RelationType? rel, float intensity) =>
        new(g.Name, FighterStats.Baseline, g.WeaponId, g.TacticsId, g.PersonalityId,
            null, g.PendingEmotions.Count > 0 ? g.PendingEmotions.ToArray() : null, rel, intensity);

    private float Intensity(string self, string opp)
        => Math.Clamp(MathF.Abs(_ledger.Get(self, opp).Affinity) / 100f, 0f, 1f);

    private static void Record(Gladiator a, Gladiator b, MatchResult r, bool standing)
    {
        // 커리어는 항상, 시즌 순위는 정규 경기만(이벤트는 exhibition).
        if (r.Winner == 0) { a.CW++; b.CL++; if (r.Reason == "KO") a.CKoW++; if (standing) { a.W++; b.L++; a.Streak = a.Streak >= 0 ? a.Streak + 1 : 1; b.Streak = b.Streak <= 0 ? b.Streak - 1 : -1; } }
        else if (r.Winner == 1) { b.CW++; a.CL++; if (r.Reason == "KO") b.CKoW++; if (standing) { b.W++; a.L++; b.Streak = b.Streak >= 0 ? b.Streak + 1 : 1; a.Streak = a.Streak <= 0 ? a.Streak - 1 : -1; } }
        else { a.CD++; b.CD++; if (standing) { a.D++; b.D++; a.Streak = 0; b.Streak = 0; } }
    }

    // ── 영속 (P3-C) ──

    private int LoadWorld()
    {
        if (!File.Exists(WorldPath)) return 0;
        var w = JsonSerializer.Deserialize<WorldState>(File.ReadAllText(WorldPath), JsonOpts);
        if (w is null) return 0;
        if (w.ConstantsVer != ConstantsVer)
            Console.WriteLine($"  ⚠ world.json 상수버전 {w.ConstantsVer} ≠ 현재 {ConstantsVer} — 과거 시즌은 다른 밸런스로 치러짐(누적은 유지).");
        foreach (var rec in w.Gladiators)
            if (_byId.TryGetValue(rec.Id, out var g))
            { g.CW = rec.CW; g.CL = rec.CL; g.CD = rec.CD; g.CKoW = rec.CKoW; g.Fame = rec.Fame; g.Popularity = rec.Popularity; }
        _ledger.Load(w.Relations);
        return w.SeasonsPlayed;
    }

    private void SaveWorld(int seasonsPlayed) =>
        File.WriteAllText(WorldPath, JsonSerializer.Serialize(new WorldState(SchemaVer, ConstantsVer, seasonsPlayed,
            _cast.Select(g => new GladRec(g.Id, g.CW, g.CL, g.CD, g.CKoW, g.Fame, g.Popularity)).ToList(),
            _ledger.Snapshot().ToList()), JsonOpts));

    // ── season.json / API 상태 ──

    private SeasonDoc BuildDoc()
    {
        var leader = Standings()[0];
        // 정규 소진 직후엔 이벤트 카드가 아직 미편성(다음 PlayNext가 편성) → next=null, UI는 "이벤트 매치"로 안내.
        Sched? next = _cursor < _schedule.Count ? _schedule[_cursor] : null;
        var fighters = _cast.Select(g => new FighterDoc(g.Id, g.Name,
            g.WeaponId.Replace("WPN_", ""), g.TacticsId.Replace("TAC_", ""), g.PersonalityId.Replace("PER_", ""),
            g.W, g.L, g.D, g.SeasonPoints, g.Streak, g.CW, g.CL, g.CD, MathF.Round(g.Fame), MathF.Round(g.Popularity))).ToList();
        var rels = _ledger.AllRelations(PersOf)
            .Select(x => new RelDoc(_byId[x.Self].Name, _byId[x.Opp].Name, RelationTable.Get(x.Type).Name,
                                    MathF.Round(x.State.Affinity), x.State.Wins, x.State.Losses)).ToList();
        int total = _schedule.Count + (_eventsAppended ? 0 : _cast.Count / 2);   // 이벤트 미편성 시 예상 포함
        return new SeasonDoc(SchemaVer, _seasonNo, _rounds, _matchIdx, total, SeasonComplete,
            next is { } n ? _byId[n.AId].Name : null, next is { } n2 ? _byId[n2.BId].Name : null, next?.IsEvent ?? true,
            leader.Name, fighters, rels, _eventDocs.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList());
    }

    private void WriteSeasonJson() => File.WriteAllText("season.json", JsonSerializer.Serialize(BuildDoc(), JsonOpts));

    public string StateJson() => JsonSerializer.Serialize(BuildDoc(), JsonOpts);

    public string PlayNextJson() => JsonSerializer.Serialize(PlayNext(), JsonOpts);

    // ── CLI (구 season 명령 — Season.Run 대체) ──

    public static void RunCli(int rounds, ulong seed, bool fresh, bool serve)
    {
        var g = new Game(rounds, seed, fresh, interactive: false);
        while (!g.SeasonComplete) g.PlayNext();
        g.WriteSeasonJson();
        g.PrintReport();
        if (serve)
        {
            Console.WriteLine("\n  🌐 시즌 대시보드 서버 기동 (Ctrl+C로 종료)");
            ViewerServer.Serve(Directory.GetCurrentDirectory(), 5173, "league.html");
        }
    }

    private void PrintReport()
    {
        static string Streak(int s) => s > 0 ? $"{s}연승" : s < 0 ? $"{-s}연패" : "-";
        Console.WriteLine($"=== MORITURI 시즌 {_seasonNo} — {_cast.Count}인 라운드로빈 ×{_rounds}회 + 이벤트 매치 = {_matchIdx}경기 ===");
        Console.WriteLine($"  누적 {_seasonNo}시즌 (world.json 영속). 자동 편성(정규 라운드로빈 + 흥행 빅매치).\n");

        Console.WriteLine("  [이번 시즌 순위] (정규전만)");
        Console.WriteLine($"    {"선수",-12}{"승점",5}{"전적",12}{"현재",8}");
        var season = Standings();
        for (int k = 0; k < season.Count; k++)
        {
            var g = season[k];
            Console.WriteLine($"    {g.Name,-12}{g.SeasonPoints,5}{$"{g.W}-{g.L}-{g.D}",12}{Streak(g.Streak),8}{(k == 0 ? " 👑" : "")}");
        }
        Console.WriteLine($"\n  🏆 시즌 {_seasonNo} 챔피언: {season[0].Name}\n");

        Console.WriteLine("  [🎪 이벤트 매치 — 자동 편성] (흥행지수 = 라이벌리 × 인기, 시스템이 선정)");
        foreach (var m in _eventDocs)
            Console.WriteLine($"    {m.A} vs {m.B} (흥행 {m.Score:F0}) → {m.Winner} 승{(m.Ko ? "(KO)" : "")}");

        Console.WriteLine("\n  [통산 명성 리더보드] (여러 시즌 누적)");
        Console.WriteLine($"    {"선수",-12}{"명성",7}{"인기",7}{"통산전적",12}");
        foreach (var g in _cast.OrderByDescending(g => g.Fame).Take(5))
            Console.WriteLine($"    {g.Name,-12}{g.Fame,7:F0}{g.Popularity,7:F0}{$"{g.CW}-{g.CL}-{g.CD}",12}");

        var rels = _ledger.AllRelations(PersOf).ToList();
        Console.WriteLine("\n  [숙적 관계] (누적 관계 그래프)");
        foreach (var x in rels.Where(x => x.Type is RelationType.Nemesis or RelationType.Fear)
                               .OrderBy(x => x.State.Affinity).Take(5))
            Console.WriteLine($"    {_byId[x.Self].Name,-12} → {_byId[x.Opp].Name,-12} {RelationTable.Get(x.Type).Name} (통산 {x.State.Wins}승 {x.State.Losses}패)");

        int rev = _story.Count(s => s.Kind == "revenge"), ups = _story.Count(s => s.Kind == "upset"), cmb = _story.Count(s => s.Kind == "comeback");
        Console.WriteLine($"\n  [시즌 서사] 복수 {rev} · 이변 {ups} · 대역전 {cmb}");
        foreach (var s in _story.Where(s => s.Kind == "revenge").Take(6)) Console.WriteLine($"    {s.Text}");
        foreach (var s in _story.Where(s => s.Kind is "upset" or "comeback" && s.Round > _rounds).Take(4)) Console.WriteLine($"    {s.Text}");

        Console.WriteLine($"\n  (감정 발생 {100.0 * _emoGen / Math.Max(1, _matchIdx * 2):F1}% · world.json 저장 — season 재실행 시 누적)");
    }
}

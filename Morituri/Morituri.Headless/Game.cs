using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 감독(루두스) 모드 게임 상태 기계 (배포[12] W2 — 매니지먼트).
/// 관전자 → 감독: 내 루두스 선수단(영입·전술 선택·성장·시설)과 AI 소속 6명이 한 리그에서 싸운다.
///  - 모든 선수는 고유 천부/잠재력(StatGen)·특성(TraitGen)·전술 3종 풀을 부여받는다. Sim 무변경(전부 기존 조립).
///  - 내 선수: 매 경기 전 전술 택1(감독 수싸움). AI: 상대 맞춤 휴리스틱 + 시드 노이즈로 자기 풀에서 선택.
///  - 경제(데나리우스): 경기별 출전료(양 선수 인기=hype)·승리/서사 보너스 / 뽑기·시설·시즌말 급여.
///  - 성장: 경기 자동 소량 + 3경기당 훈련 포인트(감독 분배). 상한 = 잠재력 버짓 — 노화(30+ 랜덤)로 상한 자체가 감소.
///  - 영속: world.json v2 — 매 변이 후 저장, 미드시즌 완전 재개(모든 난수는 저장된 카운터에서 파생 = 결정론).
/// </summary>
public sealed class Game
{
    private const int SchemaVer = 2;      // v1(관전 시즌) 파일은 비호환 → 새 세계
    private const int ConstantsVer = 1;
    private const string WorldPath = "world.json";

    // ── 경제 상수 (초안 — 튜닝 전제) ──
    private const float GachaCost = 100f, StartGold = 50f;
    private const int StartFreeGachas = 2;
    private const float FeeBase = 5f, FeePopScale = 0.05f, WinBonus = 10f, KoBonus = 3f, DramaBonus = 5f;
    private static readonly float[] RankBonus = { 150f, 100f, 60f };   // 1~3위, 이하 20
    private const float SalaryBase = 10f, SalaryFameScale = 0.03f;
    private const float AgingDecayPerSeason = 10f, MinPotentialBudget = 120f;
    private const int TrainEveryMatches = 3;

    private sealed class Gladiator
    {
        public required string Id, Name, WeaponId, PersonalityId;   // 고정 정체성
        public required string[] TacticPool;                        // 전술 3종 (유동의 폭)
        public required string TacticId;                            // 현재 선택 (내 선수=감독, AI=경기마다 자동)
        public FighterStats Stats;                                  // 성장하는 현재 스탯
        public TalentGrade Talent; public PotentialGrade Potential;
        public float TalentBudget, PotentialBudget;                 // 상한(노화로 감소)
        public required string[] TraitIds;
        public bool IsPlayer;
        public int Age, AgingStartAge;
        public int TrainingPoints, MatchCounter;                    // 3경기 주기 훈련
        public int CW, CL, CD, CKoW; public float Fame, Popularity;
        public int W, L, D, Streak;
        public readonly List<string> PendingEmotions = new();
        public int SeasonPoints => W * 3 + D;
        public int CareerPoints => CW * 3 + CD;
        public PersonalityDef Pers => PersonalityTable.Get(PersonalityId);
    }

    // ── 영속 레코드 (world.json v2) ──
    private sealed record GladRec(string Id, string Name, string Weapon, string Personality,
        string[] TacticPool, string Tactic,
        float Atk, float Def, float Hp, float Spd, float Aspd, float Rct,
        int Talent, int Potential, float TalentBudget, float PotentialBudget,
        string[] Traits, bool IsPlayer, int Age, int AgingStartAge, int TrainingPoints, int MatchCounter,
        int CW, int CL, int CD, int CKoW, float Fame, float Popularity,
        int W, int L, int D, int Streak, string[] PendingEmotions);
    private sealed record SchedRec(int Round, string A, string B, bool IsEvent, float Score);
    private sealed record WorldV2(int SchemaVer, int ConstantsVer, ulong WorldSeed, float Gold,
        int GachaCount, int FreeGachas, int TrainingLv, int MedicalLv, int QuartersLv, int SeasonsPlayed,
        bool SeasonActive, int SeasonNo, int MatchIdx, int Cursor, bool EventsAppended,
        List<SchedRec>? Schedule, List<StoryDoc>? Story, List<EventDoc>? Events,
        List<GladRec> Gladiators, List<GladRec>? Candidates, List<RelationLedger.Entry> Relations);

    // ── season.json / API 문서 ──
    private sealed record EventDoc(string A, string B, float Score, string Winner, bool Ko);
    private sealed record FighterDoc(string Id, string Name, string Weapon, string Tactic, string Personality, int Age,
        int W, int L, int D, int Points, int Streak, int CW, int CL, int CD, float Fame, float Popularity, bool IsPlayer);
    private sealed record RelDoc(string Self, string Opp, string Type, float Affinity, int Wins, int Losses);
    private sealed record StoryDoc(int Round, string Kind, string Text);
    private sealed record SeasonDoc(int SchemaVer, int SeasonNo, int Rounds, int Matches, int TotalMatches, bool Completed,
        string? NextA, string? NextB, bool NextIsEvent, string Champion,
        List<FighterDoc> Fighters, List<RelDoc> Relations, List<EventDoc> Events, List<StoryDoc> Story);

    private sealed record StatsDoc(float Atk, float Def, float Hp, float Spd, float Aspd, float Rct);
    private sealed record MyFighterDoc(string Id, string Name, string Weapon, string Personality, int Age, bool Aging,
        string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] TacticPool, string Tactic, int TrainingPoints,
        int W, int L, int D, int CW, int CL, int CD, float Fame, float Popularity);
    private sealed record CandidateDoc(int Idx, string Name, string Weapon, string Personality, string RevealedTactic); // 마스킹!
    private sealed record OppPreview(string Name, string Weapon, string Personality, int Age, float Fame, float Popularity, string Career);
    private sealed record NextMatchDoc(int Round, bool IsEvent, bool IsPlayerMatch,
        string AName, string BName, string? MyId, string? MyName, string[]? MyPool, string? MyTactic, OppPreview? Opp);
    private sealed record GameStateDoc(SeasonDoc Season, float Gold, int FreeGachas, float GachaCost,
        int TrainingLv, int MedicalLv, int QuartersLv, int RosterCap, bool SeasonActive,
        List<MyFighterDoc> MyFighters, List<CandidateDoc> Candidates, NextMatchDoc? NextMatch);

    /// <summary>PlayNext 요약 (/api/next 응답).</summary>
    public sealed record MatchSummary(int SeasonNo, int Round, bool IsEvent, string A, string B,
        string Winner, string Reason, bool SeasonCompleted, bool NewSeasonStarted, bool WasPlayerMatch,
        float Income, string IncomeNote);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── 상태 ──
    private readonly List<Gladiator> _cast = new();
    private readonly List<Gladiator> _candidates = new();     // 대기 뽑기 후보 (전체 데이터 — JSON엔 마스킹)
    private readonly RelationLedger _ledger = new();
    private readonly int _rounds;
    private readonly bool _interactive;      // 클라이언트(파일 갱신) vs CLI
    private readonly bool _playerless;       // CLI 밸런싱: 루두스 없는 순수 AI 리그

    private ulong _worldSeed;
    private float _gold;
    private int _gachaCount, _freeGachas;
    private int _trainingLv = 1, _medicalLv = 1, _quartersLv;
    private int _seasonsPlayed;

    public bool SeasonActive { get; private set; }
    private int _seasonNo, _matchIdx, _cursor;
    private bool _eventsAppended;
    private readonly List<SchedRec> _schedule = new();
    private readonly List<(int Round, string Kind, string Text)> _story = new();
    private readonly List<EventDoc> _eventDocs = new();
    private int _emoGen;

    private int RosterCap => 3 + _quartersLv;
    private ulong SeasonSeed => _worldSeed + (ulong)_seasonNo * 1000003UL;
    private Gladiator ById(string id) => _cast.First(g => g.Id == id);
    private string PersOf(string id) => ById(id).PersonalityId;

    public Game(int roundsPerSeason, ulong? worldSeed = null, bool fresh = false,
                bool interactive = true, bool playerless = false)
    {
        _rounds = roundsPerSeason;
        _interactive = interactive;
        _playerless = playerless;

        if (!fresh && LoadWorld()) { if (_interactive) WriteSeasonJson(); return; }

        // ── 새 세계 ──
        _worldSeed = worldSeed ?? (ulong)Environment.TickCount64 * 2654435761UL + 12345UL;
        _gold = StartGold;
        _freeGachas = playerless ? 0 : StartFreeGachas;
        CreateAiCast();
        SeasonActive = false; _seasonNo = 0;    // 프리시즌: 영입 후 [개막]이 시즌 시작
        SaveWorld();
        if (_interactive) WriteSeasonJson();
    }

    // ── 캐스트/후보 생성 ──

    private static readonly (string Id, string Name, string Wpn, string Per, string Sig)[] AiCastDef =
    {
        ("GLA_MAXIMUS", "막시무스",   "WPN_SWORD",      "PER_BOLD",        "TAC_PRESSURE"),
        ("GLA_SPARTA",  "스파르타쿠스", "WPN_AXE",        "PER_RECKLESS",    "TAC_BRAWLER"),
        ("GLA_CRIXUS",  "크릭수스",   "WPN_DUALBLADES", "PER_CRUEL",       "TAC_BRAWLER"),
        ("GLA_GANNICUS","가니쿠스",   "WPN_SPEAR",      "PER_CALM",        "TAC_COUNTER"),
        ("GLA_BARCA",   "바르카",     "WPN_WHIP",       "PER_OPPORTUNIST", "TAC_ZONER"),
        ("GLA_NAEVIA",  "나이비아",   "WPN_SHIELD",     "PER_HONORABLE",   "TAC_DEFENDER"),
    };

    private static readonly string[] RecruitNames =
    {
        "루푸스","펠릭스","카시우스","세베루스","티투스","옥타비우스","다리우스","발레리우스",
        "트라야누스","아우렐리우스","콤모두스","페르티낙스","알비누스","마크리누스","고르디아누스","필리푸스",
        "데키우스","갈루스","플라비우스","루키우스","퀸투스","세르비우스","아피우스","호라티우스",
    };

    private void CreateAiCast()
    {
        var rng = new SimRandom(_worldSeed ^ 0xCA57_CA57UL);
        foreach (var (id, name, wpn, per, sig) in AiCastDef)
        {
            var g = RollGladiator(rng, id, name, wpn, per, sigTactic: sig, isPlayer: false,
                                  ageMin: 20, ageMax: 28);
            _cast.Add(g);
        }
    }

    /// <summary>선수 1명 롤: 천부/잠재력(StatGen) + 특성(TraitGen) + 전술풀 3종 + 나이/노화 시작 나이.</summary>
    private static Gladiator RollGladiator(SimRandom rng, string id, string name, string wpn, string per,
                                           string? sigTactic, bool isPlayer, int ageMin, int ageMax)
    {
        var end = StatGen.Roll(rng);
        var traits = TraitGen.Roll(rng);
        var pool = RollTacticPool(rng, sigTactic);
        return new Gladiator
        {
            Id = id, Name = name, WeaponId = wpn, PersonalityId = per,
            TacticPool = pool, TacticId = pool[0],
            Stats = end.Stats, Talent = end.Talent, Potential = end.Potential,
            TalentBudget = end.TalentBudget, PotentialBudget = end.PotentialBudget,
            TraitIds = traits, IsPlayer = isPlayer,
            Age = ageMin + (int)(rng.NextFloat01() * (ageMax - ageMin + 1)),
            AgingStartAge = 30 + (int)(rng.NextFloat01() * 7),   // 30~36 (라니스타: 최저 30)
        };
    }

    private static string[] RollTacticPool(SimRandom rng, string? mustInclude)
    {
        var all = TacticsTable.All.Select(t => t.Id).ToList();
        var pool = new List<string>(3);
        if (mustInclude != null) { pool.Add(mustInclude); all.Remove(mustInclude); }
        while (pool.Count < 3)
        {
            int i = (int)(rng.NextFloat01() * all.Count);
            pool.Add(all[i]); all.RemoveAt(i);
        }
        return pool.ToArray();
    }

    // ── 스탯 축 헬퍼 (HP축 1pt = HP 10, StatGen 규약) ──
    private static float BudgetUsed(in FighterStats s) => s.Atk + s.Def + s.HpMax / 10f + s.Spd + s.Aspd + s.Rct;
    private static float AxisVal(in FighterStats s, int a) => a switch
    { 0 => s.Atk, 1 => s.Def, 2 => s.HpMax / 10f, 3 => s.Spd, 4 => s.Aspd, _ => s.Rct };
    private static FighterStats WithAxis(in FighterStats s, int a, float pts)
    {
        float v = Math.Clamp(AxisVal(s, a) + pts, 20f, 150f);
        return a switch
        {
            0 => s with { Atk = v }, 1 => s with { Def = v }, 2 => s with { HpMax = v * 10f },
            3 => s with { Spd = v }, 4 => s with { Aspd = v }, _ => s with { Rct = v },
        };
    }
    private static readonly string[] AxisNames = { "Atk", "Def", "Hp", "Spd", "Aspd", "Rct" };

    // ── 시즌 수명주기 ──

    private void StartSeason()
    {
        _seasonNo = _seasonsPlayed + 1;
        _matchIdx = 0; _emoGen = 0; _cursor = 0; _eventsAppended = false;
        _story.Clear(); _eventDocs.Clear(); _schedule.Clear();
        SeasonActive = true;
        foreach (var g in _cast) { g.W = g.L = g.D = g.Streak = 0; g.PendingEmotions.Clear(); }

        for (int r = 1; r <= _rounds; r++)
            for (int i = 0; i < _cast.Count; i++)
                for (int j = i + 1; j < _cast.Count; j++)
                    _schedule.Add(new SchedRec(r, _cast[i].Id, _cast[j].Id, false, 0f));
        _story.Add((0, "season", $"🏛 시즌 {_seasonNo} 개막 — {_cast.Count}인 리그"));
    }

    private void FinalizeSeason()
    {
        SeasonActive = false;
        _seasonsPlayed = _seasonNo;
        var standings = Standings();
        var champ = standings[0];
        _story.Add((_rounds + 1, "season", $"🏆 시즌 {_seasonNo} 종료 — 챔피언 {champ.Name} ({champ.W}승 {champ.L}패)"));

        // 시즌 순위 보너스 (내 최고 순위 기준)
        if (!_playerless && _cast.Any(g => g.IsPlayer))
        {
            int best = standings.FindIndex(g => g.IsPlayer);
            float bonus = best >= 0 && best < RankBonus.Length ? RankBonus[best] : 20f;
            _gold += bonus;
            // 급여 공제 (스타는 비싸다)
            float salary = _cast.Where(g => g.IsPlayer).Sum(g => SalaryBase + g.Fame * SalaryFameScale);
            _gold = MathF.Max(0f, _gold - salary);
            _story.Add((_rounds + 1, "season", $"💰 시즌 정산 — 순위 보너스 +{bonus:F0} · 급여 −{salary:F0} (잔고 {_gold:F0})"));
        }

        // 나이/노화: 시즌당 +1세, 노화 시작 후 잠재력 상한 점진 감소 (의무실은 내 선수만 감면)
        foreach (var g in _cast)
        {
            g.Age++;
            if (g.Age >= g.AgingStartAge)
            {
                float relief = g.IsPlayer ? 0.25f * (_medicalLv - 1) : 0f;
                g.PotentialBudget = MathF.Max(MinPotentialBudget, g.PotentialBudget - AgingDecayPerSeason * (1f - relief));
                float excess = BudgetUsed(g.Stats) - g.PotentialBudget;
                if (excess > 0f)
                {
                    // 상한 아래로 — 현재 스탯도 깎인다. RCT 가중 50%([3]6.3 노화는 반응속도부터) + 나머지 균등.
                    g.Stats = WithAxis(g.Stats, 5, -excess * 0.5f);
                    for (int a = 0; a < 5; a++) g.Stats = WithAxis(g.Stats, a, -excess * 0.1f);
                    if (g.IsPlayer) _story.Add((_rounds + 1, "aging", $"⏳ {g.Name}({g.Age}세) — 세월이 몸을 갉아먹는다 (상한 {g.PotentialBudget:F0})"));
                }
            }
            g.Popularity *= 0.6f;   // 시즌 사이 화제성 감쇠
        }

        SaveWorld();
    }

    private List<Gladiator> Standings() =>
        _cast.OrderByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W).ToList();

    // ── 진행 ──

    /// <summary>다음 경기 1판. 프리시즌이면 시즌 개막만(경기 안 침 — 감독이 1경기부터 전술을 고를 수 있게).
    /// tacticId = 내 선수의 이번 경기 전술(선택).</summary>
    public MatchSummary PlayNext(string? tacticId = null)
    {
        if (!SeasonActive)
        {
            StartSeason(); SaveWorld();
            if (_interactive) WriteSeasonJson();
            return new MatchSummary(_seasonNo, 0, false, "", "", "", "개막", false, true, false, 0f, "");
        }
        bool newSeason = false;

        if (_cursor >= _schedule.Count && !_eventsAppended)
        {
            foreach (var (a, b, score) in TopEventCards(Math.Max(2, _cast.Count / 2)))
                _schedule.Add(new SchedRec(_rounds + 1, a, b, true, score));
            _eventsAppended = true;
        }

        var s = _schedule[_cursor++];
        var A = ById(s.A); var B = ById(s.B);

        // 전술 결정: 내 선수 = 감독 선택(이번 요청 or 기존 유지) / AI = 상대 맞춤 휴리스틱 + 시드 노이즈
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);
        if (A.IsPlayer) { if (tacticId != null && A.TacticPool.Contains(tacticId)) A.TacticId = tacticId; }
        else A.TacticId = SelectTacticAi(A, B, tacRng);
        if (B.IsPlayer) { if (tacticId != null && !A.IsPlayer && B.TacticPool.Contains(tacticId)) B.TacticId = tacticId; }
        else B.TacticId = SelectTacticAi(B, A, tacRng);

        var res = Play(A, B, s.Round, s.IsEvent, out float income, out string incomeNote);
        if (s.IsEvent)
            _eventDocs.Add(new EventDoc(A.Name, B.Name, s.Score,
                res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason == "KO"));

        bool last = _cursor >= _schedule.Count && _eventsAppended;
        if (last) FinalizeSeason();
        else SaveWorld();
        if (_interactive) WriteSeasonJson();

        return new MatchSummary(_seasonNo, s.Round, s.IsEvent, A.Name, B.Name,
            res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason, last, newSeason,
            A.IsPlayer || B.IsPlayer, income, incomeNote);
    }

    /// <summary>AI 전술 선택: 상대 무기 사거리 카운터 + 자기 무기 시너지 + 노이즈 → 풀에서 argmax.</summary>
    private static string SelectTacticAi(Gladiator self, Gladiator opp, SimRandom rng)
    {
        float oppRange = WeaponTable.Get(opp.WeaponId).Range;
        float ownRange = WeaponTable.Get(self.WeaponId).Range;
        string best = self.TacticPool[0]; float bestScore = float.MinValue;
        foreach (var tid in self.TacticPool)
        {
            var t = TacticsTable.Get(tid);
            float score = 0f;
            bool rush = tid is "TAC_PRESSURE" or "TAC_BRAWLER" or "TAC_HUNTER" or "TAC_GAMBLER";
            bool keep = tid is "TAC_COUNTER" or "TAC_ZONER" or "TAC_DEFENDER" or "TAC_EVADER";
            if (oppRange >= 3.0f && rush) score += 2f;        // 장거리 카이터 상대 → 파고들기
            if (oppRange < 3.0f && keep) score += 2f;         // 근접 상대 → 거리·반응
            if (tid is "TAC_BALANCED" or "TAC_DECISION") score += 1f;
            score -= MathF.Abs(t.PreferredDistance - ownRange * 0.8f) * 0.5f;   // 제 무기와 안 맞는 전술 감점
            score += rng.Range(0f, 1.5f);                     // 예측불가성
            if (score > bestScore) { bestScore = score; best = tid; }
        }
        return best;
    }

    private MatchResult Play(Gladiator A, Gladiator B, int round, bool isEvent, out float income, out string incomeNote)
    {
        var relA = _ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
        var relB = _ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
        var defA = ToDef(A, relA, Intensity(A.Id, B.Id));
        var defB = ToDef(B, relB, Intensity(B.Id, A.Id));
        A.PendingEmotions.Clear(); B.PendingEmotions.Clear();   // 감정 소비 → 소멸 ([2]§6-1)

        ulong seed = SeasonSeed + (ulong)(++_matchIdx);
        MatchResult res;
        if (_interactive)
        {
            var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
            res = new MatchSim().Run(defA, defB, seed, events, frames);
            ViewerExport.WriteDoc(defA, defB, seed, res, frames, events, "viewer.json", Endow(A), Endow(B));
        }
        else res = new MatchSim().Run(defA, defB, seed);

        bool ko = res.Reason == "KO";
        bool comeback = false, upset = false, revenge = false;
        Gladiator? win = null, lose = null;
        MatchFighterStats winStats = res.StatsA, loseStats = res.StatsB;
        if (res.Winner >= 0)
        {
            (win, lose) = res.Winner == 0 ? (A, B) : (B, A);
            (winStats, loseStats) = res.Winner == 0 ? (res.StatsA, res.StatsB) : (res.StatsB, res.StatsA);
            upset = win.CareerPoints < lose.CareerPoints;
            comeback = winStats.MinHpPct <= 0.10f;

            var prior = _ledger.Get(win.Id, lose.Id);
            var priorRel = prior.Classify(win.PersonalityId);
            revenge = prior.Losses > prior.Wins && priorRel is RelationType.Nemesis or RelationType.Fear;
            if (revenge)
                _story.Add((round, "revenge", $"R{round} ⚔ 복수! {win.Name}이(가) 숙적 {lose.Name}에게 설욕 (그간 {prior.Wins}승 {prior.Losses}패)"));
            else if (upset)
                _story.Add((round, "upset", $"R{round} ★ 이변! {win.Name}이(가) 상위 {lose.Name}을(를) 격파"));
            if (comeback)
                _story.Add((round, "comeback", $"R{round} 🔥 대역전! {win.Name} 사선(HP{winStats.MinHpPct * 100:F0}%)에서 {lose.Name} 제압"));

            // 명성(통산 업적, 무감쇠) — 승자 중심
            win.Fame += 3f + (ko ? 2f : 0f) + (comeback ? 5f : 0f) + (upset ? 4f : 0f)
                      + winStats.CleanHits * 0.1f + winStats.Knockdowns;
            lose.Fame = MathF.Max(0f, lose.Fame + 0.5f - (loseStats.Taunted ? 2f : 0f));
        }

        // 인기(최근 화제성, 감쇠) — 패자도 잘 싸우면 오른다
        UpdatePopularity(A, res.StatsA, res.StatsB, res.Winner == 0, res.Winner < 0, ko, comeback, upset, revenge, isEvent);
        UpdatePopularity(B, res.StatsB, res.StatsA, res.Winner == 1, res.Winner < 0, ko, comeback, upset, revenge, isEvent);

        // 경제: 내 선수 출전 시 경기별 수입 (출전료 = hype)
        income = 0f; var notes = new List<string>();
        foreach (var (mine, other) in new[] { (A, B), (B, A) })
        {
            if (_playerless || !mine.IsPlayer) continue;
            float fee = (FeeBase + (mine.Popularity + other.Popularity) * FeePopScale) * (isEvent ? 2f : 1f);
            income += fee; notes.Add($"출전료 +{fee:F0}");
            if (win == mine)
            {
                float bonus = WinBonus + (ko ? KoBonus : 0f) + (comeback ? DramaBonus : 0f) + (upset ? DramaBonus : 0f);
                income += bonus; notes.Add($"승리 +{bonus:F0}");
            }
        }
        _gold += income;
        incomeNote = string.Join(" · ", notes);

        // 순위/커리어 + 관계 + 감정 (경기 인덱스 파생 스트림 = 미드시즌 재개 결정론)
        Record(A, B, res, standing: !isEvent);
        _ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
        var emoRng = new SimRandom(SeasonSeed ^ 0x5EA5_04EDUL + (ulong)_matchIdx * 17UL);
        if (EmotionGen.Roll(emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers) is { } eA) { A.PendingEmotions.Add(eA); _emoGen++; }
        if (EmotionGen.Roll(emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers) is { } eB) { B.PendingEmotions.Add(eB); _emoGen++; }

        // 성장: 경기 자동 소량 + 3경기당 훈련 포인트
        var growRng = new SimRandom(SeasonSeed ^ 0x6120_6120UL + (ulong)_matchIdx * 13UL);
        Grow(A, growRng); Grow(B, growRng);
        TickTraining(A, growRng); TickTraining(B, growRng);

        return res;
    }

    /// <summary>인기 적립(경기별): 스펙터클 + 결과항(선전패 보정) + 도발항, 이벤트 ×1.5. 자기감쇠 ×0.95.</summary>
    private static void UpdatePopularity(Gladiator g, MatchFighterStats own, MatchFighterStats opp,
        bool isWinner, bool isDraw, bool ko, bool comeback, bool upset, bool revenge, bool isEvent)
    {
        float spect = own.CleanHits * 0.3f + own.Knockdowns * 2f
                    + (isWinner && comeback ? 8f : 0f) + (isWinner && upset ? 6f : 0f) + (isWinner && revenge ? 6f : 0f);
        float result = isDraw ? 2f
                     : isWinner ? 4f + (ko ? 2f : 0f)
                     : 1f + (opp.MinHpPct <= 0.30f ? 2f : 0f);   // 선전패: 상대를 사선까지 몰았다
        float taunt = own.Taunted ? (isWinner ? 3f : -6f) : 0f;
        float matchPop = (spect + result + taunt) * (isEvent ? 1.5f : 1f);
        g.Popularity = MathF.Max(0f, g.Popularity * 0.95f + matchPop);
    }

    private void Grow(Gladiator g, SimRandom rng)
    {
        if (BudgetUsed(g.Stats) + 0.5f > g.PotentialBudget) return;   // 상한 도달 — 더 안 큼
        int axis = (int)(rng.NextFloat01() * 6f);
        g.Stats = WithAxis(g.Stats, axis, 0.5f);
    }

    private void TickTraining(Gladiator g, SimRandom rng)
    {
        if (++g.MatchCounter < TrainEveryMatches) return;
        g.MatchCounter = 0;
        int pts = g.IsPlayer ? _trainingLv : 1;
        if (g.IsPlayer) g.TrainingPoints += pts;                      // 감독이 분배
        else for (int i = 0; i < pts; i++) Grow(g, rng);              // AI 자동 (같은 리듬, 형평)
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

    private FighterDef ToDef(Gladiator g, RelationType? rel, float intensity) =>
        new(g.Name, g.Stats, g.WeaponId, g.TacticId, g.PersonalityId,
            g.TraitIds.Length > 0 ? g.TraitIds : null,
            g.PendingEmotions.Count > 0 ? g.PendingEmotions.ToArray() : null, rel, intensity);

    private ViewerEndowment Endow(Gladiator g) => new(
        ViewerExport.TalentName(g.Talent), ViewerExport.PotentialName(g.Potential),
        g.TalentBudget, g.PotentialBudget,
        g.Stats.Atk, g.Stats.Def, g.Stats.HpMax, g.Stats.Spd, g.Stats.Aspd, g.Stats.Rct);

    private float Intensity(string self, string opp)
        => Math.Clamp(MathF.Abs(_ledger.Get(self, opp).Affinity) / 100f, 0f, 1f);

    private static void Record(Gladiator a, Gladiator b, MatchResult r, bool standing)
    {
        if (r.Winner == 0) { a.CW++; b.CL++; if (r.Reason == "KO") a.CKoW++; if (standing) { a.W++; b.L++; a.Streak = a.Streak >= 0 ? a.Streak + 1 : 1; b.Streak = b.Streak <= 0 ? b.Streak - 1 : -1; } }
        else if (r.Winner == 1) { b.CW++; a.CL++; if (r.Reason == "KO") b.CKoW++; if (standing) { b.W++; a.L++; b.Streak = b.Streak >= 0 ? b.Streak + 1 : 1; a.Streak = a.Streak <= 0 ? a.Streak - 1 : -1; } }
        else { a.CD++; b.CD++; if (standing) { a.D++; b.D++; a.Streak = 0; b.Streak = 0; } }
    }

    // ── 감독 액션 API ──

    /// <summary>뽑기: 재화(또는 무료권) 소모 → 후보 3명(마스킹). 기존 후보는 소멸(포기).</summary>
    public string GachaJson()
    {
        if (_playerless) return Err("CLI 모드");
        if (_cast.Count(g => g.IsPlayer) >= RosterCap) return Err($"로스터 가득참 (상한 {RosterCap} — 숙소 증축 필요)");
        if (_freeGachas > 0) _freeGachas--;
        else if (_gold >= GachaCost) _gold -= GachaCost;
        else return Err($"잔고 부족 (뽑기 {GachaCost:F0})");

        _candidates.Clear();
        var rng = new SimRandom(_worldSeed ^ 0x6ACA_6ACAUL + (ulong)(++_gachaCount) * 2654435761UL);
        var usedNames = _cast.Select(g => g.Name).Concat(_candidates.Select(c => c.Name)).ToHashSet();
        var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
        var pers = PersonalityTable.All.Select(p => p.Id).ToArray();
        for (int i = 0; i < 3; i++)
        {
            string name = PickName(rng, usedNames); usedNames.Add(name);
            var g = RollGladiator(rng,
                id: $"GLA_R{_gachaCount}_{i}", name,
                wpn: wpns[(int)(rng.NextFloat01() * wpns.Length)],
                per: pers[(int)(rng.NextFloat01() * pers.Length)],
                sigTactic: null, isPlayer: true, ageMin: 18, ageMax: 24);
            _candidates.Add(g);
        }
        SaveWorld();
        return StateJson();
    }

    private static string PickName(SimRandom rng, HashSet<string> used)
    {
        for (int t = 0; t < 32; t++)
        {
            string n = RecruitNames[(int)(rng.NextFloat01() * RecruitNames.Length)];
            if (!used.Contains(n)) return n;
        }
        return $"검투사{used.Count + 1}";
    }

    /// <summary>영입: 후보 택1 → 전체 공개 + 로스터 편입 (시즌 중이면 다음 시즌부터 출전).</summary>
    public string RecruitJson(int idx)
    {
        if (idx < 0 || idx >= _candidates.Count) return Err("후보 없음");
        if (_cast.Count(g => g.IsPlayer) >= RosterCap) return Err("로스터 가득참");
        var g = _candidates[idx];
        _candidates.Clear();          // 나머지 후보는 떠난다
        _cast.Add(g);                 // 스케줄은 시즌 개막 시 고정 → 시즌 중 영입은 다음 시즌부터
        _story.Add((0, "recruit", $"📜 영입! {g.Name} ({ViewerExport.TalentName(g.Talent)}·{g.Age}세) 루두스 합류" +
                                   (SeasonActive ? " — 다음 시즌부터 출전" : "")));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>훈련: 포인트 1을 축에 분배 (axis: Atk/Def/Hp/Spd/Aspd/Rct).</summary>
    public string TrainJson(string fighterId, string axis)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        if (g.TrainingPoints <= 0) return Err("훈련 포인트 없음");
        int a = Array.IndexOf(AxisNames, axis);
        if (a < 0) return Err("잘못된 축");
        if (AxisVal(g.Stats, a) >= 150f) return Err("축 상한(150)");
        if (BudgetUsed(g.Stats) + 1f > g.PotentialBudget) return Err($"잠재력 상한 도달 ({g.PotentialBudget:F0})");
        g.TrainingPoints--;
        g.Stats = WithAxis(g.Stats, a, 1f);
        SaveWorld();
        return StateJson();
    }

    /// <summary>시설 구매: training / medical / quarters.</summary>
    public string BuildJson(string facility)
    {
        (int lv, int max, float[] costs) = facility switch
        {
            "training" => (_trainingLv, 3, new[] { 200f, 500f }),
            "medical"  => (_medicalLv, 3, new[] { 200f, 500f }),
            "quarters" => (_quartersLv, 2, new[] { 300f, 600f }),
            _ => (0, 0, Array.Empty<float>()),
        };
        if (costs.Length == 0) return Err("잘못된 시설");
        int step = facility == "quarters" ? lv : lv - 1;          // 다음 단계 비용 인덱스
        if (step >= costs.Length || lv >= max) return Err("최대 레벨");
        if (_gold < costs[step]) return Err($"잔고 부족 ({costs[step]:F0})");
        _gold -= costs[step];
        if (facility == "training") _trainingLv++;
        else if (facility == "medical") _medicalLv++;
        else _quartersLv++;
        SaveWorld();
        return StateJson();
    }

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, JsonOpts);

    // ── 영속 (world.json v2 — 미드시즌 완전 세이브) ──

    private bool LoadWorld()
    {
        if (!File.Exists(WorldPath)) return false;
        WorldV2? w;
        try { w = JsonSerializer.Deserialize<WorldV2>(File.ReadAllText(WorldPath), JsonOpts); }
        catch { Console.WriteLine("  ⚠ world.json 해석 실패 — 새 세계로 시작."); return false; }
        if (w is null || w.SchemaVer != SchemaVer)
        { Console.WriteLine($"  ⚠ world.json 스키마 v{w?.SchemaVer} ≠ v{SchemaVer} (감독 모드 개편) — 새 세계로 시작."); return false; }
        if (w.ConstantsVer != ConstantsVer)
            Console.WriteLine($"  ⚠ 상수버전 {w.ConstantsVer} ≠ {ConstantsVer} — 과거 경기는 다른 밸런스.");

        _worldSeed = w.WorldSeed; _gold = w.Gold;
        _gachaCount = w.GachaCount; _freeGachas = w.FreeGachas;
        _trainingLv = w.TrainingLv; _medicalLv = w.MedicalLv; _quartersLv = w.QuartersLv;
        _seasonsPlayed = w.SeasonsPlayed;
        SeasonActive = w.SeasonActive; _seasonNo = w.SeasonNo; _matchIdx = w.MatchIdx;
        _cursor = w.Cursor; _eventsAppended = w.EventsAppended;
        _schedule.Clear(); if (w.Schedule != null) _schedule.AddRange(w.Schedule);
        _story.Clear(); if (w.Story != null) _story.AddRange(w.Story.Select(s => (s.Round, s.Kind, s.Text)));
        _eventDocs.Clear(); if (w.Events != null) _eventDocs.AddRange(w.Events);
        _cast.Clear(); _cast.AddRange(w.Gladiators.Select(FromRec));
        _candidates.Clear(); if (w.Candidates != null) _candidates.AddRange(w.Candidates.Select(FromRec));
        _ledger.Load(w.Relations);
        return true;
    }

    private void SaveWorld() =>
        File.WriteAllText(WorldPath, JsonSerializer.Serialize(new WorldV2(
            SchemaVer, ConstantsVer, _worldSeed, _gold, _gachaCount, _freeGachas,
            _trainingLv, _medicalLv, _quartersLv, _seasonsPlayed,
            SeasonActive, _seasonNo, _matchIdx, _cursor, _eventsAppended,
            _schedule.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList(),
            _eventDocs.ToList(),
            _cast.Select(ToRec).ToList(),
            _candidates.Count > 0 ? _candidates.Select(ToRec).ToList() : null,
            _ledger.Snapshot().ToList()), JsonOpts));

    private static GladRec ToRec(Gladiator g) => new(g.Id, g.Name, g.WeaponId, g.PersonalityId,
        g.TacticPool, g.TacticId,
        g.Stats.Atk, g.Stats.Def, g.Stats.HpMax, g.Stats.Spd, g.Stats.Aspd, g.Stats.Rct,
        (int)g.Talent, (int)g.Potential, g.TalentBudget, g.PotentialBudget,
        g.TraitIds, g.IsPlayer, g.Age, g.AgingStartAge, g.TrainingPoints, g.MatchCounter,
        g.CW, g.CL, g.CD, g.CKoW, g.Fame, g.Popularity,
        g.W, g.L, g.D, g.Streak, g.PendingEmotions.ToArray());

    private static Gladiator FromRec(GladRec r)
    {
        var g = new Gladiator
        {
            Id = r.Id, Name = r.Name, WeaponId = r.Weapon, PersonalityId = r.Personality,
            TacticPool = r.TacticPool, TacticId = r.Tactic,
            Stats = new FighterStats(r.Atk, r.Def, r.Hp, r.Spd, r.Aspd, r.Rct),
            Talent = (TalentGrade)r.Talent, Potential = (PotentialGrade)r.Potential,
            TalentBudget = r.TalentBudget, PotentialBudget = r.PotentialBudget,
            TraitIds = r.Traits, IsPlayer = r.IsPlayer, Age = r.Age, AgingStartAge = r.AgingStartAge,
            TrainingPoints = r.TrainingPoints, MatchCounter = r.MatchCounter,
            CW = r.CW, CL = r.CL, CD = r.CD, CKoW = r.CKoW, Fame = r.Fame, Popularity = r.Popularity,
            W = r.W, L = r.L, D = r.D, Streak = r.Streak,
        };
        g.PendingEmotions.AddRange(r.PendingEmotions);
        return g;
    }

    // ── 상태 문서 ──

    private SeasonDoc BuildSeasonDoc()
    {
        var standings = Standings();
        SchedRec? next = SeasonActive && _cursor < _schedule.Count ? _schedule[_cursor] : null;
        var fighters = _cast.Select(g => new FighterDoc(g.Id, g.Name,
            g.WeaponId.Replace("WPN_", ""), g.TacticId.Replace("TAC_", ""), g.PersonalityId.Replace("PER_", ""), g.Age,
            g.W, g.L, g.D, g.SeasonPoints, g.Streak, g.CW, g.CL, g.CD,
            MathF.Round(g.Fame), MathF.Round(g.Popularity), g.IsPlayer)).ToList();
        var rels = _ledger.AllRelations(PersOf)
            .Select(x => new RelDoc(ById(x.Self).Name, ById(x.Opp).Name, RelationTable.Get(x.Type).Name,
                                    MathF.Round(x.State.Affinity), x.State.Wins, x.State.Losses)).ToList();
        int total = _schedule.Count + (_eventsAppended || !SeasonActive ? 0 : Math.Max(2, _cast.Count / 2));
        return new SeasonDoc(SchemaVer, Math.Max(1, _seasonNo), _rounds, _matchIdx, total, !SeasonActive,
            next != null ? ById(next.A).Name : null, next != null ? ById(next.B).Name : null, next?.IsEvent ?? true,
            standings[0].Name, fighters, rels, _eventDocs.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList());
    }

    private void WriteSeasonJson() => File.WriteAllText("season.json", JsonSerializer.Serialize(BuildSeasonDoc(), JsonOpts));

    public string StateJson()
    {
        var my = _cast.Where(g => g.IsPlayer).Select(g => new MyFighterDoc(g.Id, g.Name,
            g.WeaponId.Replace("WPN_", ""), g.PersonalityId.Replace("PER_", ""), g.Age, g.Age >= g.AgingStartAge,
            ViewerExport.TalentName(g.Talent), ViewerExport.PotentialName(g.Potential),
            MathF.Round(g.PotentialBudget), MathF.Round(BudgetUsed(g.Stats)),
            new StatsDoc(MathF.Round(g.Stats.Atk), MathF.Round(g.Stats.Def), MathF.Round(g.Stats.HpMax),
                         MathF.Round(g.Stats.Spd), MathF.Round(g.Stats.Aspd), MathF.Round(g.Stats.Rct)),
            g.TraitIds.Select(t => TraitTable.Get(t).Name).ToArray(),
            g.TacticPool.Select(t => t.Replace("TAC_", "")).ToArray(), g.TacticId.Replace("TAC_", ""),
            g.TrainingPoints, g.W, g.L, g.D, g.CW, g.CL, g.CD,
            MathF.Round(g.Fame), MathF.Round(g.Popularity))).ToList();

        var cands = _candidates.Select((c, i) => new CandidateDoc(i, c.Name,
            c.WeaponId.Replace("WPN_", ""), c.PersonalityId.Replace("PER_", ""),
            c.TacticPool[0].Replace("TAC_", ""))).ToList();   // ★ 마스킹: 무기·성격·전술1만 공개

        NextMatchDoc? nm = null;
        if (SeasonActive && _cursor < _schedule.Count)
        {
            var s = _schedule[_cursor];
            var A = ById(s.A); var B = ById(s.B);
            var mine = A.IsPlayer ? A : B.IsPlayer ? B : null;
            var opp = mine == A ? B : A;
            nm = new NextMatchDoc(s.Round, s.IsEvent, mine != null, A.Name, B.Name,
                mine?.Id, mine?.Name,
                mine?.TacticPool.Select(t => t.Replace("TAC_", "")).ToArray(),
                mine?.TacticId.Replace("TAC_", ""),
                mine != null ? new OppPreview(opp.Name, opp.WeaponId.Replace("WPN_", ""),
                    opp.PersonalityId.Replace("PER_", ""), opp.Age,
                    MathF.Round(opp.Fame), MathF.Round(opp.Popularity), $"{opp.CW}-{opp.CL}-{opp.CD}") : null);
        }

        return JsonSerializer.Serialize(new GameStateDoc(BuildSeasonDoc(), MathF.Round(_gold), _freeGachas, GachaCost,
            _trainingLv, _medicalLv, _quartersLv, RosterCap, SeasonActive, my, cands, nm), JsonOpts);
    }

    public string PlayNextJson(string? body)
    {
        string? tacticId = null;
        if (!string.IsNullOrWhiteSpace(body))
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("tacticId", out var t) && t.GetString() is { Length: > 0 } tid)
                    tacticId = tid.StartsWith("TAC_") ? tid : "TAC_" + tid;
            }
            catch { }
        return JsonSerializer.Serialize(PlayNext(tacticId), JsonOpts);
    }

    // ── CLI (season 명령 — 순수 AI 리그, 밸런싱 도구) ──

    public static void RunCli(int rounds, ulong seed, bool fresh, bool serve)
    {
        var g = new Game(rounds, seed, fresh, interactive: false, playerless: true);
        g.PlayNext();                        // 개막 + 1경기
        while (g.SeasonActive) g.PlayNext();
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
        Console.WriteLine($"=== MORITURI 시즌 {_seasonNo} — {_cast.Count}인 리그 ×{_rounds}회 + 이벤트 = {_matchIdx}경기 (worldSeed {_worldSeed}) ===");
        Console.WriteLine("  전 선수 고유 천부/특성/전술풀 — AI는 경기마다 상대 맞춤 전술 선택.\n");

        Console.WriteLine("  [순위]");
        var season = Standings();
        for (int k = 0; k < season.Count; k++)
        {
            var g = season[k];
            Console.WriteLine($"    {k + 1}. {g.Name,-12}{g.SeasonPoints,4}점 {$"{g.W}-{g.L}-{g.D}",9} {Streak(g.Streak),6}" +
                $"  {ViewerExport.TalentName(g.Talent)}·{g.Age}세·[{string.Join(",", g.TraitIds.Select(t => TraitTable.Get(t).Name))}]{(k == 0 ? " 👑" : "")}");
        }
        Console.WriteLine($"\n  🏆 챔피언: {season[0].Name}\n");

        Console.WriteLine("  [🎪 이벤트 매치]");
        foreach (var m in _eventDocs)
            Console.WriteLine($"    {m.A} vs {m.B} (흥행 {m.Score:F0}) → {m.Winner} 승{(m.Ko ? "(KO)" : "")}");

        Console.WriteLine("\n  [명성/인기]");
        foreach (var g in _cast.OrderByDescending(g => g.Fame).Take(5))
            Console.WriteLine($"    {g.Name,-12} 명성 {g.Fame,5:F0} · 인기 {g.Popularity,5:F0} · 통산 {g.CW}-{g.CL}-{g.CD}");

        int rev = _story.Count(s => s.Kind == "revenge"), ups = _story.Count(s => s.Kind == "upset"), cmb = _story.Count(s => s.Kind == "comeback");
        Console.WriteLine($"\n  [서사] 복수 {rev} · 이변 {ups} · 대역전 {cmb} (감정 발생 {100.0 * _emoGen / Math.Max(1, _matchIdx * 2):F1}%)");
        foreach (var s in _story.Where(s => s.Kind == "revenge").Take(5)) Console.WriteLine($"    {s.Text}");
    }
}

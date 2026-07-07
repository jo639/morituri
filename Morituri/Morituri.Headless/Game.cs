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

    // ── 루두스 등급(명성) — 승리·우승·흥행으로 축적. 뽑기 질·수입 배율에 영향. ──
    private static readonly (float Rep, string Name)[] LudusTiers =
    {
        (0f, "무명 양성소"), (120f, "신흥 루두스"), (350f, "이름난 루두스"),
        (800f, "명문 루두스"), (1600f, "제국의 자랑"), (3200f, "콜로세움의 지배자"),
    };
    private const float RepWin = 2f, RepDrama = 4f, RepLeagueTitle = 60f, RepCupTitle = 90f;
    private const float CupWinPrize = 120f, CupSemiPrize = 40f;   // 컵 우승 상금 / 4강 진출 상금

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
        public string LudusId = "PLAYER";                           // 소속 루두스(라이벌 경쟁)
        public int Division = 1;                                    // 1부(콜로세움)/2부(투기장) — 명성 랭킹 배치, 시즌말 승강
        public int Age, AgingStartAge;
        public int TrainingPoints, MatchCounter;                    // 3경기 주기 훈련
        public int CW, CL, CD, CKoW; public float Fame, Popularity;
        public int W, L, D, Streak;
        public int Fatigue, InjuryMatches;                          // 피로도 0(쌩쌩)~100(탈진,메타)·부상 잔여 경기(스탯 영향)
        public int SeasonBrutals;                                   // 이번 시즌 격전(KO패·빈사) 횟수 — 극적 운명 게이트
        public int MGrit, MRecover, MShow, MPay;                    // 마스터리(0~5) — 투혼·회복력·흥행·협상 (비스탯, 메타 전용)
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
        int W, int L, int D, int Streak, string[] PendingEmotions,
        int Fatigue = 0, int InjuryMatches = 0, string LudusId = "PLAYER", int Division = 1, int SeasonBrutals = 0,
        int MGrit = 0, int MRecover = 0, int MShow = 0, int MPay = 0);
    private sealed record SchedRec(int Round, string A, string B, bool IsEvent, float Score, string Kind = "regular",
        string Format = "normal");   // 특수 형식: execution(처형전) / same:WPN_x(무기 지정전)
    private sealed record WorldV2(int SchemaVer, int ConstantsVer, ulong WorldSeed, float Gold,
        int GachaCount, int FreeGachas, int TrainingLv, int MedicalLv, int QuartersLv, int SeasonsPlayed,
        bool SeasonActive, int SeasonNo, int MatchIdx, int Cursor, bool EventsAppended,
        List<SchedRec>? Schedule, List<StoryDoc>? Story, List<EventDoc>? Events,
        List<GladRec> Gladiators, List<GladRec>? Candidates, List<RelationLedger.Entry> Relations,
        List<LogEntry>? MatchLog = null, SeasonSummaryDoc? LastSummary = null,
        List<ChampionRec>? Champions = null, List<HallRec>? Hall = null,
        float LudusRep = 0f, List<string>? Achievements = null,       // 커리어 목표(A)
        List<string>? CupSeeds = null, int CupStage = 0, string? CupChampion = null,
        string? PendingEventId = null, string? PendingEventFighter = null,   // 시즌 중 텍스트 이벤트(2b)
        List<LudusRepRec>? RivalReps = null,   // 라이벌 루두스 명성(경쟁 메타)
        float Glory = 0f,   // 영광 하드 화폐
        string? PendingProposalOpp = null,   // 빅매치 제안 대기 상대
        string? LudusName = null,   // 라니스타가 지은 루두스 이름
        string? Mentor = null,   // 루두스의 스승(은퇴 전설 — 혈통 유산)
        List<LudusRepRec>? Perks = null,   // 제국 특전(Id, 레벨)
        int RookieSeq = 0, float Debt = 0f, int SparCount = 0);   // 신인 시리얼·사채·스파링 카운터
    private sealed record LudusRepRec(string Id, float Rep);

    // ── season.json / API 문서 ──
    private sealed record EventDoc(string A, string B, float Score, string Winner, bool Ko);
    private sealed record FighterDoc(string Id, string Name, string Weapon, string Tactic, string Personality, int Age,
        int W, int L, int D, int Points, int Streak, int CW, int CL, int CD, float Fame, float Popularity, bool IsPlayer,
        string[]? Epithets = null, int Fatigue = 0, bool Injured = false, int Division = 1);
    private sealed record RelDoc(string Self, string Opp, string Type, float Affinity, int Wins, int Losses);
    private sealed record StoryDoc(int Round, string Kind, string Text);
    private sealed record SeasonDoc(int SchemaVer, int SeasonNo, int Rounds, int Matches, int TotalMatches, bool Completed,
        string? NextA, string? NextB, bool NextIsEvent, string Champion,
        List<FighterDoc> Fighters, List<RelDoc> Relations, List<EventDoc> Events, List<StoryDoc> Story,
        List<MatchLogDoc> MatchLog, List<ChampionRec>? Champions = null, List<HallRec>? Hall = null);

    private sealed record StatsDoc(float Atk, float Def, float Hp, float Spd, float Aspd, float Rct);
    private sealed record MyFighterDoc(string Id, string Name, string Weapon, string Personality, int Age, bool Aging,
        string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] TacticPool, string Tactic, int TrainingPoints,
        int W, int L, int D, int CW, int CL, int CD, float Fame, float Popularity,
        string[] Emotions,    // 다음 경기에 실릴 감정 (💭 예고)
        string[]? Epithets = null,    // 획득 이명
        int Fatigue = 0, bool Injured = false,   // 피로도(0쌩쌩~100탈진)·부상 여부
        bool AtCap = false, int BreakthroughCost = 0,   // 상한 도달·잠재력 돌파 비용(영광)
        int MGrit = 0, int MRecover = 0, int MShow = 0, int MPay = 0);   // 마스터리 레벨
    private sealed record CandidateDoc(int Idx, string Name, string Weapon, string Personality, string RevealedTactic); // 마스킹!
    private sealed record OppPreview(string Name, string Weapon, string Personality, int Age, float Fame, float Popularity, string Career);
    private sealed record NextMatchDoc(int Round, bool IsEvent, bool IsPlayerMatch,
        string AName, string BName, string? MyId, string? MyName, string[]? MyPool, string? MyTactic, OppPreview? Opp,
        string? MyVsOpp = null,       // 이 상대와의 상대전적 "2승 1패"
        string? MyRelation = null,    // 내가 상대를 보는 관계 (원수/공포/라이벌…) — 복수전 예고
        string[]? MyEmotions = null,  // 이번 경기에 실리는 감정
        bool OppIsKiter = false,      // 상성 힌트: 상대가 장거리 카이터인가
        string? Stage = null,         // 컵 단계 라벨 (4강 결승) — 정규경기는 null
        float MyWinPct = 50f, float MyOdds = 2f, float OppOdds = 2f,   // 배당(파워 모델 — 표시용)
        bool CrowdFavorsMe = false, float Hype = 0f);   // 군중 선호(인기)·흥행지수
    private sealed record LudusDoc(float Rep, int Tier, string TierName, string? NextTierName, float NextTierRep, float IncomeMult);
    private sealed record AchDoc(string Id, string Name, string Desc, bool Unlocked);
    private sealed record CupMatchDoc(string Stage, string A, string B, string? Winner);
    private sealed record LudusStandingDoc(string Name, float Rep, string TierName, int Members,
        string? TopFighter, int SeasonW, int SeasonL, int SeasonD, bool IsPlayer);
    private sealed record RelRow(string OppName, string RelName, string RelIcon, int W, int L, int Enc, bool OppIsMine);
    private sealed record FighterProfileDoc(string Id, string Name, string Weapon, string Personality, int Age,
        bool IsPlayer, bool Aging, string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] Epithets, string[] TacticPool, string Tactic,
        int W, int L, int D, int CW, int CL, int CD, int CKoW, int Titles, float Fame, float Popularity,
        RelRow[] Relations, string[] Emotions, string[] Chronicle, int Fatigue, bool Injured, string Ludus);
    private sealed record GameStateDoc(SeasonDoc Season, float Gold, int FreeGachas, float GachaCost,
        int TrainingLv, int MedicalLv, int QuartersLv, int RosterCap, bool SeasonActive,
        List<MyFighterDoc> MyFighters, List<CandidateDoc> Candidates, NextMatchDoc? NextMatch,
        SeasonSummaryDoc? LastSeason, LudusDoc Ludus, List<AchDoc> Achievements, List<CupMatchDoc>? Cup,
        TextEventDoc? PendingEvent, List<LudusStandingDoc> LudusTable, float Glory, ProposalDoc? PendingProposal,
        string LudusName = "내 루두스", string? Mentor = null, List<PerkDoc>? Perks = null,
        float Debt = 0f, string RomanDate = "");   // 사채·로마력 날짜(시간감각)
    private sealed record PerkDoc(string Id, string Name, string Desc, int Lv, int Max, int NextCost);

    // ── 로마력(시간감각): 시즌1 = AUC 681(기원전 73년, 스파르타쿠스 봉기의 해). 경기 시즌 = Martius~October. ──
    private static readonly string[] RomanMonths = { "Martius", "Aprilis", "Maius", "Iunius", "Quinctilis", "Sextilis", "September", "October" };
    private string RomanDate()
    {
        int auc = 680 + Math.Max(1, _seasonNo);
        if (!SeasonActive) return $"AUC {auc} · Ianuarius (프리시즌)";
        int total = Math.Max(1, _schedule.Count);
        float f = Math.Clamp((float)_cursor / total, 0f, 1f);
        int dayOfSeason = (int)(f * 239f);                    // 8개월 × ~30일
        int m = Math.Min(RomanMonths.Length - 1, dayOfSeason / 30);
        return $"AUC {auc} · {RomanMonths[m]} {dayOfSeason % 30 + 1}일";
    }

    /// <summary>내 선수의 경기 후 변경사항 (결과 화면용 — 성장·재화·인기·명성 델타).</summary>
    public sealed record MyDelta(string Name, bool Won, bool Draw, float Income, string IncomeNote,
        float FameDelta, float PopDelta, string? GrowthAxis, int TrainingGained, string? Emotion);

    /// <summary>PlayNext 요약 (/api/next 응답).</summary>
    public sealed record MatchSummary(int SeasonNo, int Round, bool IsEvent, string A, string B,
        string Winner, string Reason, bool SeasonCompleted, bool NewSeasonStarted, bool WasPlayerMatch,
        float Income, string IncomeNote, List<MyDelta>? Mine = null, List<string>? Fates = null);

    /// <summary>경기 로그 1건 — 당시 선수 스냅샷 + 시드 = 결정론 재관전([2] ERD FighterSnapshot 원칙).</summary>
    private sealed record LogEntry(int Idx, int Round, bool IsEvent, string AId, string BId, string AName, string BName,
        string Winner, string Reason, bool IsPlayerMatch, ulong Seed, FighterDef DefA, FighterDef DefB);
    private sealed record MatchLogDoc(int Idx, int Round, bool IsEvent, string A, string B, string Winner, string Reason, bool IsPlayerMatch);

    /// <summary>시즌 종료 요약 (연출 화면용 — 프리시즌 동안 표시, 영속).</summary>
    private sealed record RankRow(int Rank, string Name, int W, int L, int D, int Points, bool IsPlayer);
    private sealed record SeasonSummaryDoc(int SeasonNo, string Champion, bool ChampionIsMine, List<RankRow> Standings,
        int MyBestRank, float RankBonus, float Salary, float GoldAfter,
        List<string> AgingNotes, int Revenge, int Upsets, int Comebacks, string TopFame,
        List<string>? Retirements = null,
        string? CupChampion = null, bool CupChampionMine = false, List<string>? NewAchievements = null);

    /// <summary>세계 역사 — 역대 챔피언·명예의 전당(은퇴자) 영속 기록.</summary>
    private sealed record ChampionRec(int SeasonNo, string Name, string Record, bool IsPlayer);
    private sealed record HallRec(string Name, string Weapon, float Fame, string Career, int Age, int RetiredSeason, bool IsPlayer);

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
    private readonly List<LogEntry> _matchLog = new();   // 이번 시즌 경기 기록(스냅샷+시드 = 재관전)
    private SeasonSummaryDoc? _lastSummary;              // 최근 종료 시즌 요약 (연출 화면, 영속)
    private readonly List<ChampionRec> _champions = new();   // 역대 챔피언 (세계 역사)
    private readonly List<HallRec> _hall = new();            // 명예의 전당 (은퇴자)
    private float _ludusRep;                                 // 루두스 등급 명성 (A)
    private float _glory;                                    // 영광(하드 화폐) — 위신 업적에서만, 잠재력 돌파 등에 소모
    private string _ludusName = "내 루두스";                 // 라니스타가 직접 명명 가능
    private readonly HashSet<string> _achievements = new();  // 달성 업적 id
    private List<string> _cupSeeds = new();                  // 컵 시드 (top4 id, 컵 시작 시)
    private int _cupStage;                                   // 0=미시작 1=4강편성 2=결승편성 3=종료
    private string? _cupChampion;                            // 최근 컵 우승자 이름
    private string? _pendingEventId, _pendingEventFighter;   // 시즌 중 텍스트 이벤트(2b) — 선택 대기
    private string? _pendingProposalOpp;                     // 빅매치 제안(감독 개입) — 출전 선택 대기 상대 id
    private readonly List<string> _lastFates = new();        // 직전 경기의 극적 운명(결과 화면 표시용)
    private int _rookieSeq;                                  // 신인 id 시리얼(중복 방지, 영속)
    private float _debt;                                     // 사채(이벤트 빚) — 시즌말 이자·상환·명성 압박
    private readonly Dictionary<string, float> _rivalRep = new();   // 라이벌 루두스별 명성(경쟁 순위표)
    private int _emoGen;

    // ── 업적 정의 (조건은 코드에서 체크) ──
    private static readonly (string Id, string Name, string Desc)[] AchievementDefs =
    {
        ("first_win",    "첫 승리",       "내 검투사의 첫 승"),
        ("first_title",  "리그 제패",     "리그 시즌 우승"),
        ("first_cup",    "챔피언십 정복", "챔피언십 컵 우승"),
        ("caesar",       "카이사르 발굴", "카이사르 천부 영입"),
        ("legend",       "살아있는 전설", "내 검투사 명성 100 돌파"),
        ("streak10",     "무패의 투사",   "내 검투사 10연승"),
        ("empire",       "제국의 정점",   "루두스 최고 등급 달성"),
        ("dynasty",      "왕조",          "리그 3연패"),
    };

    private readonly List<string> _seasonNewAch = new();   // 이번 시즌 신규 업적 (요약용)

    private int LudusTier()
    {
        int t = 0;
        for (int i = 0; i < LudusTiers.Length; i++) if (_ludusRep >= LudusTiers[i].Rep) t = i;
        return t;
    }
    private float IncomeMult => _playerless ? 1f : 1f + LudusTier() * 0.08f + 0.05f * PerkLv("patron");   // 등급 + 제국 후원 특전
    private void AddRep(float r) { if (!_playerless) _ludusRep += r; }
    private void AddRivalRep(string ludusId, float r)   // 라이벌 루두스 명성 누적(경쟁)
    {
        if (ludusId == PlayerLudusId) return;
        _rivalRep[ludusId] = MathF.Max(0f, _rivalRep.GetValueOrDefault(ludusId) + r);
    }
    private static string TierNameForRep(float rep)
    {
        int t = 0;
        for (int i = 0; i < LudusTiers.Length; i++) if (rep >= LudusTiers[i].Rep) t = i;
        return LudusTiers[t].Name;
    }

    // ── 영광(하드 화폐) — 위신 업적에서만. 잠재력 돌파 등에 소모. ──
    private const float GloryLeagueTitle = 12f, GloryCup = 18f, GloryPromote = 6f, GloryAchievement = 4f, GloryUpset = 1f;
    private void AddGlory(float g) { if (!_playerless) _glory += g; }
    /// <summary>잠재력 돌파 비용 — 현재 상한이 클수록 비쌈(무한 인플레 방지, 체감 감소).</summary>
    private int BreakthroughCost(Gladiator g) => (int)MathF.Ceiling(g.PotentialBudget / 40f);
    /// <summary>루두스 순위표(내 루두스 + 라이벌 검투소들, 명성 내림차순) — 경쟁 메타.</summary>
    private List<LudusStandingDoc> BuildLudusTable()
    {
        var byLudus = _cast.GroupBy(g => g.LudusId).ToDictionary(x => x.Key, x => x.ToList());
        var list = new List<LudusStandingDoc>();
        void Add(string id, float rep, bool isPlayer)
        {
            var m = byLudus.GetValueOrDefault(id) ?? new();
            var top = m.OrderByDescending(x => x.Fame).FirstOrDefault();
            list.Add(new LudusStandingDoc(LudusNameOf(id), MathF.Round(rep), TierNameForRep(rep),
                m.Count, top?.Name, m.Sum(x => x.W), m.Sum(x => x.L), m.Sum(x => x.D), isPlayer));
        }
        if (!_playerless) Add(PlayerLudusId, _ludusRep, true);
        foreach (var r in ActiveRivalLudi) Add(r.Id, _rivalRep.GetValueOrDefault(r.Id), false);
        return list.OrderByDescending(x => x.Rep).ThenByDescending(x => x.IsPlayer).ToList();
    }
    private void Unlock(string id)
    {
        if (_playerless || !_achievements.Add(id)) return;   // 이미 달성/CLI
        var def = AchievementDefs.First(a => a.Id == id);
        _seasonNewAch.Add(def.Name);
        _story.Add((0, "achievement", $"🏅 업적 — {def.Name}: {def.Desc}"));
        _ludusRep += 20f; AddGlory(GloryAchievement);
    }

    private int RosterCap => 3 + _quartersLv;
    private ulong SeasonSeed => _worldSeed + (ulong)_seasonNo * 1000003UL;
    private Gladiator ById(string id) => _cast.First(g => g.Id == id);
    private string PersOf(string id) => ById(id).PersonalityId;
    private int TitlesOf(Gladiator g) => _champions.Count(c => c.Name == g.Name);

    /// <summary>획득 이명 — 통산 전적·KO·연승·우승·연륜에서 파생(저장 안 함, 읽을 때 계산). 넴시스 서사의 표지.</summary>
    private string[] Epithets(Gladiator g)
    {
        var e = new List<string>();
        int games = g.CW + g.CL + g.CD, titles = TitlesOf(g);
        if (titles >= 3) e.Add("👑 패왕");
        else if (titles >= 1) e.Add("👑 챔피언");
        if (g.CL == 0 && g.CW >= 5) e.Add("🛡 불패");
        if (g.Streak >= 6) e.Add("⚡ 파죽지세");
        if (g.CKoW >= 4 && g.CKoW * 2 >= Math.Max(1, g.CW)) e.Add("💀 처형자");
        if (g.Fame >= 120f) e.Add("🌟 전설");
        if (g.Popularity >= 60f) e.Add("🎭 군중의 연인");
        if (g.Age >= 34 || games >= 40) e.Add("⚔ 백전노장");
        if (games == 0) e.Add("🌱 신예");
        return e.Take(3).ToArray();
    }

    /// <summary>선수 상세(서사) — 이명·관계·감정·연대기. 기존 데이터 파생, 스키마 무변경.</summary>
    public string ProfileJson(string id)
    {
        var g = _cast.FirstOrDefault(x => x.Id == id);
        if (g == null) return Err("선수를 찾을 수 없다");

        var rels = _ledger.Snapshot().Where(x => x.Self == g.Id && x.Encounters > 0)
            .Select(x => (x, type: _ledger.Get(g.Id, x.Opp).Classify(g.PersonalityId)))
            .Where(t => t.type is { })
            .OrderByDescending(t => RelationTable.Get(t.type!.Value).DramaWeight * (1 + t.x.Encounters))
            .Take(6)
            .Select(t => { var rd = RelationTable.Get(t.type!.Value);
                var icon = t.type switch { RelationType.Nemesis => "⚔", RelationType.Fear => "😨",
                    RelationType.Rival => "🔥", RelationType.Obsession => "🌀", RelationType.Envy => "😤",
                    RelationType.Respect => "🤝", RelationType.Friend => "🫂", _ => "🔗" };
                var opp = _cast.FirstOrDefault(c => c.Id == t.x.Opp);
                return new RelRow(opp?.Name ?? t.x.Opp, rd.Name, icon, t.x.Wins, t.x.Losses, t.x.Encounters,
                    opp?.IsPlayer ?? false); })
            .ToArray();

        // 연대기: 통산 우승 이력(영속) + 현 시즌 이 선수가 등장한 서사
        var chron = new List<string>();
        foreach (var c in _champions.Where(c => c.Name == g.Name))
            chron.Add($"🏆 시즌 {c.SeasonNo} 리그 챔피언 ({c.Record})");
        foreach (var s in _story.Where(s => s.Text.Contains(g.Name) && s.Kind is "revenge" or "upset" or "comeback" or "cup").TakeLast(6))
            chron.Add(s.Text);

        var doc = new FighterProfileDoc(g.Id, g.Name, g.WeaponId.Replace("WPN_", ""), g.PersonalityId.Replace("PER_", ""),
            g.Age, g.IsPlayer, g.Age >= g.AgingStartAge,
            ViewerExport.TalentName(g.Talent), ViewerExport.PotentialName(g.Potential),
            MathF.Round(g.PotentialBudget), MathF.Round(BudgetUsed(g.Stats)),
            new StatsDoc(MathF.Round(g.Stats.Atk), MathF.Round(g.Stats.Def), MathF.Round(g.Stats.HpMax),
                         MathF.Round(g.Stats.Spd), MathF.Round(g.Stats.Aspd), MathF.Round(g.Stats.Rct)),
            g.TraitIds.Select(t => TraitTable.Get(t).Name).ToArray(), Epithets(g),
            g.TacticPool.Select(t => t.Replace("TAC_", "")).ToArray(), g.TacticId.Replace("TAC_", ""),
            g.W, g.L, g.D, g.CW, g.CL, g.CD, g.CKoW, TitlesOf(g), MathF.Round(g.Fame), MathF.Round(g.Popularity),
            rels, g.PendingEmotions.Select(x => EmotionTable.Get(x).Name).ToArray(), chron.ToArray(),
            g.Fatigue, g.InjuryMatches > 0, LudusNameOf(g.LudusId));
        return JsonSerializer.Serialize(doc, JsonOpts);
    }

    // ── 시즌 중 텍스트 이벤트(2b) — 감독의 선택. 효과는 전부 기존 메커니즘(재화·명성·인기·훈련·감정·스탯). ──
    private sealed record TextEventDoc(string Id, string Icon, string Title, string Body, string[] Choices);
    private sealed record ProposalPickDoc(string Id, string Name, string Weapon, string Personality, int Fatigue, bool Injured);
    private sealed record ProposalDoc(string OppName, string OppWeapon, string OppPersonality, int OppAge, float OppFame,
        string OppCareer, ProposalPickDoc[] Roster);
    private sealed class EvtTemplate
    {
        public required string Id, Icon, Title;
        public required bool NeedsFighter;
        public required Func<string, string> Body;                       // 대상 이름 → 본문
        public required (string Label, Func<Gladiator?, string> Apply)[] Choices;
    }

    /// <summary>스탯을 상한(잠재력 버짓) 내에서 영구 조정 — 여유 없으면 훈련 포인트로 환급. axis: Atk/Def/Rct.</summary>
    private string NudgeStat(Gladiator g, string axis, float amt)
    {
        if (BudgetUsed(g.Stats) + amt > g.PotentialBudget) { g.TrainingPoints += 1; return "상한이 꽉 차 훈련 포인트로 전환"; }
        int idx = axis switch { "Atk" => 0, "Def" => 1, "Rct" => 5, _ => 0 };
        g.Stats = WithAxis(g.Stats, idx, amt);
        return $"{axis} +{amt:F0}";
    }

    /// <summary>이벤트 지불: 골드가 부족해도 거래는 성사된다 — 부족분은 사채(원금 1.5배)로. 빚은 시즌말 이자·명성 압박.</summary>
    private string SpendOrDebt(float cost)
    {
        if (_gold >= cost) { _gold -= cost; return $"골드 −{cost:F0}"; }
        float shortfall = cost - _gold; _gold = 0f;
        _debt += shortfall * 1.5f;
        _story.Add((0, "debt", $"💸 사채 — 부족분 {shortfall:F0}을 빚으로 (원금 1.5배 기록, 채무 {_debt:F0})"));
        return $"골드 바닥 → 부족분 {shortfall:F0} 사채(채무 {_debt:F0})";
    }

    private List<EvtTemplate> EvtTemplates() => new()
    {
        new EvtTemplate { Id = "training", Icon = "🏋", Title = "혹독한 훈련", NeedsFighter = true,
            Body = n => $"{n}이(가) 한계를 넘는 훈련을 자청한다. 몸을 갈아 실력을 얻을 것인가, 팬 앞의 몸 상태를 지킬 것인가.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("강행군 (훈련 포인트 +2, 인기 −5)", g => { g!.TrainingPoints += 2; g.Popularity = MathF.Max(0, g.Popularity - 5); return $"{g.Name} 훈련 포인트 +2, 인기 −5"; }),
                ("휴식 (인기 +5)", g => { g!.Popularity += 5; return $"{g.Name} 인기 +5"; }) } },

        new EvtTemplate { Id = "patron", Icon = "💰", Title = "후원자의 제안", NeedsFighter = false,
            Body = _ => "부유한 후원자가 두둑한 금화를 내밀며 루두스의 이름을 빌리려 한다. 실리인가 명예인가.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받는다 (골드 +80, 명성 −15)", _ => { _gold += 80f; _ludusRep = MathF.Max(0, _ludusRep - 15f); return "골드 +80, 루두스 명성 −15"; }),
                ("거절한다 (명성 +20)", _ => { AddRep(20f); return "루두스 명성 +20"; }) } },

        new EvtTemplate { Id = "crowd", Icon = "🎭", Title = "군중의 갈망", NeedsFighter = true,
            Body = n => $"관중이 {n}의 화끈한 경기를 원한다. 흥행에 응할 것인가, 실속을 챙길 것인가.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("응한다 (인기 +12, 다음 경기 흥분)", g => { g!.Popularity += 12f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated); return $"{g.Name} 인기 +12, 다음 경기 '동기부여'"; }),
                ("침착하게 (명성 +8)", g => { g!.Fame += 8f; return $"{g.Name} 명성 +8"; }) } },

        new EvtTemplate { Id = "taunt", Icon = "😤", Title = "라이벌의 조롱", NeedsFighter = true,
            Body = n => $"타 검투사가 {n}을(를) 공개적으로 조롱했다. 응수할 것인가, 검으로 답할 것인가.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("맞받아친다 (인기 +6, 다음 경기 원한)", g => { g!.Popularity += 6f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Grudge); return $"{g.Name} 인기 +6, 다음 경기 '원한'"; }),
                ("무시한다 (명성 +6)", g => { g!.Fame += 6f; return $"{g.Name} 명성 +6"; }) } },

        new EvtTemplate { Id = "mentor", Icon = "📜", Title = "노장의 지도", NeedsFighter = true,
            Body = n => $"은퇴한 전설이 {n}을(를) 지도하겠다 한다. 사례가 필요하지만 기예가 는다.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("수련한다 (골드 −40 · 부족분은 빚)", g => { var pay = SpendOrDebt(40f); var r = NudgeStat(g!, "Rct", 3f); return $"{pay}, {g!.Name} {r}"; }),
                ("사양한다", g => "정중히 사양했다.") } },

        new EvtTemplate { Id = "blackmarket", Icon = "🗡", Title = "암시장 무기상", NeedsFighter = true,
            Body = n => $"뒷골목 상인이 {n}에게 은밀히 예리한 검을 제안한다. 이점인가 명예인가.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("산다 (골드 −60 · 부족분은 빚)", g => { var pay = SpendOrDebt(60f); var r = NudgeStat(g!, "Atk", 3f); return $"{pay}, {g!.Name} {r}"; }),
                ("정직하게 (명성 +10)", g => { AddRep(10f); return "루두스 명성 +10"; }) } },
    };

    /// <summary>플레이어 경기 후 확률적으로 이벤트 스폰(결정론 — 시드 파생). 대상=방금 싸운 내 선수.</summary>
    private void MaybeSpawnEvent(Gladiator? subject)
    {
        if (_pendingEventId != null || subject == null || !subject.IsPlayer) return;
        var rng = new SimRandom(SeasonSeed ^ 0xE7E7_0A11UL + (ulong)_matchIdx * 131UL);
        if (!rng.Roll(0.22f)) return;                             // ~22% 발생
        var pool = EvtTemplates();
        var t = pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
        _pendingEventId = t.Id;
        _pendingEventFighter = t.NeedsFighter ? subject.Id : null;
    }

    private TextEventDoc? PendingEventDoc()
    {
        if (_pendingEventId == null) return null;
        var t = EvtTemplates().FirstOrDefault(x => x.Id == _pendingEventId);
        if (t == null) return null;
        string nm = _pendingEventFighter != null ? (_cast.FirstOrDefault(g => g.Id == _pendingEventFighter)?.Name ?? "선수") : "";
        return new TextEventDoc(t.Id, t.Icon, t.Title, t.Body(nm), t.Choices.Select(c => c.Label).ToArray());
    }

    private ProposalDoc? PendingProposalDoc()
    {
        if (_pendingProposalOpp == null) return null;
        var o = _cast.FirstOrDefault(g => g.Id == _pendingProposalOpp);
        if (o == null) return null;
        var roster = _cast.Where(g => g.IsPlayer).Select(g => new ProposalPickDoc(g.Id, g.Name,
            g.WeaponId.Replace("WPN_", ""), g.PersonalityId.Replace("PER_", ""), g.Fatigue, g.InjuryMatches > 0)).ToArray();
        return new ProposalDoc(o.Name, o.WeaponId.Replace("WPN_", ""), o.PersonalityId.Replace("PER_", ""),
            o.Age, MathF.Round(o.Fame), $"{o.CW}-{o.CL}-{o.CD}", roster);
    }

    /// <summary>빅매치 제안에 응해 출전 선수를 선택 → 커서 위치에 전시(exhibition) 카드 삽입. 빈 id = 거절.</summary>
    public string PickProposalJson(string fighterId)
    {
        if (_pendingProposalOpp == null) return Err("대기 중인 제안이 없다");
        var opp = _cast.FirstOrDefault(g => g.Id == _pendingProposalOpp);
        if (string.IsNullOrEmpty(fighterId) || opp == null)   // 거절 또는 상대 소멸
        {
            _pendingProposalOpp = null; SaveWorld(); return StateJson();
        }
        var me = _cast.FirstOrDefault(g => g.Id == fighterId && g.IsPlayer);
        if (me == null) return Err("내 선수 아님");
        int round = SeasonActive && _cursor < _schedule.Count ? _schedule[_cursor].Round : _rounds + 1;
        _schedule.Insert(_cursor, new SchedRec(round, me.Id, opp.Id, true, 0f, "proposal"));   // 다음 경기로 삽입(전시)
        _story.Add((0, "proposal", $"🎤 빅매치 성사 — {me.Name} vs {opp.Name}(도전장)"));
        _pendingProposalOpp = null; SaveWorld();
        return StateJson();
    }

    /// <summary>이벤트 선택 적용 → 결과 문구. 대상 선수가 사라졌으면(방출 등) 이벤트 취소.</summary>
    public string ChooseEventJson(int choiceIdx)
    {
        var t = _pendingEventId == null ? null : EvtTemplates().FirstOrDefault(x => x.Id == _pendingEventId);
        if (t == null) return Err("대기 중인 이벤트가 없다");
        if (choiceIdx < 0 || choiceIdx >= t.Choices.Length) return Err("잘못된 선택");
        Gladiator? subj = _pendingEventFighter != null ? _cast.FirstOrDefault(g => g.Id == _pendingEventFighter) : null;
        if (t.NeedsFighter && subj == null) { _pendingEventId = _pendingEventFighter = null; SaveWorld(); return Err("대상 선수가 없다 — 이벤트 취소"); }
        string outcome = t.Choices[choiceIdx].Apply(subj);
        _story.Add((0, "event_choice", $"{t.Icon} {t.Title} — {outcome}"));
        _pendingEventId = _pendingEventFighter = null;
        SaveWorld();
        return JsonSerializer.Serialize(new { ok = true, title = t.Title, outcome }, JsonOpts);
    }

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

    // 12인 풀 — worldSeed가 6인을 선발(커리어마다 다른 캐스트 = 변칙성)
    private static readonly (string Id, string Name, string Wpn, string Per, string Sig)[] AiCastDef =
    {
        ("GLA_MAXIMUS", "막시무스",   "WPN_SWORD",      "PER_BOLD",        "TAC_PRESSURE"),
        ("GLA_SPARTA",  "스파르타쿠스", "WPN_AXE",        "PER_RECKLESS",    "TAC_BRAWLER"),
        ("GLA_CRIXUS",  "크릭수스",   "WPN_DUALBLADES", "PER_CRUEL",       "TAC_BRAWLER"),
        ("GLA_GANNICUS","가니쿠스",   "WPN_SPEAR",      "PER_CALM",        "TAC_COUNTER"),
        ("GLA_BARCA",   "바르카",     "WPN_WHIP",       "PER_OPPORTUNIST", "TAC_ZONER"),
        ("GLA_NAEVIA",  "나이비아",   "WPN_SHIELD",     "PER_HONORABLE",   "TAC_DEFENDER"),
        ("GLA_OENO",    "오이노마우스", "WPN_HAMMER",     "PER_CALM",        "TAC_DEFENDER"),
        ("GLA_AGRON",   "아그론",     "WPN_GREATSWORD", "PER_BOLD",        "TAC_PRESSURE"),
        ("GLA_DURO",    "두로",       "WPN_DUALBLADES", "PER_RECKLESS",    "TAC_GAMBLER"),
        ("GLA_CASTUS",  "카스투스",   "WPN_SWORD",      "PER_OPPORTUNIST", "TAC_HUNTER"),
        ("GLA_NEMETES", "네메테스",   "WPN_WHIP",       "PER_CRUEL",       "TAC_EVADER"),
        ("GLA_SALVIUS", "살비우스",   "WPN_SPEAR",      "PER_WARY",        "TAC_DECISION"),
    };

    private static readonly string[] RecruitNames =
    {
        "루푸스","펠릭스","카시우스","세베루스","티투스","옥타비우스","다리우스","발레리우스",
        "트라야누스","아우렐리우스","콤모두스","페르티낙스","알비누스","마크리누스","고르디아누스","필리푸스",
        "데키우스","갈루스","플라비우스","루키우스","퀸투스","세르비우스","아피우스","호라티우스",
    };

    // 라이벌 루두스 — AI 검투사가 소속된 경쟁 검투소(명성 순위표). 플레이어는 "PLAYER".
    private const string PlayerLudusId = "PLAYER";
    private string PlayerLudusName => "★ " + _ludusName;   // 라니스타 명명 반영
    // 6종 풀 — worldSeed가 3곳을 선발(커리어마다 다른 경쟁 구도)
    private static readonly (string Id, string Name)[] RivalLudiPool =
    {
        ("LUD_BATIATUS", "바티아투스 검투소"),
        ("LUD_SOLONIUS", "솔로니우스 양성소"),
        ("LUD_CRASSUS",  "크라수스 투기장"),
        ("LUD_GLABER",   "글라베르 원형경기장"),
        ("LUD_COSSUTIUS","코수티우스 검투단"),
        ("LUD_OVIDIUS",  "오비디우스 양성소"),
    };
    private string LudusNameOf(string id) => id == PlayerLudusId ? PlayerLudusName
        : RivalLudiPool.FirstOrDefault(r => r.Id == id).Name ?? id;
    /// <summary>이 세계에 실존하는 라이벌 루두스(캐스트 소속 + 명성 기록 보유) — 풀 순서 유지.</summary>
    private IEnumerable<(string Id, string Name)> ActiveRivalLudi =>
        RivalLudiPool.Where(r => _rivalRep.ContainsKey(r.Id) || _cast.Any(g => g.LudusId == r.Id));

    private void CreateAiCast()
    {
        var rng = new SimRandom(_worldSeed ^ 0xCA57_CA57UL);
        var picks = AiCastDef.OrderBy(_ => rng.NextUInt64()).Take(6).ToList();          // 12인 풀 → 6인
        var ludi = RivalLudiPool.OrderBy(_ => rng.NextUInt64()).Take(3).ToList();       // 6종 풀 → 3곳
        int i = 0;
        foreach (var (id, name, wpn, per, sig) in picks)
        {
            var g = RollGladiator(rng, id, name, wpn, per, sigTactic: sig, isPlayer: false,
                                  ageMin: 20, ageMax: 28);
            g.LudusId = ludi[i / 2 % ludi.Count].Id;   // 2명씩 3개 라이벌 루두스로 편성
            _cast.Add(g);
            i++;
        }
        foreach (var r in ludi) _rivalRep.TryAdd(r.Id, 0f);
    }

    /// <summary>선수 1명 롤: 천부/잠재력(StatGen) + 특성(TraitGen) + 전술풀 3종 + 나이/노화 시작 나이.
    /// talentRolls>1이면 천부를 여러 번 굴려 버짓 최대치를 취함(루두스 스카우팅 안목 — 등급이 높을수록 좋은 원석).</summary>
    private static Gladiator RollGladiator(SimRandom rng, string id, string name, string wpn, string per,
                                           string? sigTactic, bool isPlayer, int ageMin, int ageMax, int talentRolls = 1)
    {
        var end = StatGen.Roll(rng);
        for (int r = 1; r < talentRolls; r++)
        {
            var alt = StatGen.Roll(rng);
            if (alt.TalentBudget > end.TalentBudget) end = alt;   // 더 나은 원석 채택
        }
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

    /// <summary>AI 신인 생성(공통) — 전임자의 검투소·디비전을 승계해 리그 구조 유지. id는 시리얼로 유일.</summary>
    private Gladiator SpawnRookieCore(SimRandom rng, string ludus, int div, int talentRolls)
    {
        var used = _cast.Select(g => g.Name).ToHashSet();
        var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
        var pers = PersonalityTable.All.Select(p => p.Id).ToArray();
        var rk = RollGladiator(rng, $"GLA_NS{_rookieSeq++}", PickName(rng, used),
            wpns[(int)(rng.NextFloat01() * wpns.Length)], pers[(int)(rng.NextFloat01() * pers.Length)],
            sigTactic: null, isPlayer: false, ageMin: 18, ageMax: 24, talentRolls: talentRolls);
        rk.LudusId = ludus; rk.Division = div;
        _cast.Add(rk);
        return rk;
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

    // ── 디비전(승강제) — 첫 시즌만 명성으로 초기 배치. 이후 승강은 '시즌 성적'으로(FinalizeSeason의 SwapDivisions).
    //    챔피언(=1부 승점 1위)은 정의상 강등 불가 — 통산 명성 재배치로 우승자가 강등되던 결함 수정. ──
    private static string DivName(int d) => d == 1 ? "1부 콜로세움" : "2부 투기장";
    private void AssignDivisions()
    {
        var ranked = _cast.OrderByDescending(g => g.Fame).ThenByDescending(g => g.CareerPoints).ThenBy(g => g.Id).ToList();
        int topSize = (ranked.Count + 1) / 2;   // 1부 = 상위 절반(홀수면 1부가 큼)
        for (int i = 0; i < ranked.Count; i++) ranked[i].Division = i < topSize ? 1 : 2;
    }

    /// <summary>시즌말 승강(성적 기반): 1부 최하위 2명 ⇄ 2부 상위 2명. 시즌 순위가 살아있을 때 실행.</summary>
    private void SwapDivisions()
    {
        var d1 = Standings(1); var d2 = Standings(2);
        int n = Math.Min(2, Math.Min(d1.Count - 1, d2.Count));   // 1부에 최소 1명은 남긴다
        for (int i = 0; i < n; i++)
        {
            var down = d1[^(i + 1)]; var up = d2[i];
            down.Division = 2; up.Division = 1;
            _story.Add((_rounds + 1, "relegate", $"⬇ 강등 — {down.Name}({down.W}승 {down.L}패) → {DivName(2)}"));
            _story.Add((_rounds + 1, "promote", $"⬆ 승격 — {up.Name}({up.W}승 {up.L}패) → {DivName(1)}"));
            if (up.IsPlayer) AddGlory(GloryPromote);   // 승격 = 위신
        }
    }

    /// <summary>부 인원 편차 보정(영입·은퇴·방출로 어긋났을 때) — 명성 하위/상위를 이동.</summary>
    private void RebalanceDivisions()
    {
        while (true)
        {
            int c1 = _cast.Count(g => g.Division == 1), c2 = _cast.Count(g => g.Division == 2);
            if (c1 - c2 > 1) _cast.Where(g => g.Division == 1).OrderBy(g => g.Fame).First().Division = 2;
            else if (c2 - c1 > 1) _cast.Where(g => g.Division == 2).OrderByDescending(g => g.Fame).First().Division = 1;
            else break;
        }
    }

    /// <summary>파이트 카드 매치메이커 — 부 내에서 매 라운드 라이벌·랭킹근접·흥행 가중으로 대진 편성.
    /// 전원 라운드로빈 대신 큐레이션된 소수 카드(라이벌은 자주, 지루한 매치업은 덜). 결정론(시즌시드 파생).</summary>
    private void BuildDivisionCards(int div)
    {
        var pool = _cast.Where(g => g.Division == div).ToList();
        if (pool.Count < 2) return;
        int rounds = Math.Clamp(pool.Count, 3, 6);
        var rankOf = pool.OrderByDescending(g => g.Fame).ThenBy(g => g.Id)
                         .Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i);
        for (int r = 1; r <= rounds; r++)
        {
            var rng = new SimRandom(SeasonSeed ^ (0xCA5D_0000UL + (ulong)(div * 100 + r)));
            var avail = pool.OrderBy(_ => rng.NextUInt64()).ToList();   // 라운드별 결정론 셔플
            while (avail.Count >= 2)
            {
                var a = avail[0]; avail.RemoveAt(0);
                Gladiator best = avail[0]; float bestScore = float.MinValue;
                foreach (var b in avail)
                {
                    float sc = 1f
                        + _ledger.RivalryWeight(a.Id, b.Id, PersOf) * 2f          // 라이벌 우선
                        + (a.Popularity + b.Popularity) / 40f                     // 흥행
                        - MathF.Abs(rankOf[a.Id] - rankOf[b.Id]) * 0.3f           // 랭킹 근접
                        + (rng.NextUInt64() % 100) / 200f;                        // 노이즈
                    if (sc > bestScore) { bestScore = sc; best = b; }
                }
                avail.Remove(best);
                _schedule.Add(new SchedRec(r, a.Id, best.Id, false, 0f));
            }
        }
    }

    private void StartSeason()
    {
        _seasonNo = _seasonsPlayed + 1;
        _matchIdx = 0; _emoGen = 0; _cursor = 0; _eventsAppended = false;
        _cupStage = 0; _cupSeeds = new(); _cupChampion = null; _seasonNewAch.Clear();
        _story.Clear(); _eventDocs.Clear(); _schedule.Clear(); _matchLog.Clear();
        SeasonActive = true;
        foreach (var g in _cast) { g.W = g.L = g.D = g.Streak = 0; g.PendingEmotions.Clear(); g.Fatigue = 0; g.InjuryMatches = 0; g.SeasonBrutals = 0; }   // 시즌 사이 휴식 = 완전 회복
        if (_seasonNo == 1) AssignDivisions();   // 초기 배치만 명성 — 이후 승강은 시즌말 성적 스왑
        else RebalanceDivisions();               // 영입·은퇴로 어긋난 인원만 보정

        // 파이트 카드: 부별로 라이벌·랭킹근접·흥행 가중 카드 편성(전원 라운드로빈 대신 큐레이션)
        BuildDivisionCards(1);
        BuildDivisionCards(2);
        int d1 = _cast.Count(g => g.Division == 1);
        _story.Add((0, "season", $"🏛 시즌 {_seasonNo} 개막 — {DivName(1)} {d1}인 · {DivName(2)} {_cast.Count - d1}인"));

        // 빅매치 제안(감독 개입): 내 선수 2명+ & 결정론 확률 → 명망 있는 도전 상대. 감독이 누구를 내보낼지 선택.
        _pendingProposalOpp = null;
        if (!_playerless && _cast.Count(g => g.IsPlayer) >= 2 && new SimRandom(SeasonSeed ^ 0x0B16_A7C4UL).Roll(0.6f))
            _pendingProposalOpp = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault()?.Id;
    }

    private void FinalizeSeason()
    {
        SeasonActive = false;
        _seasonsPlayed = _seasonNo;
        var standings = Standings(1);                       // 리그 챔피언 = 1부 우승자
        var champ = standings[0];
        var d2 = Standings(2);
        _story.Add((_rounds + 1, "season", $"🏆 시즌 {_seasonNo} 종료 — {DivName(1)} 챔피언 {champ.Name} ({champ.W}승 {champ.L}패)"));
        if (d2.Count > 0) _story.Add((_rounds + 1, "season", $"🏆 {DivName(2)} 우승 — {d2[0].Name}"));

        // 시즌 순위 보너스 (내 최고 순위 기준) + 급여
        int bestRank = -1; float bonusPaid = 0f, salaryPaid = 0f;
        if (!_playerless && _cast.Any(g => g.IsPlayer))
        {
            // 종합 순위(1부 먼저·부내 승점순) 기준 — 내 선수가 2부여도 위치 반영
            var overall = _cast.OrderBy(g => g.Division).ThenByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W).ToList();
            int best = overall.FindIndex(g => g.IsPlayer);
            bestRank = best + 1;
            bonusPaid = best >= 0 && best < RankBonus.Length ? RankBonus[best] : 20f;
            _gold += bonusPaid;
            // 급여 공제 (스타는 비싸다)
            salaryPaid = _cast.Where(g => g.IsPlayer).Sum(g => SalaryBase + g.Fame * SalaryFameScale);
            _gold = MathF.Max(0f, _gold - salaryPaid);
            _story.Add((_rounds + 1, "season", $"💰 시즌 정산 — 순위 보너스 +{bonusPaid:F0} · 급여 −{salaryPaid:F0} (잔고 {_gold:F0})"));

            // 사채 정산: 이자 20% → 잔고에서 자동 상환 → 남으면 채권자의 압박(루두스 명성 −10)
            if (_debt > 0f)
            {
                _debt *= 1.2f;
                float pay = MathF.Min(_gold, _debt); _gold -= pay; _debt = MathF.Round(_debt - pay);
                if (_debt > 0.5f)
                {
                    _ludusRep = MathF.Max(0f, _ludusRep - 10f);
                    _story.Add((_rounds + 1, "debt", $"💸 채권자의 압박 — 이자 20% · 상환 {pay:F0} · 잔여 채무 {_debt:F0} · 루두스 명성 −10"));
                }
                else { _debt = 0f; _story.Add((_rounds + 1, "debt", $"💸 빚 청산 — {pay:F0} 상환, 채무에서 벗어났다")); }
            }
        }

        // 나이/노화: 시즌당 +1세, 노화 시작 후 잠재력 상한 점진 감소 (의무실은 내 선수만 감면)
        var agingNotes = new List<string>();
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
                    if (g.IsPlayer)
                    {
                        _story.Add((_rounds + 1, "aging", $"⏳ {g.Name}({g.Age}세) — 세월이 몸을 갉아먹는다 (상한 {g.PotentialBudget:F0})"));
                        agingNotes.Add($"{g.Name} ({g.Age}세) — 능력 하락, 상한 {g.PotentialBudget:F0}");
                    }
                }
                else if (g.IsPlayer) agingNotes.Add($"{g.Name} ({g.Age}세) — 노쇠 진행 중 (상한 {g.PotentialBudget:F0})");
            }
            g.Popularity *= g.IsPlayer ? MathF.Min(0.9f, 0.6f + 0.1f * PerkLv("tour")) : 0.6f;   // 시즌 사이 화제성 감쇠 (순회 흥행 특전 = 보존)
        }

        // 세계 역사: 역대 챔피언 기록 + 루두스 명성/업적(리그 우승·왕조)
        _champions.Add(new ChampionRec(_seasonNo, champ.Name, $"{champ.W}-{champ.L}-{champ.D}", champ.IsPlayer));
        if (champ.IsPlayer) { AddRep(RepLeagueTitle); AddGlory(GloryLeagueTitle); Unlock("first_title"); }
        else AddRivalRep(champ.LudusId, RepLeagueTitle);   // 라이벌 우승 = 그 검투소 명성
        if (_champions.Count >= 3 && _champions.TakeLast(3).All(c => c.IsPlayer)) Unlock("dynasty");

        SwapDivisions();   // 승강(성적 기반) — 다음 시즌 배치 확정. 챔피언은 1부 1위라 강등 불가

        // AI 세대교체: 노화 6시즌 경과(36~42세) 또는 상한 바닥 → 은퇴(명예의 전당) → 신인 AI 데뷔 (리그 영속성).
        // 내 선수는 은퇴 없음 — 방출은 감독 권한(약해진 채 데리고 있을 자유).
        var retirements = new List<string>();
        var rookieRng = new SimRandom(_worldSeed ^ 0xA1A1_A1A1UL + (ulong)_seasonNo * 97UL);
        // 신인 파동(변칙성): 시즌마다 원석 품질이 출렁인다 — 풍년(20%)=천부 2롤, 평년=1롤
        bool rookieBoom = rookieRng.Roll(0.20f);
        if (rookieBoom) _story.Add((_rounds + 1, "season", "🌾 신인 풍년 — 이번 세대엔 유망한 원석이 많다"));
        Gladiator SpawnRookie(string inheritLudus, int inheritDiv) =>
            SpawnRookieCore(rookieRng, inheritLudus, inheritDiv, rookieBoom ? 2 : 1);
        // (극적 운명은 실시간 — 경기 직후 Play()에서 그때그때 발생)

        foreach (var old in _cast.Where(g => !g.IsPlayer).ToList())
        {
            bool aged = old.Age >= old.AgingStartAge + 6 || old.PotentialBudget <= MinPotentialBudget + 0.5f;
            // 부진 방출(변칙성): 충분히 뛰고도 승률이 처참한 AI는 검투소가 조기 정리 → 리그 물갈이
            int games = old.CW + old.CL + old.CD;
            bool washed = !aged && games >= 12 && old.CW < games * 0.2f;
            if (!aged && !washed) continue;

            _cast.Remove(old);
            _ledger.RemoveFighter(old.Id);
            if (aged)   // 명예의 전당은 은퇴자만 — 방출자는 조용히 사라진다
                _hall.Add(new HallRec(old.Name, old.WeaponId.Replace("WPN_", ""), MathF.Round(old.Fame),
                    $"{old.CW}-{old.CL}-{old.CD}", old.Age, _seasonNo, old.IsPlayer));
            var rookie = SpawnRookie(old.LudusId, old.Division);
            string note = aged
                ? $"{old.Name}({old.Age}세, 명성 {old.Fame:F0}) 은퇴 → 신인 {rookie.Name} 데뷔"
                : $"{old.Name}({old.CW}승 {old.CL}패) 방출 → 신인 {rookie.Name} 데뷔";
            retirements.Add(note);
            _story.Add((_rounds + 1, aged ? "retire" : "release", (aged ? "🏛 " : "👋 ") + note + (aged ? " — 명예의 전당 등재" : "")));
        }

        // 시즌 요약 (연출 화면 — 프리시즌 동안 표시)
        _lastSummary = new SeasonSummaryDoc(_seasonNo, champ.Name, champ.IsPlayer,
            standings.Select((g, i) => new RankRow(i + 1, g.Name, g.W, g.L, g.D, g.SeasonPoints, g.IsPlayer)).ToList(),
            bestRank, bonusPaid, salaryPaid, MathF.Round(_gold), agingNotes,
            _story.Count(s => s.Kind == "revenge"), _story.Count(s => s.Kind == "upset"), _story.Count(s => s.Kind == "comeback"),
            _cast.OrderByDescending(g => g.Fame).First().Name,
            retirements.Count > 0 ? retirements : null,
            _cupChampion, _cupChampion != null && _cast.Any(g => g.IsPlayer && g.Name == _cupChampion),
            _seasonNewAch.Count > 0 ? _seasonNewAch.ToList() : null);

        SaveWorld();
    }

    /// <summary>방출: 프리시즌에만(시즌 중엔 스케줄 고정). 관계 청산 포함.</summary>
    public string ReleaseJson(string fighterId)
    {
        if (SeasonActive) return Err("시즌 중엔 방출 불가 — 시즌 종료 후에");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        _story.Add((0, "release", $"👋 방출 — {g.Name}이(가) 루두스를 떠났다"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    private int _sparCount;   // 스파링 시드 카운터(영속 — 결정론)

    /// <summary>친선 스파링(프리시즌): 같은 부 AI와 연습 경기 — 무기록·부상 없음, 성장 소량 + 가벼운 피로.</summary>
    public string SparringJson(string fighterId)
    {
        if (SeasonActive) return Err("스파링은 프리시즌에만");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        if (g.Fatigue >= 80) return Err("피로가 너무 쌓였다 — 휴식이 먼저");
        var rng = new SimRandom(_worldSeed ^ 0x5B42_00AAUL + (ulong)_sparCount++ * 7UL);
        var peers = _cast.Where(x => !x.IsPlayer && x.Division == g.Division).ToList();
        if (peers.Count == 0) peers = _cast.Where(x => !x.IsPlayer).ToList();
        if (peers.Count == 0) return Err("상대가 없다");
        var opp = peers[(int)(rng.NextUInt64() % (ulong)peers.Count)];
        var (dA, dB) = BuildDefs(g, opp, "normal");
        var res = new MatchSim().Run(dA, dB, rng.NextUInt64());
        string? grow = Grow(g, rng);
        g.Fatigue = Math.Min(100, g.Fatigue + 3);
        string wName = res.Winner == 0 ? g.Name : res.Winner == 1 ? opp.Name : "무승부";
        _story.Add((0, "sparring", $"🤺 스파링 — {g.Name} vs {opp.Name}: {wName} 우세" + (grow != null ? $" · {grow} +0.5" : "")));
        SaveWorld();
        return JsonSerializer.Serialize(new { ok = true, opp = opp.Name, winner = wName, grow, fatigue = g.Fatigue }, JsonOpts);
    }

    /// <summary>은퇴(세대·혈통): 프리시즌에 내 선수를 명예롭게 보낸다 → 명예의 전당(★).
    /// 명성 60+ 전설이면 루두스의 스승으로 남아 유산을 물려준다: 영입 원석 품질(+1롤)·신인 잠재력 +10.</summary>
    public string RetireJson(string fighterId)
    {
        if (SeasonActive) return Err("시즌 중엔 은퇴 불가 — 시즌 종료 후에");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        _hall.Add(new HallRec(g.Name, g.WeaponId.Replace("WPN_", ""), MathF.Round(g.Fame),
            $"{g.CW}-{g.CL}-{g.CD}", g.Age, Math.Max(1, _seasonsPlayed), true));
        if (g.Fame >= MentorFameMin)
        {
            _mentorName = g.Name;
            _story.Add((0, "mentor", $"🏛 {g.Name}({g.Fame:F0} 명성) 은퇴 — 루두스의 스승이 되다. 후배들에게 유산을 남긴다"));
        }
        else _story.Add((0, "retire", $"🏛 {g.Name} 명예 은퇴 — 명예의 전당 등재"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }
    private const float MentorFameMin = 60f;
    private string? _mentorName;   // 루두스의 스승(은퇴 전설) — 영입 유산

    // ── 제국 특전(영광 소모 영구 업그레이드) — 루두스 제국 등반의 뼈대. 전부 메타 효과. ──
    private static readonly (string Id, string Name, string Desc, int Max, int[] Costs)[] PerkDefs =
    {
        ("patron", "제국 후원",   "경기 수입 +5%/Lv",             3, new[] { 8, 16, 24 }),
        ("senate", "원로원 인맥", "뽑기 비용 −15%/Lv",            3, new[] { 6, 12, 18 }),
        ("tour",   "순회 흥행",   "시즌 사이 인기 보존 +10%p/Lv", 2, new[] { 8, 16 }),
    };
    private readonly Dictionary<string, int> _perks = new();
    private int PerkLv(string id) => _perks.GetValueOrDefault(id);
    private float EffGachaCost => MathF.Round(GachaCost * (1f - 0.15f * PerkLv("senate")));

    /// <summary>제국 특전 구매: 영광 소모 → 영구 루두스 업그레이드.</summary>
    public string PerkJson(string id)
    {
        var def = PerkDefs.FirstOrDefault(p => p.Id == id);
        if (def.Id == null) return Err("잘못된 특전");
        int lv = PerkLv(id);
        if (lv >= def.Max) return Err($"{def.Name} 최대 Lv");
        int cost = def.Costs[lv];
        if (_glory < cost) return Err($"영광 부족 ({cost} 필요)");
        _glory -= cost;
        _perks[id] = lv + 1;
        _story.Add((0, "perk", $"🏛 제국 특전 — {def.Name} Lv{lv + 1} (영광 −{cost})"));
        SaveWorld();
        return StateJson();
    }

    private List<Gladiator> Standings(int? division = null) =>
        _cast.Where(g => division == null || g.Division == division)
             .OrderByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W).ToList();

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
        EnsureSchedule();

        var s = _schedule[_cursor++];
        var A = ById(s.A); var B = ById(s.B);

        // 전술 결정: 내 선수 = 감독 선택(이번 요청 or 기존 유지) / AI = 상대 맞춤 휴리스틱 + 시드 노이즈
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);
        if (A.IsPlayer) { if (tacticId != null && A.TacticPool.Contains(tacticId)) A.TacticId = tacticId; }
        else A.TacticId = SelectTacticAi(A, B, tacRng);
        if (B.IsPlayer) { if (tacticId != null && !A.IsPlayer && B.TacticPool.Contains(tacticId)) B.TacticId = tacticId; }
        else B.TacticId = SelectTacticAi(B, A, tacRng);

        var res = Play(A, B, s.Round, s.Kind, out float income, out string incomeNote, out var mine, s.Format);
        if (s.IsEvent)
            _eventDocs.Add(new EventDoc(A.Name, B.Name, s.Score,
                res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason == "KO"));

        // 컵 결승: 우승자 확정 + 상금·명성·업적
        if (s.Kind == "cup_final" && res.Winner >= 0)
        {
            var cupW = res.Winner == 0 ? A : B;
            _cupChampion = cupW.Name;
            cupW.Fame += 10f;
            _story.Add((s.Round, "cup", $"🏆 챔피언십 컵 우승 — {cupW.Name}!"));
            if (cupW.IsPlayer) { _gold += CupWinPrize; AddRep(RepCupTitle); AddGlory(GloryCup); Unlock("first_cup"); }
            else AddRivalRep(cupW.LudusId, RepCupTitle);
        }
        else if (s.Kind == "cup_sf" && res.Winner >= 0)   // 4강 진출 상금(내 선수)
        {
            var w = res.Winner == 0 ? A : B;
            if (w.IsPlayer) _gold += CupSemiPrize;
        }

        EnsureSchedule();   // 다음 페이즈 편성(예: 4강 후 결승) — 종료 판정 전에
        bool last = _cursor >= _schedule.Count && _cupStage == 3;
        if (!last) MaybeSpawnEvent(A.IsPlayer ? A : B.IsPlayer ? B : null);   // 내 경기 후 서사 이벤트(2b)
        if (last) FinalizeSeason();
        else SaveWorld();
        if (_interactive) WriteSeasonJson();

        return new MatchSummary(_seasonNo, s.Round, s.IsEvent, A.Name, B.Name,
            res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason, last, newSeason,
            A.IsPlayer || B.IsPlayer, income, incomeNote, mine,
            _lastFates.Count > 0 ? _lastFates.ToList() : null);
    }

    /// <summary>
    /// 다음 경기가 없으면 다음 페이즈를 편성: 정규 소진 → 이벤트 빅매치 → 챔피언십 컵(4강→결승).
    /// 각 단계는 감독이 전술을 고를 수 있게 한 페이즈씩 채운다. 시즌 종료 판정 = 컵까지 끝(_cupStage==3).
    /// </summary>
    private void EnsureSchedule()
    {
        if (!SeasonActive || _cursor < _schedule.Count) return;

        // 정규 소진 → 이벤트 빅매치 (일부는 특수 형식: ☠처형전 / ⚔무기 지정전 — 시드 결정론)
        if (!_eventsAppended)
        {
            var fmtRng = new SimRandom(SeasonSeed ^ 0xF0_47_11UL);
            var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
            foreach (var (a, b, score) in TopEventCards(Math.Max(2, _cast.Count / 2)))
            {
                string fmt = fmtRng.Roll(0.30f) ? "execution"
                           : fmtRng.Roll(0.25f) ? "same:" + wpns[(int)(fmtRng.NextUInt64() % (ulong)wpns.Length)]
                           : "normal";
                _schedule.Add(new SchedRec(_rounds + 1, a, b, true, score, "event", fmt));
            }
            _eventsAppended = true;
            if (_cursor < _schedule.Count) return;
        }

        // 이벤트 소진 → 챔피언십 컵
        if (_cupStage == 0)
        {
            var top = Standings(1).Take(4).ToList();          // 컵 = 1부 상위 4인
            if (top.Count < 4) { _cupStage = 3; return; }   // 선수 부족 → 컵 생략
            _cupSeeds = top.Select(g => g.Id).ToList();
            _story.Add((_rounds + 2, "cup", $"🏛 챔피언십 컵 개막 — {top[0].Name}·{top[1].Name}·{top[2].Name}·{top[3].Name}"));
            _schedule.Add(new SchedRec(_rounds + 2, _cupSeeds[0], _cupSeeds[3], false, 0f, "cup_sf"));  // 1v4
            _schedule.Add(new SchedRec(_rounds + 2, _cupSeeds[1], _cupSeeds[2], false, 0f, "cup_sf"));  // 2v3
            _cupStage = 1;
            return;
        }
        if (_cupStage == 1)   // 4강 둘 다 끝 → 결승 편성
        {
            var sfWinners = _matchLog.Where(m => m.Round == _rounds + 2).TakeLast(2)
                .Select(m => m.Winner == m.AName ? m.AId : m.BId).ToList();
            if (sfWinners.Count == 2)
                _schedule.Add(new SchedRec(_rounds + 3, sfWinners[0], sfWinners[1], false, 0f, "cup_final"));
            _cupStage = 2;
            return;
        }
        if (_cupStage == 2) _cupStage = 3;   // 결승 끝 → 컵 종료
    }

    /// <summary>내 경기 직전(전술 선택 기회) 또는 시즌 종료까지 AI 경기 자동 시뮬. 프리시즌이면 개막부터.</summary>
    public string PlayUntilMineJson()
    {
        int played = 0; bool seasonDone = false;
        for (int guard = 0; guard < 600; guard++)
        {
            if (!SeasonActive) { PlayNext(); continue; }        // 개막 (경기 아님)
            EnsureSchedule();
            if (_cursor >= _schedule.Count) break;
            var s = _schedule[_cursor];
            if (ById(s.A).IsPlayer || ById(s.B).IsPlayer) break; // 내 경기 발견 — 멈춰서 감독에게
            var m = PlayNext(); played++;
            if (m.SeasonCompleted) { seasonDone = true; break; }
        }
        return JsonSerializer.Serialize(new { played, seasonDone }, JsonOpts);
    }

    // ── 라이브 매치(감독 실시간 개입) — 관전 먼저, 정산은 나중. 커서는 정산 시에만 전진(앱 종료 = 미개시로 복원, 세이브 안전) ──
    private sealed class LiveMatch
    {
        public required string MyId; public required string[] MyPool;
        public required List<TacticSwitch> Switches;
    }
    private LiveMatch? _live;                                       // 진행 중 라이브 매치(메모리 전용 — 영속 안 함)
    private (string FighterId, TacticSwitch[] Switches)? _liveSwitches;   // 정산 시 Play가 def에 주입

    /// <summary>내 경기 라이브 시작: 커서 전진 없이 잠정 시뮬 → viewer.json. 정산(/api/settle) 전까지 세계 무변이.</summary>
    public string LiveBeginJson(string? tacticId)
    {
        if (!SeasonActive || _cursor >= _schedule.Count) return Err("다음 경기가 없다");
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var mine = A.IsPlayer ? A : B.IsPlayer ? B : null;
        if (mine == null) return Err("내 경기가 아니다");

        // PlayNext와 동일한 전술 결정(같은 rng 시드 → 정산 때 재현됨)
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);
        if (A.IsPlayer) { if (tacticId != null && A.TacticPool.Contains(tacticId)) A.TacticId = tacticId; }
        else A.TacticId = SelectTacticAi(A, B, tacRng);
        if (B.IsPlayer) { if (tacticId != null && !A.IsPlayer && B.TacticPool.Contains(tacticId)) B.TacticId = tacticId; }
        else B.TacticId = SelectTacticAi(B, A, tacRng);

        _live = new LiveMatch { MyId = mine.Id, MyPool = mine.TacticPool, Switches = new() };
        LiveResim();
        return JsonSerializer.Serialize(new { ok = true, a = A.Name, b = B.Name, round = s.Round,
            kind = s.Kind, remaining = 2 }, JsonOpts);
    }

    /// <summary>라이브 재시뮬(같은 시드 + 현재 전환 예약) → viewer.json 재작성. Play와 동일한 def 조립 = 정산과 일치.</summary>
    private void LiveResim()
    {
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var (defA, defB) = BuildDefs(A, B, s.Format);   // 정산(Play)과 동일 조립 — 형식 오버라이드 포함
        if (_live!.Switches.Count > 0)
        {
            var sw = _live.Switches.OrderBy(x => x.Time).ToArray();
            if (A.Id == _live.MyId) defA = defA with { TacticSwitches = sw };
            else defB = defB with { TacticSwitches = sw };
        }
        ulong seed = SeasonSeed + (ulong)(_matchIdx + 1);   // Play의 ++_matchIdx와 동일
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(defA, defB, seed, events, frames);
        ViewerExport.WriteDoc(defA, defB, seed, res, frames, events, "viewer.json",
            EndowOf(A.Id, defA), EndowOf(B.Id, defB));
    }

    /// <summary>관전 중 전술 변경(2회 한정): 그 시각부터 새 전술로 재시뮬 — 이후의 운명이 갈린다.</summary>
    public string LiveSwitchJson(float time, string tacticId)
    {
        if (_live == null) return Err("라이브 경기가 없다");
        if (_live.Switches.Count >= 2) return Err("전술 변경은 경기당 2회");
        string full = tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId;
        if (!_live.MyPool.Contains(full)) return Err("전술풀에 없는 전술");
        _live.Switches.Add(new TacticSwitch(MathF.Max(0.1f, time), full));
        LiveResim();
        return JsonSerializer.Serialize(new { ok = true, remaining = 2 - _live.Switches.Count }, JsonOpts);
    }

    /// <summary>라이브 정산: 예약된 전환을 주입해 정식 경기 처리(수입·명성·관계·운명·저장). 관전한 것과 같은 시드 = 같은 결과.</summary>
    public string LiveSettleJson()
    {
        if (_live == null) return Err("정산할 라이브 경기가 없다");
        if (_live.Switches.Count > 0)
            _liveSwitches = (_live.MyId, _live.Switches.OrderBy(x => x.Time).ToArray());
        _live = null;
        return JsonSerializer.Serialize(PlayNext(null), JsonOpts);
    }

    /// <summary>시즌 자동완주(편의): 내 경기 포함 남은 전 경기를 현재 전술로 진행. 이벤트 발생 시 멈춰서 감독에게 결정 위임.</summary>
    public string AutoFinishJson()
    {
        if (!SeasonActive) return Err("시즌 진행 중이 아니다");
        int played = 0, guard = 0;
        while (SeasonActive && _pendingEventId == null && guard++ < 600)
        {
            PlayNext();   // 각 선수 현재(기본) 전술로 — 관전·모달 없이 빠르게
            played++;
        }
        return JsonSerializer.Serialize(new { ok = true, played, seasonDone = !SeasonActive,
            eventPending = _pendingEventId != null }, JsonOpts);
    }

    /// <summary>경기 재관전: 로그의 스냅샷+시드로 결정론 재시뮬 → viewer.json. idx<0 = 최근 경기.</summary>
    public string WatchJson(int idx)
    {
        var e = idx < 0 ? _matchLog.LastOrDefault() : _matchLog.FirstOrDefault(x => x.Idx == idx);
        if (e == null) return Err("경기 기록 없음");
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(e.DefA, e.DefB, e.Seed, events, frames);
        ViewerExport.WriteDoc(e.DefA, e.DefB, e.Seed, res, frames, events, "viewer.json",
            EndowOf(e.AId, e.DefA), EndowOf(e.BId, e.DefB));
        return JsonSerializer.Serialize(new { ok = true, a = e.AName, b = e.BName, round = e.Round, isEvent = e.IsEvent }, JsonOpts);
    }

    private ViewerEndowment? EndowOf(string id, FighterDef def)
    {
        var g = _cast.FirstOrDefault(x => x.Id == id);
        if (g == null) return null;
        return new(ViewerExport.TalentName(g.Talent), ViewerExport.PotentialName(g.Potential),
            g.TalentBudget, g.PotentialBudget,
            def.Stats.Atk, def.Stats.Def, def.Stats.HpMax, def.Stats.Spd, def.Stats.Aspd, def.Stats.Rct);
    }

    /// <summary>루두스 상세에서 전술 변경 (다음 경기 기본값 — 경기 직전 모달과 별개 경로).</summary>
    public string TacticJson(string fighterId, string tacticId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        string tid = tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId;
        if (!g.TacticPool.Contains(tid)) return Err("보유 전술 아님");
        g.TacticId = tid;
        SaveWorld();
        return StateJson();
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

    private MatchResult Play(Gladiator A, Gladiator B, int round, string kind,
                             out float income, out string incomeNote, out List<MyDelta>? mine,
                             string format = "normal")
    {
        bool isEvent = kind != "regular";   // 이벤트·컵 = 순위 무관(exhibition), 흥행 배수
        bool exec = format == "execution";  // ☠ 처형전 — 패자는 죽을 수 있다. 보상도 크다
        var (defA, defB) = BuildDefs(A, B, format);
        if (_liveSwitches is { } li)   // 감독 실시간 개입(라이브 정산): 관전 중 예약한 전술 전환을 결정 def에 주입
        {
            if (A.Id == li.FighterId) defA = defA with { TacticSwitches = li.Switches };
            else if (B.Id == li.FighterId) defB = defB with { TacticSwitches = li.Switches };
            _liveSwitches = null;
        }
        A.PendingEmotions.Clear(); B.PendingEmotions.Clear();   // 감정 소비 → 소멸 ([2]§6-1)
        float fameA0 = A.Fame, popA0 = A.Popularity, fameB0 = B.Fame, popB0 = B.Popularity;

        // 관전은 로그의 스냅샷+시드로 결정론 재시뮬(WatchJson) — 여기선 시뮬만.
        ulong seed = SeasonSeed + (ulong)(++_matchIdx);
        var res = new MatchSim().Run(defA, defB, seed);

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

            // 명성(통산 업적, 무감쇠) — 승자 중심. 처형전 승자는 배가된 영예
            win.Fame += (3f + (ko ? 2f : 0f) + (comeback ? 5f : 0f) + (upset ? 4f : 0f)
                      + winStats.CleanHits * 0.1f + winStats.Knockdowns) * (exec ? 2f : 1f);
            lose.Fame = MathF.Max(0f, lose.Fame + 0.5f - (loseStats.Taunted ? 2f : 0f));

            // 라이벌 루두스 경쟁: AI 승자의 소속 검투소 명성 누적(플레이어는 income 루프에서 별도 처리)
            if (!win.IsPlayer) AddRivalRep(win.LudusId, RepWin + (comeback || upset || revenge ? RepDrama : 0f));
        }

        // 인기(최근 화제성, 감쇠) — 패자도 잘 싸우면 오른다
        UpdatePopularity(A, res.StatsA, res.StatsB, res.Winner == 0, res.Winner < 0, ko, comeback, upset, revenge, isEvent);
        UpdatePopularity(B, res.StatsB, res.StatsA, res.Winner == 1, res.Winner < 0, ko, comeback, upset, revenge, isEvent);

        // 경제: 내 선수 출전 시 경기별 수입 (출전료 = hype)
        income = 0f; float incA = 0f, incB = 0f; string noteA = "", noteB = "";
        foreach (var (self, other) in new[] { (A, B), (B, A) })
        {
            if (_playerless || !self.IsPlayer) continue;
            float own = (FeeBase + (self.Popularity + other.Popularity) * FeePopScale) * (exec ? 3f : isEvent ? 2f : 1f) * IncomeMult
                      * (1f + 0.08f * self.MPay);   // 협상 마스터리 = 출전료 협상력. 처형전 ×3(목숨값)
            var notes = new List<string> { $"출전료 +{own:F0}" };
            if (win == self)
            {
                float bonus = (WinBonus + (ko ? KoBonus : 0f) + (comeback ? DramaBonus : 0f) + (upset ? DramaBonus : 0f)) * IncomeMult;
                own += bonus; notes.Add($"승리 +{bonus:F0}");
                // 루두스 등급 명성 (A): 내 승리·드라마
                AddRep(RepWin + (comeback || upset || revenge ? RepDrama : 0f));
                if (upset) AddGlory(GloryUpset);   // 대이변 = 위신(영광)
                Unlock("first_win");
                if (self.Streak >= 9) Unlock("streak10");   // 이번 승으로 10연승(Record는 이후 반영)
            }
            if (self.Fame >= 100f) Unlock("legend");
            income += own;
            if (self == A) { incA = own; noteA = string.Join(" · ", notes); } else { incB = own; noteB = string.Join(" · ", notes); }
        }
        _gold += income;
        incomeNote = string.Join(" · ", new[] { noteA, noteB }.Where(n => n.Length > 0));

        // 순위/커리어 + 관계 + 감정 (경기 인덱스 파생 스트림 = 미드시즌 재개 결정론)
        Record(A, B, res, standing: !isEvent);
        _ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
        ProcessFatigue(A, res.StatsA, res, 0, round);   // 피로 누적(메타) + 부상 판정(드묾, 부상만 스탯 영향)
        ProcessFatigue(B, res.StatsB, res, 1, round);
        var emoRng = new SimRandom(SeasonSeed ^ 0x5EA5_04EDUL + (ulong)_matchIdx * 17UL);
        string? eA = EmotionGen.Roll(emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers);
        string? eB = EmotionGen.Roll(emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers);
        if (eA != null) { A.PendingEmotions.Add(eA); _emoGen++; }
        if (eB != null) { B.PendingEmotions.Add(eB); _emoGen++; }

        // 성장: 경기 자동 소량 + 3경기당 훈련 포인트
        var growRng = new SimRandom(SeasonSeed ^ 0x6120_6120UL + (ulong)_matchIdx * 13UL);
        string? growA = Grow(A, growRng); string? growB = Grow(B, growRng);
        int trA = TickTraining(A, growRng); int trB = TickTraining(B, growRng);

        if (LudusTier() >= LudusTiers.Length - 1) Unlock("empire");

        // 경기 로그 (스냅샷+시드 = 재관전) + 내 선수 변경사항(결과 화면)
        string winner = res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name);
        _matchLog.Add(new LogEntry(_matchIdx, round, isEvent, A.Id, B.Id, A.Name, B.Name,
            winner, res.Reason, A.IsPlayer || B.IsPlayer, seed, defA, defB));
        mine = null;
        if (!_playerless)
        {
            if (A.IsPlayer) (mine ??= new()).Add(new MyDelta(A.Name, win == A, res.Winner < 0, incA, noteA,
                A.Fame - fameA0, A.Popularity - popA0, growA, trA, eA != null ? EmotionTable.Get(eA).Name : null));
            if (B.IsPlayer) (mine ??= new()).Add(new MyDelta(B.Name, win == B, res.Winner < 0, incB, noteB,
                B.Fame - fameB0, B.Popularity - popB0, growB, trB, eB != null ? EmotionTable.Get(eB).Name : null));
        }

        // ── 극적 운명(실시간): 경기가 끝난 그 순간 운명이 갈린다 — 드묾, 매치시드 결정론 ──
        _lastFates.Clear();
        var fRng = new SimRandom(SeasonSeed ^ 0xFA7E_FA7EUL + (ulong)_matchIdx * 61UL);
        void Fate(int r, string k, string note) { _lastFates.Add(note); _story.Add((r, k, note)); }
        if (win != null && lose != null)
        {
            bool loserBrutal = ko || loseStats.MinHpPct <= 0.15f;
            // ⚰ 사망 — 정규 경기(격전 누적 2%) 또는 ☠처형전(격전 패배 25% — 그것이 처형전이다). 컵 대진은 보호
            if ((kind == "regular" || exec) && loserBrutal && (exec || lose.SeasonBrutals >= 2)
                && fRng.Roll(exec ? 0.25f : 0.02f))
            {
                _cast.Remove(lose); _ledger.RemoveFighter(lose.Id);
                for (int i = _schedule.Count - 1; i >= _cursor; i--)   // 남은 대진에서 제거
                    if (_schedule[i].A == lose.Id || _schedule[i].B == lose.Id) _schedule.RemoveAt(i);
                _hall.Add(new HallRec(lose.Name, lose.WeaponId.Replace("WPN_", ""), MathF.Round(lose.Fame),
                    $"{lose.CW}-{lose.CL}-{lose.CD} ⚰전사", lose.Age, _seasonNo, lose.IsPlayer));
                Fate(round, "death", $"⚰ {lose.Name}({lose.Age}세) — 모래 위에서 숨을 거두다. 검투사로 죽다");
                if (!lose.IsPlayer)
                {
                    var rk = SpawnRookieCore(fRng, lose.LudusId, lose.Division, 1);
                    _story.Add((round, "recruit", $"🌱 {LudusNameOf(lose.LudusId)}, 공석에 신인 {rk.Name} 영입 (다음 시즌 출전)"));
                }
            }
            else if (loserBrutal && fRng.Roll(0.03f))   // 💀 영구 중상 — 상한 자체가 깎인다
            {
                lose.PotentialBudget = MathF.Max(MinPotentialBudget, lose.PotentialBudget - 12f);
                float ex = BudgetUsed(lose.Stats) - lose.PotentialBudget;
                if (ex > 0f) { lose.Stats = WithAxis(lose.Stats, 5, -ex * 0.5f); for (int a = 0; a < 5; a++) lose.Stats = WithAxis(lose.Stats, a, -ex * 0.1f); }
                Fate(round, "grave_injury", $"💀 {lose.Name} — 영구 중상, 몸이 예전 같지 않다 (상한 {lose.PotentialBudget:F0})");
            }
            else if (loserBrutal && lose.SeasonBrutals >= 2 && fRng.Roll(0.04f))   // 🎭 트라우마 성격 변화
            {
                string? shift = lose.PersonalityId switch
                {
                    "PER_RECKLESS" => "PER_WARY", "PER_BOLD" => "PER_CALM", "PER_ARROGANT" => "PER_WARY",
                    "PER_SHOWMAN" => "PER_CALM", "PER_CRUEL" => "PER_WARY", "PER_OPPORTUNIST" => "PER_WARY",
                    "PER_CALM" => "PER_WARY", "PER_WARY" => "PER_COWARD", _ => null,
                };
                if (shift != null)
                {
                    lose.PersonalityId = shift;
                    Fate(round, "persona", $"🎭 {lose.Name} — 사선을 넘은 패배가 사람을 바꿨다 ({shift.Replace("PER_", "")})");
                }
            }
            // 🌟 각성 — 대역전·이변의 순간, 한계가 열린다 (승자·30세 이하)
            if (_cast.Contains(win) && (comeback || upset) && win.Age <= 30 && fRng.Roll(0.04f))
            {
                win.PotentialBudget += 20f;
                win.Stats = WithAxis(win.Stats, (int)(fRng.NextFloat01() * 6), 2f);
                win.Stats = WithAxis(win.Stats, (int)(fRng.NextFloat01() * 6), 2f);
                Fate(round, "awakening", $"🌟 {win.Name} — 각성! 그 승리가 한계를 열었다 (상한 {win.PotentialBudget:F0})");
                string? bloom = win.PersonalityId switch { "PER_COWARD" => "PER_BOLD", "PER_WARY" => "PER_BOLD", _ => null };
                if (bloom != null && fRng.Roll(0.30f))
                { win.PersonalityId = bloom; Fate(round, "persona", $"🎭 {win.Name} — 성격 개화: 대담해졌다"); }
            }
        }
        // ⚖ 강제 트레이드오프 — 몸의 적응(아주 드묾, 승패 무관)
        foreach (var g in new[] { A, B })
            if (_cast.Contains(g) && fRng.Roll(0.008f))
            {
                int a = (int)(fRng.NextFloat01() * 6), b = (a + 1 + (int)(fRng.NextFloat01() * 5)) % 6;
                g.Stats = WithAxis(g.Stats, a, -3f); g.Stats = WithAxis(g.Stats, b, 3f);
                Fate(round, "tradeoff", $"⚖ {g.Name} — 몸의 적응: {AxisNames[a]} −3 → {AxisNames[b]} +3");
                break;
            }
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
        float matchPop = (spect + result + taunt) * (isEvent ? 1.5f : 1f) * (1f + 0.08f * g.MShow);   // 흥행 마스터리
        g.Popularity = MathF.Max(0f, g.Popularity * 0.95f + matchPop);
    }

    /// <summary>경기 후 피로 누적(메타) + 드문 부상 판정. 부상 중엔 향후 몇 경기 실효 스탯 하락(ToDef). 의무실=플레이어 부상 완화.</summary>
    private void ProcessFatigue(Gladiator g, MatchFighterStats st, MatchResult res, int side, int round)
    {
        if (g.InjuryMatches > 0) g.InjuryMatches--;                     // 경기 소화 = 회복 1진행
        bool lostKo = res.Reason == "KO" && res.Winner >= 0 && res.Winner != side;
        bool brutal = lostKo || st.MinHpPct <= 0.15f;                   // 격전 = KO패 또는 빈사
        int fGain = (int)MathF.Round((5 + (brutal ? 7 : 0)) * (1f - 0.08f * g.MGrit));   // 투혼 마스터리 = 피로 저항
        g.Fatigue = Math.Min(100, g.Fatigue + fGain);                    // 피로 누적(메타) — 격전일수록 큼
        if (brutal) g.SeasonBrutals++;                                   // 극적 운명(사망·영구중상) 게이트 누적

        // 부상은 '격전에서만' 드물게 발생(부상=중상). 높은 피로도·의무실이 확률 조정.
        if (brutal && g.InjuryMatches == 0)
        {
            var rng = new SimRandom(SeasonSeed ^ 0x1234_9A11UL + (ulong)_matchIdx * 7UL + (ulong)side);
            float chance = 0.15f * (g.Fatigue > 60 ? 1.6f : 1f);
            if (g.IsPlayer) chance *= 1f - 0.25f * (_medicalLv - 1);     // 의무실 Lv → 부상률 감소
            chance *= 1f - 0.10f * g.MRecover;                           // 회복력 마스터리
            if (rng.Roll(chance))
            {
                int dur = (g.IsPlayer && _medicalLv >= 2) || g.MRecover >= 3 ? 1 : 2;
                g.InjuryMatches = dur;
                _story.Add((round, "injury", $"🩹 부상! {g.Name} — 향후 {dur}경기 실효 스탯 저하"));
            }
        }
    }

    /// <summary>경기 자동 성장 +0.5pt. 성장한 축 이름 반환(결과 화면 표시용), 상한 도달 시 null.</summary>
    private string? Grow(Gladiator g, SimRandom rng)
    {
        if (BudgetUsed(g.Stats) + 0.5f > g.PotentialBudget) return null;   // 상한 도달 — 더 안 큼
        int axis = (int)(rng.NextFloat01() * 6f);
        g.Stats = WithAxis(g.Stats, axis, 0.5f);
        return AxisNames[axis];
    }

    /// <summary>3경기 주기 훈련. 내 선수는 포인트 지급(반환값), AI는 자동 분배.</summary>
    private int TickTraining(Gladiator g, SimRandom rng)
    {
        if (++g.MatchCounter < TrainEveryMatches) return 0;
        g.MatchCounter = 0;
        int pts = g.IsPlayer ? _trainingLv : 1;
        if (g.IsPlayer) { g.TrainingPoints += pts; return pts; }      // 감독이 분배
        for (int i = 0; i < pts; i++) Grow(g, rng);                   // AI 자동 (같은 리듬, 형평)
        return 0;
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

    /// <summary>경기 def 조립(잠정 시뮬·정산 공용 — 동일성 필수). 무기 지정전(same:WPN_x)은 양측 무기 오버라이드.</summary>
    private (FighterDef defA, FighterDef defB) BuildDefs(Gladiator A, Gladiator B, string format)
    {
        var relA = _ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
        var relB = _ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
        var defA = ToDef(A, relA, Intensity(A.Id, B.Id));
        var defB = ToDef(B, relB, Intensity(B.Id, A.Id));
        if (format.StartsWith("same:"))
        {
            string w = format[5..];
            defA = defA with { WeaponId = w }; defB = defB with { WeaponId = w };
        }
        return (defA, defB);
    }

    private FighterDef ToDef(Gladiator g, RelationType? rel, float intensity)
    {
        // 부상 중에만 실효 스탯 소폭 하락(반응·속도 위주 — 코어 매트릭스 ATK/DEF/HP 불변, 회복성). 평상 피로는 무영향.
        var stats = g.InjuryMatches > 0
            ? g.Stats with { Rct = g.Stats.Rct * 0.90f, Aspd = g.Stats.Aspd * 0.92f, Spd = g.Stats.Spd * 0.94f }
            : g.Stats;
        return new(g.Name, stats, g.WeaponId, g.TacticId, g.PersonalityId,
            g.TraitIds.Length > 0 ? g.TraitIds : null,
            g.PendingEmotions.Count > 0 ? g.PendingEmotions.ToArray() : null, rel, intensity);
    }

    private ViewerEndowment Endow(Gladiator g) => new(
        ViewerExport.TalentName(g.Talent), ViewerExport.PotentialName(g.Potential),
        g.TalentBudget, g.PotentialBudget,
        g.Stats.Atk, g.Stats.Def, g.Stats.HpMax, g.Stats.Spd, g.Stats.Aspd, g.Stats.Rct);

    private float Intensity(string self, string opp)
        => Math.Clamp(MathF.Abs(_ledger.Get(self, opp).Affinity) / 100f, 0f, 1f);

    /// <summary>배당용 전력 근사(표시 전용) — 스탯 합 + 명성·최근 폼(연승) + 부상 페널티.</summary>
    private static float Power(Gladiator g)
    {
        float s = g.Stats.Atk + g.Stats.Def + g.Stats.HpMax / 10f + g.Stats.Spd + g.Stats.Aspd + g.Stats.Rct;
        return s + g.Fame * 0.15f + g.Streak * 2f - (g.InjuryMatches > 0 ? 15f : 0f);
    }
    /// <summary>내 선수 관점 승률(0~1) — 극단 방지 클램프.</summary>
    private static float WinProb(Gladiator me, Gladiator opp)
        => Math.Clamp(Power(me) / MathF.Max(1f, Power(me) + Power(opp)), 0.15f, 0.85f);

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
        else if (_gold >= EffGachaCost) _gold -= EffGachaCost;   // 원로원 인맥 특전 = 뽑기 할인
        else return Err($"잔고 부족 (뽑기 {EffGachaCost:F0})");

        _candidates.Clear();
        var rng = new SimRandom(_worldSeed ^ 0x6ACA_6ACAUL + (ulong)(++_gachaCount) * 2654435761UL);
        var usedNames = _cast.Select(g => g.Name).Concat(_candidates.Select(c => c.Name)).ToHashSet();
        var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
        var pers = PersonalityTable.All.Select(p => p.Id).ToArray();
        int scouting = 1 + LudusTier() + (_mentorName != null ? 1 : 0);   // 루두스 등급 + 스승의 안목(혈통 유산) = 원석 품질
        for (int i = 0; i < 3; i++)
        {
            string name = PickName(rng, usedNames); usedNames.Add(name);
            var g = RollGladiator(rng,
                id: $"GLA_R{_gachaCount}_{i}", name,
                wpn: wpns[(int)(rng.NextFloat01() * wpns.Length)],
                per: pers[(int)(rng.NextFloat01() * pers.Length)],
                sigTactic: null, isPlayer: true, ageMin: 18, ageMax: 24, talentRolls: scouting);
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
        g.Division = 2;               // 무명 신인은 2부 투기장부터 — 승격으로 증명하라
        _cast.Add(g);
        // 중도 투입: 시즌 중 영입은 잔여 라운드에 같은 부 상대와의 합류전을 편성(컵 시작 전 한정)
        int joined = 0;
        if (SeasonActive && _cupStage == 0)
        {
            var jRng = new SimRandom(_worldSeed ^ 0x11D0_CAFEUL + (ulong)_gachaCount * 13UL);
            var rounds = _schedule.Skip(_cursor).Where(s => s.Kind == "regular").Select(s => s.Round).Distinct().OrderBy(r => r).ToList();
            var peers = _cast.Where(x => x.Division == g.Division && x.Id != g.Id).ToList();
            foreach (var r in rounds)
            {
                if (peers.Count == 0) break;
                var p = peers[(int)(jRng.NextUInt64() % (ulong)peers.Count)];
                _schedule.Add(new SchedRec(r, g.Id, p.Id, false, 0f));
                joined++;
            }
        }
        if (_mentorName != null)      // 스승의 지도(혈통 유산) — 신인의 그릇이 넓어진다
        {
            g.PotentialBudget += 10f;
            _story.Add((0, "mentor", $"📜 스승 {_mentorName}의 지도 — {g.Name} 잠재력 +10 (상한 {g.PotentialBudget:F0})"));
        }
        if (g.Talent == TalentGrade.Caesar) Unlock("caesar");
        _story.Add((0, "recruit", $"📜 영입! {g.Name} ({ViewerExport.TalentName(g.Talent)}·{g.Age}세) 루두스 합류" +
                                   (SeasonActive ? (joined > 0 ? $" — 중도 투입: 합류전 {joined}경기 편성" : " — 다음 시즌부터 출전") : "")));
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

    /// <summary>잠재력 돌파: 영광을 소모해 잠재력 상한(PotentialBudget)을 올린다 → 상한 찬 선수도 계속 성장.</summary>
    public string BreakthroughJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        int cost = BreakthroughCost(g);
        if (_glory < cost) return Err($"영광 부족 (돌파 {cost} 필요)");
        _glory -= cost;
        g.PotentialBudget += 25f;
        _story.Add((0, "breakthrough", $"💥 잠재력 돌파! {g.Name} — 상한 {g.PotentialBudget:F0} (영광 −{cost})"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>마스터리 수련: 훈련 포인트를 비스탯 성장에 투자(상한 찬 선수의 성장 여지).
    /// track: grit(투혼=피로저항)/recover(회복력=부상저항)/show(흥행=인기)/pay(협상=출전료). 비용=현재Lv+1, 최대 5.</summary>
    public string MasteryJson(string fighterId, string track)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        int lv = track switch { "grit" => g.MGrit, "recover" => g.MRecover, "show" => g.MShow, "pay" => g.MPay, _ => -1 };
        if (lv < 0) return Err("잘못된 마스터리");
        if (lv >= 5) return Err("마스터리 최대(5)");
        int cost = lv + 1;
        if (g.TrainingPoints < cost) return Err($"훈련 포인트 부족 ({cost} 필요)");
        g.TrainingPoints -= cost;
        switch (track) { case "grit": g.MGrit++; break; case "recover": g.MRecover++; break;
                         case "show": g.MShow++; break; default: g.MPay++; break; }
        SaveWorld();
        return StateJson();
    }

    /// <summary>개명(라니스타 명명권): kind=ludus → 내 루두스 / kind=fighter+id → 내 검투사.
    /// 검투사 개명 시 과거 기록(챔피언·명전·컵)의 이름도 승계(업적이 이름을 따라간다).</summary>
    public string RenameJson(string kind, string id, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 14) return Err("이름은 1~14자");
        if (kind == "ludus") { _ludusName = name; SaveWorld(); return StateJson(); }

        var g = _cast.FirstOrDefault(x => x.Id == id && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        if (_cast.Any(x => x != g && x.Name == name)) return Err("이미 있는 이름");
        string old = g.Name;
        g.Name = name;
        for (int i = 0; i < _champions.Count; i++) if (_champions[i].Name == old) _champions[i] = _champions[i] with { Name = name };
        for (int i = 0; i < _hall.Count; i++) if (_hall[i].Name == old) _hall[i] = _hall[i] with { Name = name };
        if (_cupChampion == old) _cupChampion = name;
        _story.Add((0, "rename", $"📛 개명 — {old} → {name}"));
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
        // 손상 대비: 본 파일 실패 시 직전 백업(world.bak — 매 저장 전 스냅샷)으로 복구 시도.
        WorldV2? w = TryRead(WorldPath);
        if (w == null && File.Exists(WorldPath + ".bak"))
        {
            w = TryRead(WorldPath + ".bak");
            if (w != null) Console.WriteLine("  ⚠ world.json 손상 — 백업(world.json.bak)에서 복구.");
        }
        if (w is null) return false;
        if (w.SchemaVer != SchemaVer)
        { Console.WriteLine($"  ⚠ world.json 스키마 v{w.SchemaVer} ≠ v{SchemaVer} (감독 모드 개편) — 새 세계로 시작."); return false; }

        static WorldV2? TryRead(string path)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<WorldV2>(File.ReadAllText(path), JsonOpts); }
            catch { return null; }
        }
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
        _matchLog.Clear(); if (w.MatchLog != null) _matchLog.AddRange(w.MatchLog);
        _lastSummary = w.LastSummary;
        _champions.Clear(); if (w.Champions != null) _champions.AddRange(w.Champions);
        _hall.Clear(); if (w.Hall != null) _hall.AddRange(w.Hall);
        _ludusRep = w.LudusRep; _glory = w.Glory; _pendingProposalOpp = w.PendingProposalOpp;
        _ludusName = string.IsNullOrWhiteSpace(w.LudusName) ? "내 루두스" : w.LudusName!;
        _mentorName = w.Mentor;
        _perks.Clear(); if (w.Perks != null) foreach (var p in w.Perks) _perks[p.Id] = (int)p.Rep;
        _rookieSeq = w.RookieSeq; _debt = w.Debt; _sparCount = w.SparCount;
        _achievements.Clear(); if (w.Achievements != null) foreach (var a in w.Achievements) _achievements.Add(a);
        _cupSeeds = w.CupSeeds ?? new(); _cupStage = w.CupStage; _cupChampion = w.CupChampion;
        _pendingEventId = w.PendingEventId; _pendingEventFighter = w.PendingEventFighter;
        _rivalRep.Clear();
        if (w.RivalReps != null) foreach (var lr in w.RivalReps) _rivalRep[lr.Id] = lr.Rep;
        foreach (var lid in _cast.Where(g => !g.IsPlayer).Select(g => g.LudusId).Distinct())
            _rivalRep.TryAdd(lid, 0f);   // 구세이브 호환 — 캐스트 소속에서 라이벌 루두스 복원
        _ledger.Load(w.Relations);
        return true;
    }

    private void SaveWorld()
    {
        try { if (File.Exists(WorldPath)) File.Copy(WorldPath, WorldPath + ".bak", true); } catch { }   // 저장 전 스냅샷
        File.WriteAllText(WorldPath, JsonSerializer.Serialize(new WorldV2(
            SchemaVer, ConstantsVer, _worldSeed, _gold, _gachaCount, _freeGachas,
            _trainingLv, _medicalLv, _quartersLv, _seasonsPlayed,
            SeasonActive, _seasonNo, _matchIdx, _cursor, _eventsAppended,
            _schedule.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList(),
            _eventDocs.ToList(),
            _cast.Select(ToRec).ToList(),
            _candidates.Count > 0 ? _candidates.Select(ToRec).ToList() : null,
            _ledger.Snapshot().ToList(),
            _matchLog.Count > 0 ? _matchLog.ToList() : null,
            _lastSummary,
            _champions.Count > 0 ? _champions.ToList() : null,
            _hall.Count > 0 ? _hall.ToList() : null,
            _ludusRep, _achievements.Count > 0 ? _achievements.ToList() : null,
            _cupSeeds.Count > 0 ? _cupSeeds.ToList() : null, _cupStage, _cupChampion,
            _pendingEventId, _pendingEventFighter,
            _rivalRep.Count > 0 ? _rivalRep.Select(kv => new LudusRepRec(kv.Key, kv.Value)).ToList() : null,
            _glory, _pendingProposalOpp, _ludusName, _mentorName,
            _perks.Count > 0 ? _perks.Select(kv => new LudusRepRec(kv.Key, kv.Value)).ToList() : null,
            _rookieSeq, _debt, _sparCount), JsonOpts));
    }

    private static GladRec ToRec(Gladiator g) => new(g.Id, g.Name, g.WeaponId, g.PersonalityId,
        g.TacticPool, g.TacticId,
        g.Stats.Atk, g.Stats.Def, g.Stats.HpMax, g.Stats.Spd, g.Stats.Aspd, g.Stats.Rct,
        (int)g.Talent, (int)g.Potential, g.TalentBudget, g.PotentialBudget,
        g.TraitIds, g.IsPlayer, g.Age, g.AgingStartAge, g.TrainingPoints, g.MatchCounter,
        g.CW, g.CL, g.CD, g.CKoW, g.Fame, g.Popularity,
        g.W, g.L, g.D, g.Streak, g.PendingEmotions.ToArray(), g.Fatigue, g.InjuryMatches, g.LudusId, g.Division, g.SeasonBrutals,
        g.MGrit, g.MRecover, g.MShow, g.MPay);

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
            W = r.W, L = r.L, D = r.D, Streak = r.Streak, Fatigue = r.Fatigue, InjuryMatches = r.InjuryMatches,
            LudusId = r.LudusId, Division = r.Division, SeasonBrutals = r.SeasonBrutals,
            MGrit = r.MGrit, MRecover = r.MRecover, MShow = r.MShow, MPay = r.MPay,
        };
        g.PendingEmotions.AddRange(r.PendingEmotions);
        return g;
    }

    // ── 상태 문서 ──

    private SeasonDoc BuildSeasonDoc()
    {
        var standings = Standings(1);   // 헤더 챔피언 = 1부 선두
        SchedRec? next = SeasonActive && _cursor < _schedule.Count ? _schedule[_cursor] : null;
        var fighters = _cast.Select(g => new FighterDoc(g.Id, g.Name,
            g.WeaponId.Replace("WPN_", ""), g.TacticId.Replace("TAC_", ""), g.PersonalityId.Replace("PER_", ""), g.Age,
            g.W, g.L, g.D, g.SeasonPoints, g.Streak, g.CW, g.CL, g.CD,
            MathF.Round(g.Fame), MathF.Round(g.Popularity), g.IsPlayer, Epithets(g),
            g.Fatigue, g.InjuryMatches > 0, g.Division)).ToList();
        var rels = _ledger.AllRelations(PersOf)
            .Select(x => new RelDoc(ById(x.Self).Name, ById(x.Opp).Name, RelationTable.Get(x.Type).Name,
                                    MathF.Round(x.State.Affinity), x.State.Wins, x.State.Losses)).ToList();
        int total = _schedule.Count;
        if (SeasonActive)
        {
            if (!_eventsAppended) total += Math.Max(2, _cast.Count / 2);   // 이벤트 미편성분
            if (_cupStage == 0 && _cast.Count >= 4) total += 3;           // 컵 미편성분(4강2+결승1)
        }
        return new SeasonDoc(SchemaVer, Math.Max(1, _seasonNo), _rounds, _matchIdx, total, !SeasonActive,
            next != null ? ById(next.A).Name : null, next != null ? ById(next.B).Name : null, next?.IsEvent ?? true,
            standings[0].Name, fighters, rels, _eventDocs.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList(),
            _matchLog.Select(e => new MatchLogDoc(e.Idx, e.Round, e.IsEvent, e.AName, e.BName, e.Winner, e.Reason, e.IsPlayerMatch)).ToList(),
            _champions.Count > 0 ? _champions.ToList() : null,
            _hall.Count > 0 ? _hall.OrderByDescending(h => h.Fame).ToList() : null);
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
            MathF.Round(g.Fame), MathF.Round(g.Popularity),
            g.PendingEmotions.Select(e => EmotionTable.Get(e).Name).ToArray(), Epithets(g),
            g.Fatigue, g.InjuryMatches > 0,
            BudgetUsed(g.Stats) + 1f > g.PotentialBudget, BreakthroughCost(g),
            g.MGrit, g.MRecover, g.MShow, g.MPay)).ToList();

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
            string? vsRecord = null, relName = null; string[]? myEmo = null; bool oppKiter = false;
            if (mine != null)
            {
                var h2h = _ledger.Get(mine.Id, opp.Id);
                vsRecord = h2h.Encounters > 0 ? $"{h2h.Wins}승 {h2h.Losses}패" : "첫 대결";
                relName = h2h.Classify(mine.PersonalityId) is { } rt ? RelationTable.Get(rt).Name : null;
                myEmo = mine.PendingEmotions.Select(e => EmotionTable.Get(e).Name).ToArray();
                oppKiter = WeaponTable.Get(opp.WeaponId).Range >= 3.0f;
            }
            float myP = mine != null ? WinProb(mine, opp) : 0.5f;
            nm = new NextMatchDoc(s.Round, s.IsEvent, mine != null, A.Name, B.Name,
                mine?.Id, mine?.Name,
                mine?.TacticPool.Select(t => t.Replace("TAC_", "")).ToArray(),
                mine?.TacticId.Replace("TAC_", ""),
                mine != null ? new OppPreview(opp.Name, opp.WeaponId.Replace("WPN_", ""),
                    opp.PersonalityId.Replace("PER_", ""), opp.Age,
                    MathF.Round(opp.Fame), MathF.Round(opp.Popularity), $"{opp.CW}-{opp.CL}-{opp.CD}") : null,
                vsRecord, relName, myEmo, oppKiter,
                s.Kind == "cup_final" ? "🏆 챔피언십 컵 결승" : s.Kind == "cup_sf" ? "🏆 챔피언십 컵 4강"
                    : s.Format == "execution" ? "☠ 처형전 — 패자는 죽을 수 있다 (보상 ×3)"
                    : s.Format.StartsWith("same:") ? $"⚔ 무기 지정전 — 양측 {s.Format[5..].Replace("WPN_", "")}" : null,
                MathF.Round(myP * 100f), MathF.Round(1f / myP * 100f) / 100f, MathF.Round(1f / (1f - myP) * 100f) / 100f,
                mine != null && mine.Popularity >= opp.Popularity, mine != null ? MathF.Round(mine.Popularity + opp.Popularity) : 0f);
        }

        // 루두스 등급
        int tier = LudusTier();
        var ludus = new LudusDoc(MathF.Round(_ludusRep), tier, LudusTiers[tier].Name,
            tier + 1 < LudusTiers.Length ? LudusTiers[tier + 1].Name : null,
            tier + 1 < LudusTiers.Length ? LudusTiers[tier + 1].Rep : LudusTiers[tier].Rep,
            MathF.Round(IncomeMult * 100f) / 100f);

        // 업적 (달성/미달성 전부)
        var ach = AchievementDefs.Select(a => new AchDoc(a.Id, a.Name, a.Desc, _achievements.Contains(a.Id))).ToList();

        // 챔피언십 컵 대진 (진행 중이거나 방금 끝난 것)
        List<CupMatchDoc>? cup = null;
        if (_cupSeeds.Count > 0)
        {
            string Nm(string id) => _cast.FirstOrDefault(g => g.Id == id)?.Name ?? id;
            cup = _schedule.Where(s => s.Kind.StartsWith("cup")).Select(s =>
            {
                var log = _matchLog.FirstOrDefault(m => m.AId == s.A && m.BId == s.B && m.Round == s.Round);
                return new CupMatchDoc(s.Kind == "cup_final" ? "결승" : "4강", Nm(s.A), Nm(s.B),
                    log != null && log.Winner != "무승부" ? log.Winner : null);
            }).ToList();
        }

        return JsonSerializer.Serialize(new GameStateDoc(BuildSeasonDoc(), MathF.Round(_gold), _freeGachas, EffGachaCost,
            _trainingLv, _medicalLv, _quartersLv, RosterCap, SeasonActive, my, cands, nm, _lastSummary,
            ludus, ach, cup, PendingEventDoc(), BuildLudusTable(), MathF.Round(_glory), PendingProposalDoc(),
            _ludusName, _mentorName,
            PerkDefs.Select(p => new PerkDoc(p.Id, p.Name, p.Desc, PerkLv(p.Id), p.Max,
                PerkLv(p.Id) < p.Max ? p.Costs[PerkLv(p.Id)] : 0)).ToList(),
            MathF.Round(_debt), RomanDate()), JsonOpts);
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
        Console.WriteLine($"\n  🏆 리그 챔피언: {season[0].Name}" + (_cupChampion != null ? $"  ·  🏆 컵 우승: {_cupChampion}" : "") + "\n");

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

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
    private readonly string _worldPath = "world.json";   // 세이브 슬롯: 슬롯별 world{n}.json

    // ── 경제 상수 (초안 — 튜닝 전제) ──
    private const float GachaCost = 100f, StartGold = 50f;
    private const int StartFreeGachas = 2;
    private const float FeeBase = 5f, FeePopScale = 0.05f, WinBonus = 10f, KoBonus = 3f, DramaBonus = 5f;
    private static readonly float[] RankBonus = { 150f, 100f, 60f };   // 1~3위, 이하 20
    private const float SalaryBase = 10f, SalaryFameScale = 0.10f;   // 인플레 흡수: 스타는 정말 비싸다 (0.03→0.10)
    private const float UpkeepPerFacLv = 8f;                          // 시설 유지비(레벨당/시즌) — 증축의 지속 비용
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
        int RookieSeq = 0, float Debt = 0f, int SparCount = 0,   // 신인 시리얼·사채·스파링 카운터
        EdictRec? Edict = null, bool EdictDone = false,   // 황제의 특명
        List<GreatRec>? Greatest = null,   // 명경기 보관함
        int BetCursor = -1, int BetSide = 0, float BetAmount = 0f, float BetOdds = 0f,   // 도박장
        int Favor = 0, int FavorLv = 0, bool ProposalExec = false,   // 황제 총애·도전장
        float SeasonBetNet = 0f, int GauntletStage = 0, int GauntletWins = 0,   // 베팅 수지·초청전
        List<ArchRec>? Archive = null,   // 관전 아카이브(지난 시즌 경기)
        string? MasterName = null, string? MasterTrait = null, string? MasterTactic = null,   // 스승 전수 대기
        int ScoutLevel = 0, float[]? AxisCapBonus = null,   // 스카우터·교관 유산
        int BetHits = 0, float Patronage = 0f,   // 베팅 누적 적중·후원자 관계
        List<BetLogRec>? BetLog = null, int StreetSeq = 0);   // 베팅 이력·거리 시비 카운터
    private sealed record LudusRepRec(string Id, float Rep);

    // ── season.json / API 문서 ──
    private sealed record EventDoc(string A, string B, float Score, string Winner, bool Ko);
    private sealed record FighterDoc(string Id, string Name, string Weapon, string Tactic, string Personality, int Age,
        int W, int L, int D, int Points, int Streak, int CW, int CL, int CD, float Fame, float Popularity, bool IsPlayer,
        string[]? Epithets = null, int Fatigue = 0, bool Injured = false, int Division = 1, int CKoW = 0);
    private sealed record RelDoc(string Self, string Opp, string Type, float Affinity, int Wins, int Losses);
    private sealed record StoryDoc(int Round, string Kind, string Text);
    private sealed record SeasonDoc(int SchemaVer, int SeasonNo, int Rounds, int Matches, int TotalMatches, bool Completed,
        string? NextA, string? NextB, bool NextIsEvent, string Champion,
        List<FighterDoc> Fighters, List<RelDoc> Relations, List<EventDoc> Events, List<StoryDoc> Story,
        List<MatchLogDoc> MatchLog, List<ChampionRec>? Champions = null, List<HallRec>? Hall = null,
        List<CalDoc>? Calendar = null, int Auc = 0);   // 달력: 전 일정(과거+미래)+로마 날짜
    private sealed record CalDoc(int Idx, string Month, int Day, string A, string B, string Kind, string Format,
        string? Winner, bool IsPlayerMatch, bool IsNext, float Hype);   // Idx = 재관전용 matchLog 인덱스(미래 경기는 -1)

    private sealed record StatsDoc(float Atk, float Def, float Hp, float Spd, float Aspd, float Rct);
    private sealed record MyFighterDoc(string Id, string Name, string Weapon, string Personality, int Age, bool Aging,
        string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] TacticPool, string Tactic, int TrainingPoints,
        int W, int L, int D, int CW, int CL, int CD, float Fame, float Popularity,
        string[] Emotions,    // 다음 경기에 실릴 감정 (💭 예고)
        string[]? Epithets = null,    // 획득 이명
        int Fatigue = 0, bool Injured = false,   // 피로도(0쌩쌩~100탈진)·부상 여부
        bool AtCap = false, int BreakthroughCost = 0,   // 상한 도달·잠재력 돌파 비용(영광)
        int MGrit = 0, int MRecover = 0, int MShow = 0, int MPay = 0,   // 마스터리 레벨
        int CKoW = 0);   // 통산 KO승(스카우터 은퇴 자격 표시)
    private sealed record CandidateDoc(int Idx, string Name, string Weapon, string Personality, string RevealedTactic, int Age, string[]? Hints = null); // 마스킹! (나이 공개·스카우터 힌트)
    private sealed record RevealDoc(string Name, string Weapon, string Personality, int Age, string Talent, string Potential, string[] Traits, string? JoinedRival);
    private sealed record OppPreview(string Name, string Weapon, string Personality, int Age, float Fame, float Popularity, string Career);
    private sealed record NextMatchDoc(int Round, bool IsEvent, bool IsPlayerMatch,
        string AName, string BName, string? MyId, string? MyName, string[]? MyPool, string? MyTactic, OppPreview? Opp,
        string? MyVsOpp = null,       // 이 상대와의 상대전적 "2승 1패"
        string? MyRelation = null,    // 내가 상대를 보는 관계 (원수/공포/라이벌…) — 복수전 예고
        string[]? MyEmotions = null,  // 이번 경기에 실리는 감정
        bool OppIsKiter = false,      // 상성 힌트: 상대가 장거리 카이터인가
        string? Stage = null,         // 컵 단계 라벨 (4강 결승) — 정규경기는 null
        float MyWinPct = 50f, float MyOdds = 2f, float OppOdds = 2f,   // 배당(파워 모델 — 표시용)
        bool CrowdFavorsMe = false, float Hype = 0f,   // 군중 선호(인기)·흥행지수
        float OddsA = 2f, float OddsB = 2f,   // 범용 배당(A/B 기준 — AI 경기 베팅용)
        float FeeEstimate = 0f, float WinBonusEstimate = 0f);   // 예상 출전료·승리 보너스(#15 수익 가시화)
    private sealed record LudusDoc(float Rep, int Tier, string TierName, string? NextTierName, float NextTierRep, float IncomeMult);
    private sealed record AchDoc(string Id, string Name, string Desc, bool Unlocked);
    private sealed record CupMatchDoc(string Stage, string A, string B, string? Winner);
    private sealed record LudusStandingDoc(string Name, float Rep, string TierName, int Members,
        string? TopFighter, int SeasonW, int SeasonL, int SeasonD, bool IsPlayer, float Treasury);
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
        float Debt = 0f, string RomanDate = "",   // 사채·로마력 날짜(시간감각)
        EdictDoc? Edict = null,   // 황제의 특명(시즌 계약)
        List<GreatDoc>? Greatest = null,   // 명경기 보관함
        BetDoc? PendingBet = null, int Favor = 0,   // 도박장·황제 총애
        bool HasMyMatchAhead = false,   // 남은 일정에 내 경기가 있는가(스킵 버튼 노출)
        List<RevealDoc>? RecruitReveal = null,   // 직전 영입에서 공개된 미선택 후보(#8)
        string? MasterPending = null, int ScoutLevel = 0, string? Legacy = null,   // 은퇴 유산(#10)
        float Patronage = 0f,   // 후원자 관계도(#7)
        GambleDoc? Gamble = null);   // 도박장 탭(#32)
    private sealed record GambleDoc(float SeasonNet, int Hits, int Total, List<BetLogRec> Log);
    private sealed record EdictDoc(string Desc, bool Done);
    private sealed record BetDoc(string On, float Amount, float Odds);
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
        float Income, string IncomeNote, List<MyDelta>? Mine = null, List<string>? Fates = null,
        float Hype = 0f, List<string>? Injuries = null,   // 흥행도·부상자(AI 결과 카드 #4)
        bool Upset = false, float WinnerOdds = 0f,        // 대이변·승자의 경기 전 배당(잭팟 연출 — 0=산출 불가)
        bool BetWon = false, string? BetNote = null);     // 이 경기 베팅 정산(결과 카드 연계)

    /// <summary>경기 로그 1건 — 당시 선수 스냅샷 + 시드 = 결정론 재관전([2] ERD FighterSnapshot 원칙).</summary>
    private sealed record LogEntry(int Idx, int Round, bool IsEvent, string AId, string BId, string AName, string BName,
        string Winner, string Reason, bool IsPlayerMatch, ulong Seed, FighterDef DefA, FighterDef DefB);
    private sealed record MatchLogDoc(int Idx, int Round, bool IsEvent, string A, string B, string Winner, string Reason, bool IsPlayerMatch);
    private sealed record ArchRec(int Season, LogEntry Entry);   // 관전 아카이브 1건(시즌 태그)
    private sealed record ArchDoc(int Idx, int Season, int Round, bool IsEvent, string A, string B, string Winner, string Reason, bool Mine);
    private sealed record GreatRec(int Season, float Drama, LogEntry Entry);   // 명경기(시즌 넘어 영속)
    private sealed record GreatDoc(int Idx, int Season, string A, string B, string Winner, string Reason, float Drama);

    /// <summary>시즌 종료 요약 (연출 화면용 — 프리시즌 동안 표시, 영속).</summary>
    private sealed record RankRow(int Rank, string Name, int W, int L, int D, int Points, bool IsPlayer);
    private sealed record SeasonSummaryDoc(int SeasonNo, string Champion, bool ChampionIsMine, List<RankRow> Standings,
        int MyBestRank, float RankBonus, float Salary, float GoldAfter,
        List<string> AgingNotes, int Revenge, int Upsets, int Comebacks, string TopFame,
        List<string>? Retirements = null,
        string? CupChampion = null, bool CupChampionMine = false, List<string>? NewAchievements = null,
        List<string>? FateNotes = null, List<string>? PromoNotes = null, string? EdictNote = null,
        float BetNet = 0f, int GreatCount = 0, int Favor = 0, int GauntletWins = 0);   // 결산 대통합

    /// <summary>세계 역사 — 역대 챔피언·명예의 전당(은퇴자) 영속 기록.</summary>
    private sealed record ChampionRec(int SeasonNo, string Name, string Record, bool IsPlayer);
    private sealed record HallRec(string Name, string Weapon, float Fame, string Career, int Age, int RetiredSeason, bool IsPlayer);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── 상태 ──
    private readonly List<Gladiator> _cast = new();
    private readonly List<Gladiator> _candidates = new();     // 대기 뽑기 후보 (전체 데이터 — JSON엔 마스킹)
    private readonly List<RevealDoc> _lastReveal = new();     // 직전 영입에서 공개된 미선택 후보(#8, 메모리 전용)
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
    private readonly List<ArchRec> _archive = new();      // 지난 시즌들 경기 아카이브(영속, 최근 400 롤링)
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
    private float _patronage;   // 후원자 관계도(−100 압박 ~ +100 총애) — #7. 선택으로 변동, 시즌말 정산
    private void Patron(float d) => _patronage = Math.Clamp(_patronage + d, -100f, 100f);
    private string? _pendingProposalOpp;                     // 빅매치 제안(감독 개입) — 출전 선택 대기 상대 id
    private bool _proposalExec;                              // 제안이 원수의 처형전 도전장인가
    private readonly List<string> _lastFates = new();        // 직전 경기의 극적 운명(결과 화면 표시용)
    private readonly List<string> _lastInjuries = new();      // 직전 경기 신규 부상자(결과 카드 표시)
    private float _lastHype;                                   // 직전 경기 흥행도
    private bool _lastUpset;                                   // 직전 경기 대이변 여부(잭팟 연출)
    private float _lastDrama;                                // 직전 경기 드라마 스코어(명경기 보관 판정)
    private readonly List<GreatRec> _greatest = new();       // 명경기 보관함(top 12, 영속 — 스냅샷+시드 재관전)
    private int _rookieSeq;                                  // 신인 id 시리얼(중복 방지, 영속)
    private float _debt;                                     // 사채(이벤트 빚) — 시즌말 이자·상환·명성 압박
    private readonly Dictionary<string, float> _rivalRep = new();   // 라이벌 루두스별 명성(경쟁 순위표)
    private int _emoGen;

    // ── 업적 정의 (조건은 코드에서 체크) ──
    // 업적: 보상 차등(골드·영광·명성). 종류·보상 다양화(#5).
    private static readonly (string Id, string Name, string Desc, float Gold, float Glory, float Rep)[] AchievementDefs =
    {
        ("first_win",    "첫 승리",       "내 검투사의 첫 승",           50f,  2f,  10f),
        ("first_title",  "리그 제패",     "리그 시즌 우승",              200f, 10f, 30f),
        ("first_cup",    "챔피언십 정복", "챔피언십 컵 우승",            300f, 14f, 40f),
        ("caesar",       "카이사르 발굴", "카이사르 천부 영입",          0f,   12f, 20f),
        ("legend",       "살아있는 전설", "내 검투사 명성 100 돌파",     0f,   16f, 30f),
        ("streak10",     "무패의 투사",   "내 검투사 10연승",            150f, 8f,  20f),
        ("empire",       "제국의 정점",   "루두스 최고 등급 달성",       0f,   20f, 0f),
        ("dynasty",      "왕조",          "리그 3연패",                  500f, 25f, 50f),
        // 신규(#5)
        ("executioner",  "콜로세움의 사형집행인", "처형전에서 승리",      200f, 8f,  25f),
        ("gambler",      "행운의 도박사",  "베팅 누적 10회 적중",        300f, 6f,  10f),
        ("giant_killer", "거인 사냥꾼",   "명성 2배 이상 상대 격파(이변)", 100f, 10f, 20f),
        ("kingmaker",    "명장의 산실",   "교관·스승·스카우터 배출",     0f,   12f, 25f),
        ("perfect",      "무결점 시즌",   "시즌 전승(내 검투사 전원)",   400f, 20f, 40f),
        ("tycoon",       "대부호",        "금고 2000 데나리우스 돌파",   0f,   10f, 15f),
        ("veteran",      "백전노장",      "내 검투사 통산 50승",         200f, 12f, 25f),
    };
    private int _betHits;   // 베팅 누적 적중(gambler 업적)

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
            // 재화: 내 루두스는 실제 골드, 라이벌은 명성·간판 인기 기반 추정 금고(관전 흥미용 근사)
            float treasury = isPlayer ? _gold : MathF.Round(rep * 6f + m.Sum(x => x.Popularity) * 2f);
            list.Add(new LudusStandingDoc(LudusNameOf(id), MathF.Round(rep), TierNameForRep(rep),
                m.Count, top?.Name, m.Sum(x => x.W), m.Sum(x => x.L), m.Sum(x => x.D), isPlayer, treasury));
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
        var rw = new List<string>();
        if (def.Gold > 0) { _gold += def.Gold; rw.Add($"💰{def.Gold:F0}"); }
        if (def.Glory > 0) { AddGlory(def.Glory); rw.Add($"✨{def.Glory:F0}"); }
        if (def.Rep > 0) { _ludusRep += def.Rep; rw.Add($"명성 +{def.Rep:F0}"); }
        _story.Add((0, "achievement", $"🏅 업적 — {def.Name}: {def.Desc} ({string.Join(" ", rw)})"));
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
        string OppCareer, ProposalPickDoc[] Roster, bool Execution = false);
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
            Body = n => $"{n}이(가) 땀에 젖은 채 훈련장에 남아 감독을 노려본다.\n💬 {n}: \"더 강해질 수 있습니다. 몸이 부서지더라도 — 허락해 주십시오.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("강행군 (훈련 포인트 +2, 인기 −5)", g => { g!.TrainingPoints += 2; g.Popularity = MathF.Max(0, g.Popularity - 5); return $"{g.Name} 훈련 포인트 +2, 인기 −5"; }),
                ("휴식 (인기 +5)", g => { g!.Popularity += 5; return $"{g.Name} 인기 +5"; }) } },

        new EvtTemplate { Id = "patron", Icon = "💰", Title = "후원자의 제안", NeedsFighter = false,
            Body = _ => "부유한 원로원 의원 그라쿠스가 두둑한 금화 주머니를 탁자에 던진다.\n💬 그라쿠스: \"자네 루두스의 이름을 내 연회에 좀 빌리세. 서로 좋은 거래 아닌가?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받는다 (골드 +80, 명성 −15, 후원 +15)", _ => { _gold += 80f; _ludusRep = MathF.Max(0, _ludusRep - 15f); Patron(15f); return "골드 +80, 명성 −15, 후원 +15"; }),
                ("거절한다 (명성 +20, 후원 −10)", _ => { AddRep(20f); Patron(-10f); return "명성 +20, 후원 −10 — \"고집스러운 친구로군.\""; }) } },

        // ── 신규 미션(#13) — 수락/거절 · 대사 포함(#9) · 일부는 후원 관계(#7) 변동 ──
        new EvtTemplate { Id = "fix", Icon = "🎲", Title = "승부조작 제안", NeedsFighter = true,
            Body = n => $"복면의 사내가 도박장의 뒷돈 냄새를 풍기며 다가온다.\n💬 복면인: \"다음 경기, {n}이(가) 져주기만 하면 되네. 이 금화는 침묵의 대가야.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("수락 (골드 +150, 발각 위험·명성 급락 가능)", g => { _gold += 150f;
                    var rng = new SimRandom(SeasonSeed ^ 0xF15E_D000UL + (ulong)_matchIdx); if (rng.Roll(0.35f)) { _ludusRep = MathF.Max(0, _ludusRep - 40f); Patron(-25f); return "골드 +150 — ⚠ 발각! 명성 −40, 후원 −25 (더러운 소문이 퍼졌다)"; } return "골드 +150 — 아무도 모른다… 아직은"; }),
                ("거절 (명성 +15, 후원 +10)", g => { AddRep(15f); Patron(10f); return "명성 +15, 후원 +10 — \"청렴한 라니스타라, 흔치 않지.\""; }) } },

        new EvtTemplate { Id = "tribute", Icon = "🏛", Title = "총독의 조공 요구", NeedsFighter = false,
            Body = _ => "속주 총독의 전령이 두루마리를 펼친다.\n💬 전령: \"총독께서 검투 흥행세를 인상하셨소. 성의를 보이는 게 좋을 거요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("바친다 (골드 −70 · 부족분 빚, 후원 +20)", _ => { var pay = SpendOrDebt(70f); Patron(20f); return $"{pay}, 후원 +20 — 총독의 눈에 들었다"; }),
                ("버틴다 (후원 −20, 다음 시즌 압박)", _ => { Patron(-20f); return "후원 −20 — \"기억해 두겠소.\" (관계 악화)"; }) } },

        new EvtTemplate { Id = "duel", Icon = "⚔", Title = "결투 신청", NeedsFighter = true,
            Body = n => $"경쟁 검투소의 투사가 {n}의 면전에 장갑을 던진다.\n💬 도전자: \"소문난 실력, 모래 위에서 증명해보시지. 겁이 나거든 물러서든가.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받아들인다 (인기 +14, 다음 경기 '투지')", g => { g!.Popularity += 14f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated); return $"{g.Name} 인기 +14, 다음 경기 '투지'"; }),
                ("품위있게 거절 (명성 +5, 인기 −4)", g => { g!.Fame += 5f; g.Popularity = MathF.Max(0, g.Popularity - 4f); return $"{g.Name} 명성 +5, 인기 −4"; }) } },

        new EvtTemplate { Id = "brawl", Icon = "🍺", Title = "술집 시비", NeedsFighter = true,
            Body = n => $"선술집에서 취객 무리가 {n}의 탁자를 걷어찬다.\n💬 취객: \"검투장 밖에선 별 것 아니구만? 어디 한 번 놀아보자고!\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("주먹으로 답한다 (인기 +10, 부상 위험)", g => {
                    var rng = new SimRandom(SeasonSeed ^ 0xB4A_1234UL + (ulong)_matchIdx * 17UL);
                    g!.Popularity += 10f;
                    if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated);
                    if (rng.Roll(0.30f)) { g.InjuryMatches = Math.Max(g.InjuryMatches, 1); return $"{g.Name} 인기 +10, 다음 경기 '투지' — 하지만 난투 중 부상(1경기)"; }
                    return $"{g.Name} 취객들을 때려눕혔다 — 인기 +10, 다음 경기 '투지'"; }),
                ("자리를 뜬다 (인기 −4)", g => { g!.Popularity = MathF.Max(0, g.Popularity - 4f); return $"{g.Name} 조용히 물러났다 — 인기 −4"; }) } },

        new EvtTemplate { Id = "temple", Icon = "🏛", Title = "신전 봉헌", NeedsFighter = false,
            Body = _ => "마르스 신전의 사제가 향을 피우며 청한다.\n💬 사제: \"승리의 신께 봉헌하라, 라니스타여. 신들은 관대한 자를 굽어살피신다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("봉헌한다 (골드 −50 · 부족분 빚, ✨+3)", _ => { var pay = SpendOrDebt(50f); AddGlory(3f); return $"{pay}, ✨+3 — 신들의 가호"; }),
                ("검약한다 (골드 보존)", _ => "정중히 향만 올렸다.") } },

        new EvtTemplate { Id = "crowd", Icon = "🎭", Title = "군중의 갈망", NeedsFighter = true,
            Body = n => $"관중석에서 {n}의 이름을 연호하는 함성이 터진다.\n💬 흥행주: \"군중이 피와 볼거리를 원하네! 자네 검투사, 쇼를 보여줄 수 있겠나?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("응한다 (인기 +12, 다음 경기 흥분)", g => { g!.Popularity += 12f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated); return $"{g.Name} 인기 +12, 다음 경기 '동기부여'"; }),
                ("침착하게 (명성 +8)", g => { g!.Fame += 8f; return $"{g.Name} 명성 +8"; }) } },

        new EvtTemplate { Id = "taunt", Icon = "😤", Title = "라이벌의 조롱", NeedsFighter = true,
            Body = n => $"광장에서 한 검투사가 침을 뱉으며 비웃는다.\n💬 라이벌: \"{n}? 겁쟁이한테 붙은 과분한 이름이지. 모래 위에서 울게 해주마.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("맞받아친다 (인기 +6, 다음 경기 원한)", g => { g!.Popularity += 6f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Grudge); return $"{g.Name} 인기 +6, 다음 경기 '원한'"; }),
                ("무시한다 (명성 +6)", g => { g!.Fame += 6f; return $"{g.Name} 명성 +6"; }) } },

        new EvtTemplate { Id = "mentor", Icon = "📜", Title = "노장의 지도", NeedsFighter = true,
            Body = n => $"한쪽 눈에 흉터가 있는 노검투사가 {n}을 지켜보다 입을 연다.\n💬 노장: \"자네, 재능은 있군. 허나 다듬지 않은 검은 무디지. 며칠만 내게 맡겨보게 — 공짜는 아니네만.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("수련한다 (골드 −40 · 부족분은 빚)", g => { var pay = SpendOrDebt(40f); var r = NudgeStat(g!, "Rct", 3f); return $"{pay}, {g!.Name} {r}"; }),
                ("사양한다", g => "정중히 사양했다.") } },

        new EvtTemplate { Id = "blackmarket", Icon = "🗡", Title = "암시장 무기상", NeedsFighter = true,
            Body = n => $"후드를 쓴 상인이 천을 걷어 시퍼런 칼날을 드러낸다.\n💬 무기상: \"{n}에게 딱이지. 규정보다 조금… 예리할 뿐이야. 심판이 눈치채지만 않으면 돼.\"",
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
            o.Age, MathF.Round(o.Fame), $"{o.CW}-{o.CL}-{o.CD}", roster, _proposalExec);
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
        _schedule.Insert(_cursor, new SchedRec(round, me.Id, opp.Id, true, 0f, "proposal",
            _proposalExec ? "execution" : "normal"));   // 다음 경기로 삽입(전시 — 도전장이면 ☠처형전)
        _story.Add((0, "proposal", _proposalExec
            ? $"☠ 처형전 성사 — {me.Name} vs {opp.Name}. 둘 중 하나는 걸어 나오지 못할 수 있다"
            : $"🎤 빅매치 성사 — {me.Name} vs {opp.Name}(도전장)"));
        _pendingProposalOpp = null; _proposalExec = false; SaveWorld();
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
        // 난입 술집시비(#2/#14): "주먹으로 답한다"(choice 0)면 실제 경기(술집 배경)로 붙는다 → viewer.json
        object? fight = null;
        if (t.Id == "brawl" && choiceIdx == 0 && subj != null)
        {
            var brng = new SimRandom(SeasonSeed ^ 0xB4A4_5EED + (ulong)(_streetSeq++) * 11UL);
            var foe = _cast.Where(x => !x.IsPlayer).OrderBy(_ => brng.NextUInt64()).FirstOrDefault();
            if (foe != null)
            {
                RunExhibition(subj, foe, brng.NextUInt64());
                fight = new { venue = "bar", a = subj.Name, b = foe.Name };
            }
        }
        string outcome = t.Choices[choiceIdx].Apply(subj);
        _story.Add((0, "event_choice", $"{t.Icon} {t.Title} — {outcome}"));
        _pendingEventId = _pendingEventFighter = null;
        SaveWorld();
        return JsonSerializer.Serialize(new { ok = true, title = t.Title, outcome, fight }, JsonOpts);
    }

    public Game(int roundsPerSeason, ulong? worldSeed = null, bool fresh = false,
                bool interactive = true, bool playerless = false, string worldPath = "world.json")
    {
        _rounds = roundsPerSeason;
        _interactive = interactive;
        _playerless = playerless;
        _worldPath = worldPath;

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
        // 천재(#16): 잠재력 상한 ×1.15 — 성장 여력이 근본적으로 크다
        float potBudget = traits.Contains(TraitTable.Genius) ? end.PotentialBudget * 1.15f : end.PotentialBudget;
        // 저속노화(#16): 노화 시작을 늦춘다(+4세)
        int agingStart = 30 + (int)(rng.NextFloat01() * 7) + (traits.Contains(TraitTable.SlowAge) ? 4 : 0);
        return new Gladiator
        {
            Id = id, Name = name, WeaponId = wpn, PersonalityId = per,
            TacticPool = pool, TacticId = pool[0],
            Stats = end.Stats, Talent = end.Talent, Potential = end.Potential,
            TalentBudget = end.TalentBudget, PotentialBudget = potBudget,
            TraitIds = traits, IsPlayer = isPlayer,
            Age = ageMin + (int)(rng.NextFloat01() * (ageMax - ageMin + 1)),
            AgingStartAge = agingStart,   // 30~36 (+저속노화 4)
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
    private FighterStats WithAxis(in FighterStats s, int a, float pts)
    {
        float v = Math.Clamp(AxisVal(s, a) + pts, 20f, 150f + _axisCapBonus[a]);
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
        _cupStage = 0; _cupSeeds = new(); _cupChampion = null; _seasonNewAch.Clear(); _oddsCursor = -1;
        _seasonBetNet = 0f; _gauntletStage = 0; _gauntletWins = 0;
        // 관전 아카이브(#1): 직전 시즌 경기를 시즌 태그와 함께 영속 보관(재관전용). 최근 400경기로 롤링(파일 비대 방지)
        foreach (var e in _matchLog) _archive.Add(new ArchRec(Math.Max(1, _seasonsPlayed), e));
        while (_archive.Count > 400) _archive.RemoveAt(0);
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

        RollEdict();   // 황제의 특명(시즌 계약)

        // 빅매치 제안(감독 개입): 원수의 처형전 도전장(우선) 또는 명망 도전자와의 전시 카드.
        _pendingProposalOpp = null; _proposalExec = false;
        if (!_playerless && _cast.Count(g => g.IsPlayer) >= 2)
        {
            var pRng = new SimRandom(SeasonSeed ^ 0x0B16_A7C4UL);
            // ☠ 원수의 도전장: 내 선수를 '원수'로 여기는 AI가 있으면 50%로 처형전을 걸어온다 (관계 발화)
            var nemesis = _cast.Where(ai => !ai.IsPlayer && _cast.Any(my => my.IsPlayer &&
                _ledger.Get(ai.Id, my.Id).Classify(ai.PersonalityId) == RelationType.Nemesis)).FirstOrDefault();
            if (nemesis != null && pRng.Roll(0.5f))
            {
                _pendingProposalOpp = nemesis.Id; _proposalExec = true;
                _story.Add((0, "proposal", $"☠ 도전장 — 원수 {nemesis.Name}이(가) 처형전을 요구한다!"));
            }
            else if (pRng.Roll(0.6f))
                _pendingProposalOpp = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault()?.Id;
        }
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
            float upkeep = ((_trainingLv - 1) + (_medicalLv - 1) + _quartersLv) * UpkeepPerFacLv;   // 시설 유지비
            salaryPaid += upkeep;
            _gold = MathF.Max(0f, _gold - salaryPaid);
            _story.Add((_rounds + 1, "season", $"💰 시즌 정산 — 순위 보너스 +{bonusPaid:F0} · 급여·유지비 −{salaryPaid:F0}{(upkeep > 0 ? $"(시설 {upkeep:F0})" : "")} (잔고 {_gold:F0})"));

            // 후원자 정산(#7): 높은 관계 = 시즌말 하사금, 낮은 관계 = 압박(명성 삭감). 관계는 매 시즌 중앙으로 감쇠.
            if (_patronage >= 40f) { float gift = MathF.Round(_patronage * 2f); _gold += gift; _story.Add((_rounds + 1, "patron", $"💰 후원자의 하사 — 관계 {_patronage:F0} → 금화 +{gift:F0} (\"올해도 즐거웠네.\")")); }
            else if (_patronage <= -40f) { float pen = MathF.Round(-_patronage * 0.5f); _ludusRep = MathF.Max(0f, _ludusRep - pen); _story.Add((_rounds + 1, "patron", $"🗡 후원자의 냉대 — 관계 {_patronage:F0} → 루두스 명성 −{pen:F0} (뒷말이 돈다)")); }
            _patronage *= 0.6f;

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
                if (g.TraitIds.Contains(TraitTable.SlowAge)) relief = Math.Min(0.9f, relief + 0.4f);   // 저속노화(#16): 감소폭 −40%p
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
        // 무결점 시즌: 내 검투사 전원이 정규 시즌 무패(최소 1경기 이상)
        var myFighters = _cast.Where(g => g.IsPlayer).ToList();
        if (myFighters.Count > 0 && myFighters.All(g => g.L == 0 && g.W + g.D > 0)) Unlock("perfect");

        // 특명 미달성 = 황제의 실망(루두스 명성 하락)
        if (_edict != null && !_edictDone)
        {
            _ludusRep = MathF.Max(0f, _ludusRep - EdictFailRep);
            _favor = Math.Max(0, _favor - 1);   // 총애도 식는다
            _story.Add((_rounds + 1, "edict", $"📜 특명 실패 — \"{_edict.Desc}\" · 황제의 실망 (루두스 명성 −{EdictFailRep:F0})"));
        }
        _edict = null; _edictDone = false;

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
            _seasonNewAch.Count > 0 ? _seasonNewAch.ToList() : null,
            // 결산 대통합: 운명·승강·특명·베팅·명경기·총애 — 시즌 한 편의 마침표
            FateNotes: _story.Where(s => s.Kind is "death" or "grave_injury" or "awakening" or "persona" or "tradeoff")
                .Select(s => s.Text).ToList() is { Count: > 0 } fn ? fn : null,
            PromoNotes: _story.Where(s => s.Kind is "promote" or "relegate").Select(s => s.Text).ToList() is { Count: > 0 } pn ? pn : null,
            EdictNote: _story.Where(s => s.Kind == "edict" && (s.Text.Contains("달성") || s.Text.Contains("실패")))
                .Select(s => s.Text).LastOrDefault(),
            BetNet: MathF.Round(_seasonBetNet),
            GreatCount: _story.Count(s => s.Kind == "greatest"),
            Favor: _favor, GauntletWins: _gauntletWins);

        SaveWorld();
    }

    /// <summary>시즌 중 선수 제거 시 잔여 일정에서 그 선수 경기를 뺀다(#3 — 은퇴/방출 중도 허용).</summary>
    private void PurgeRemainingMatches(string fid)
    {
        if (!SeasonActive) return;
        for (int i = _schedule.Count - 1; i >= _cursor; i--)
            if (_schedule[i].A == fid || _schedule[i].B == fid) _schedule.RemoveAt(i);
    }

    /// <summary>방출(#3 시즌 중에도 가능): 관계 청산 + 잔여 일정 정리.</summary>
    public string ReleaseJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        PurgeRemainingMatches(g.Id);
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        _story.Add((0, "release", $"👋 방출 — {g.Name}이(가) 루두스를 떠났다"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    private int _sparCount;   // 스파링 시드 카운터(영속 — 결정론)

    // ── 콜로세움 도박장 — AI 경기에 골드 베팅(내 경기 금지=승부조작 방지). 배당은 베팅 시점 고정 ──
    private int _betCursor = -1, _betSide; private float _betAmount, _betOdds;
    private sealed record BetLogRec(int Season, string On, float Amount, float Odds, bool Won, float Payout);
    private readonly List<BetLogRec> _betLog = new();   // 베팅 이력(최근 60, 영속)

    /// <summary>승률 p → 표시 배당. 하우스 마진(8%)을 배당에 내장 → payout=amount×odds(이중 공제 없음).
    /// 하한 1.20(대세팀도 최소 +20% 회수), 상한 6.0(약체 대박). 저배당 무이득 버그 해소.</summary>
    private static float BetOdds(float p) => Math.Clamp(0.92f / Math.Clamp(p, 0.08f, 0.95f), 1.2f, 6f);

    /// <summary>다음 AI 경기에 베팅: side 0=A/1=B. 경기당 1회, 배당 고정.</summary>
    public string BetJson(int side, float amount)
    {
        if (!SeasonActive || _cursor >= _schedule.Count) return Err("다음 경기가 없다");
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        if (A.IsPlayer || B.IsPlayer) return Err("내 루두스 경기엔 걸 수 없다 (승부조작 금지)");
        if (_betCursor == _cursor) return Err("이미 이 경기에 걸었다");
        if (side is < 0 or > 1) return Err("잘못된 선택");
        amount = MathF.Floor(MathF.Min(amount, MathF.Floor(_gold)));   // 전액 베팅 안전: 잔고 이하로 클램프(부동소수 오탐 방지)
        if (amount < 5) return Err("최소 5 데나리우스 (잔고 부족)");
        float pA = CursorProbA();   // 시뮬 기반(상성 반영) — 파워식은 예측력 없음(MAE 35%p)
        float odds = BetOdds(side == 0 ? pA : 1f - pA);
        _gold -= amount; _seasonBetNet -= amount;
        _betCursor = _cursor; _betSide = side; _betAmount = amount; _betOdds = odds;
        _story.Add((s.Round, "bet", $"🎲 베팅 — {(side == 0 ? A.Name : B.Name)}에 {amount:F0} (배당 {odds:F2})"));
        SaveWorld();
        return StateJson();
    }

    // ── 이적 시장(라이벌 루두스 v2) — 프리시즌 전용. 목록·제안은 시드 파생(저장 불필요, 재조회 일관) ──
    private sealed record TransferBuyDoc(string Id, string Name, string Weapon, string Personality, int Age,
        float Fame, int Division, string Ludus, int Price);
    private sealed record TransferSellDoc(string Id, string Name, string Buyer, int Offer);
    private float TransferPrice(Gladiator g) =>
        MathF.Round(BudgetUsed(g.Stats) * 0.6f + g.Fame * 1.5f + MathF.Max(0, 30 - g.Age) * 4f);

    /// <summary>이적 시장 조회: 매물 AI 2~3명 + 내 스타에 대한 인수 제안(있으면).</summary>
    public string TransfersJson()
    {
        if (_playerless) return Err("CLI 모드");
        if (SeasonActive) return Err("이적은 프리시즌에만");
        var rng = new SimRandom(_worldSeed ^ 0x7124_5FE2UL + (ulong)_seasonsPlayed * 41UL);
        var pool = _cast.Where(g => !g.IsPlayer).OrderBy(_ => rng.NextUInt64()).Take(3)
            .Select(g => new TransferBuyDoc(g.Id, g.Name, g.WeaponId.Replace("WPN_", ""), g.PersonalityId.Replace("PER_", ""),
                g.Age, MathF.Round(g.Fame), g.Division, LudusNameOf(g.LudusId), (int)TransferPrice(g))).ToList();

        TransferSellDoc? sell = null;
        var star = _cast.Where(g => g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault();
        if (star != null && star.Fame >= 20f && rng.Roll(0.6f))
        {
            var buyer = ActiveRivalLudi.OrderBy(_ => rng.NextUInt64()).First();
            sell = new TransferSellDoc(star.Id, star.Name, buyer.Name, (int)(TransferPrice(star) * 1.2f));
        }
        return JsonSerializer.Serialize(new { ok = true, Buyables = pool, SellOffer = sell }, JsonOpts);
    }

    /// <summary>AI 선수 인수: 골드 지불 → 내 로스터로. 판 검투소는 신인으로 공석 승계.</summary>
    public string TransferBuyJson(string id)
    {
        if (SeasonActive) return Err("이적은 프리시즌에만");
        var g = _cast.FirstOrDefault(x => x.Id == id && !x.IsPlayer);
        if (g == null) return Err("매물이 아니다");
        if (_cast.Count(x => x.IsPlayer) >= RosterCap) return Err($"로스터 가득참 (상한 {RosterCap})");
        int price = (int)TransferPrice(g);
        if (_gold < price) return Err($"잔고 부족 (이적료 {price})");
        _gold -= price;
        string oldLudus = g.LudusId; int oldDiv = g.Division;
        g.IsPlayer = true; g.LudusId = PlayerLudusId; g.TrainingPoints = 0;
        var rk = SpawnRookieCore(new SimRandom(_worldSeed ^ 0x7124_B0B0UL + (ulong)_rookieSeq * 3UL), oldLudus, oldDiv, 1);
        AddRivalRep(oldLudus, 8f);   // 이적료의 위신 — 판 쪽도 명성을 얻는다
        _story.Add((0, "transfer", $"🤝 이적 — {g.Name}, {LudusNameOf(oldLudus)}에서 우리 루두스로 (이적료 {price}) · 공석엔 신인 {rk.Name}"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>인수 제안 수락: 내 스타를 라이벌 루두스에 판다 — 골드는 크지만 전력을 잃는다.</summary>
    public string TransferSellJson(string id)
    {
        if (SeasonActive) return Err("이적은 프리시즌에만");
        var offerJson = TransfersJson();
        var doc = JsonDocument.Parse(offerJson).RootElement;
        if (!doc.TryGetProperty("SellOffer", out var so) || so.ValueKind == JsonValueKind.Null) return Err("유효한 제안이 없다");
        if (so.GetProperty("Id").GetString() != id) return Err("제안 대상이 아니다");
        var g = _cast.First(x => x.Id == id);
        int offer = so.GetProperty("Offer").GetInt32();
        string buyerName = so.GetProperty("Buyer").GetString()!;
        var buyer = ActiveRivalLudi.First(r => r.Name == buyerName);
        _gold += offer;
        g.IsPlayer = false; g.LudusId = buyer.Id; g.TrainingPoints = 0;
        _story.Add((0, "transfer", $"💰 이적 — {g.Name}, {buyerName}(으)로 (이적료 +{offer}). 잘 가라, 검투사여"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    // ── 황제의 특명(시즌 계약) — 개막 시 부여, 달성=영광·골드 / 실패=루두스 명성 하락 ──
    public sealed record EdictRec(string Type, string? TargetId, int N, string Desc);
    private EdictRec? _edict; private bool _edictDone;
    private const float EdictGlory = 8f, EdictGold = 80f, EdictFailRep = 15f;

    private void RollEdict()
    {
        _edict = null; _edictDone = false;
        if (_playerless || !_cast.Any(g => g.IsPlayer)) return;
        if (_seasonNo < 4) return;   // 신생 루두스(1~3시즌)엔 황제의 눈길이 닿지 않는다 — 실패 벌 면제(신생 보호)
        var rng = new SimRandom(SeasonSeed ^ 0xED1C_ED1CUL);
        if (!rng.Roll(0.75f)) return;   // 가끔은 조용한 시즌
        int pick = (int)(rng.NextUInt64() % 4UL);
        switch (pick)
        {
            case 0:
                _edict = new EdictRec("cup", null, 0, "챔피언십 컵을 우승하라"); break;
            case 1:
                var star = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault();
                if (star == null) return;
                _edict = new EdictRec("beat", star.Id, 0, $"{star.Name}을(를) 모래 위에 꿇려라"); break;
            case 2:
                _edict = new EdictRec("streak", null, 3, "3연승으로 군중을 열광시켜라"); break;
            default:
                _edict = new EdictRec("wins", null, 4, "이번 시즌 4승을 거둬라"); break;
        }
        _story.Add((0, "edict", $"📜 황제의 특명 — {_edict.Desc} (달성: ✨{EdictGlory:F0}·💰{EdictGold:F0} / 실패: 명성 −{EdictFailRep:F0})"));
    }

    /// <summary>경기 직후 특명 진행 체크(beat/streak/wins는 즉시 달성 가능).</summary>
    private void CheckEdict()
    {
        if (_edict == null || _edictDone) return;
        bool done = _edict.Type switch
        {
            "beat" => false,   // Play에서 승자 기준으로 별도 마킹
            "streak" => _cast.Any(g => g.IsPlayer && g.Streak >= _edict.N),
            "wins" => _cast.Where(g => g.IsPlayer).Sum(g => g.W) >= _edict.N,
            _ => false,
        };
        if (done) MarkEdictDone();
    }
    private void MarkEdictDone()
    {
        if (_edict == null || _edictDone) return;
        _edictDone = true;
        AddGlory(EdictGlory); _gold += EdictGold;
        _story.Add((0, "edict", $"📜 특명 달성! — {_edict.Desc} (✨+{EdictGlory:F0} 💰+{EdictGold:F0})"));
        // 황제의 총애: 특명을 거듭 완수하면 눈에 든다 — 단계 도달 시 1회성 하사품
        _favor++;
        (int Need, float Glory, string Title)[] tiers = { (3, 10f, "황제의 눈에 들다"), (6, 20f, "황제의 총신"), (10, 40f, "콜로세움의 총아") };
        for (int i = _favorLv; i < tiers.Length; i++)
            if (_favor >= tiers[i].Need)
            {
                _favorLv = i + 1; AddGlory(tiers[i].Glory);
                _story.Add((0, "favor", $"👑 {tiers[i].Title} — 총애 {_favor} (✨+{tiers[i].Glory:F0})"));
            }
    }
    private int _favor, _favorLv;   // 황제의 총애(특명 달성 누적)·도달한 단계
    private int _streetSeq;         // 거리 시비 시드 카운터(영속)

    /// <summary>친선/난투 등 리그 외 전투를 viewer.json으로 내보낸다(#2 — 실제 경기화면). 시드 결정론, 무기록.</summary>
    private MatchResult RunExhibition(Gladiator a, Gladiator b, ulong seed)
    {
        var (dA, dB) = BuildDefs(a, b, "normal");
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(dA, dB, seed, events, frames);
        ViewerExport.WriteDoc(dA, dB, seed, res, frames, events, "viewer.json",
            EndowOf(a.Id, dA), EndowOf(b.Id, dB));
        return res;
    }

    /// <summary>거리 시비(#14/#2): 내 선수가 지목한 라이벌에게 싸움 — 실제 경기 시뮬(길거리 배경), 감정·관계 악화·인기·부상.</summary>
    public string StreetFightJson(string fighterId, string targetId = "")
    {
        if (_playerless) return Err("CLI 모드");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        if (g.InjuryMatches > 0) return Err("부상 중 — 거리 싸움은 무리다");
        if (g.Fatigue >= 85) return Err("너무 지쳤다 — 휴식이 먼저");
        var rng = new SimRandom(_worldSeed ^ 0x5417_B4A1UL + (ulong)(_streetSeq++) * 29UL);
        var target = _cast.FirstOrDefault(x => x.Id == targetId && !x.IsPlayer)
                     ?? _cast.Where(x => !x.IsPlayer).OrderBy(_ => rng.NextUInt64()).FirstOrDefault();
        if (target == null) return Err("시비 걸 상대가 없다");
        var res = RunExhibition(g, target, rng.NextUInt64());   // 실제 난투 시뮬 → viewer.json(길거리)
        bool win = res.Winner == 0;
        // 감정 유발 + 관계 악화(그 상대 한정) — 다음 경기에서 발화
        target.PendingEmotions.Add(EmotionTable.Grudge);
        _ledger.Get(target.Id, g.Id).Affinity = Math.Clamp(_ledger.Get(target.Id, g.Id).Affinity - 20f, -100f, 100f);
        _ledger.Get(g.Id, target.Id).Affinity = Math.Clamp(_ledger.Get(g.Id, target.Id).Affinity - 8f, -100f, 100f);
        g.Fatigue = Math.Min(100, g.Fatigue + 5);
        string note;
        if (win)
        {
            g.Popularity += 12f;
            if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated);
            note = $"🍺 {g.Name}이(가) {target.Name}을(를) 길거리에서 눕혔다 — 인기 +12 · {target.Name}이(가) 이를 갈다";
        }
        else
        {
            g.Popularity += 4f;
            if (res.StatsA.MinHpPct <= 0.20f && rng.Roll(0.40f)) { g.InjuryMatches = Math.Max(g.InjuryMatches, 1); note = $"🍺 {g.Name}, {target.Name}과의 난투에서 밀렸다 — 부상(1경기) · 인기 +4"; }
            else note = $"🍺 {g.Name} vs {target.Name} 난투 — {(res.Winner < 0 ? "팽팽했다" : "졌다")} · 인기 +4";
        }
        _story.Add((0, "brawl", note + $" ({target.Name} 다음 경기 '원한')"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return JsonSerializer.Serialize(new { ok = true, note, target = target.Name, won = win, venue = "street", a = g.Name, b = target.Name }, JsonOpts);
    }

    /// <summary>친선 스파링(#2 실제 경기화면): 같은 부 AI와 연습 경기(투기장 배경) — 무기록·부상 없음, 성장 소량 + 가벼운 피로.</summary>
    public string SparringJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);   // #3 시즌 중에도 가능
        if (g == null) return Err("내 선수 아님");
        if (g.InjuryMatches > 0) return Err("부상 중 — 스파링은 무리다");
        if (g.Fatigue >= 80) return Err("피로가 너무 쌓였다 — 휴식이 먼저");
        var rng = new SimRandom(_worldSeed ^ 0x5B42_00AAUL + (ulong)_sparCount++ * 7UL);
        var peers = _cast.Where(x => !x.IsPlayer && x.Division == g.Division).ToList();
        if (peers.Count == 0) peers = _cast.Where(x => !x.IsPlayer).ToList();
        if (peers.Count == 0) return Err("상대가 없다");
        var opp = peers[(int)(rng.NextUInt64() % (ulong)peers.Count)];
        var res = RunExhibition(g, opp, rng.NextUInt64());   // 실제 시뮬 → viewer.json(스파링 투기장)
        string? grow = Grow(g, rng);
        g.Fatigue = Math.Min(100, g.Fatigue + 3);
        string wName = res.Winner == 0 ? g.Name : res.Winner == 1 ? opp.Name : "무승부";
        _story.Add((0, "sparring", $"🤺 스파링 — {g.Name} vs {opp.Name}: {wName} 우세" + (grow != null ? $" · {grow} +0.5" : "")));
        SaveWorld();
        return JsonSerializer.Serialize(new { ok = true, opp = opp.Name, winner = wName, grow, fatigue = g.Fatigue, venue = "spar", a = g.Name, b = opp.Name }, JsonOpts);
    }

    /// <summary>난투 리플레이 데이터 → melee.json (melee.html이 읽어 N명 렌더).</summary>
    private static void WriteMeleeJson(MeleeSim.MeleeResult res, List<MeleeSim.Frame> frames, List<MeleeSim.Unit> units, string venue)
    {
        var doc = new
        {
            Venue = venue,
            Units = units.Select(u => new { u.Name, u.Team, u.Weapon }).ToArray(),
            Frames = frames.Select(f => new { t = MathF.Round(f.T * 100) / 100, u = f.Units.Select(x => new {
                x = MathF.Round(x.X * 100) / 100, y = MathF.Round(x.Y * 100) / 100,
                h = MathF.Round(x.HpPct * 100) / 100, sp = MathF.Round(x.StamPct * 100) / 100,
                s = x.State, f = x.Facing, hv = x.Heavy, bl = x.Bleed, ht = x.Hit, d = x.Dead }).ToArray() }).ToArray(),
            Result = new { res.WinningTeam, res.Reason, Duration = MathF.Round(res.DurationSec * 100) / 100 },
        };
        File.WriteAllText("melee.json", JsonSerializer.Serialize(doc, JsonOpts));
    }

    /// <summary>거리 시비 타겟 후보(라이벌 검투사 목록).</summary>
    public string StreetTargetsJson()
    {
        var list = _cast.Where(x => !x.IsPlayer)
            .OrderByDescending(x => x.Fame)
            .Select(x => new { id = x.Id, name = x.Name, weapon = x.WeaponId.Replace("WPN_", ""),
                personality = x.PersonalityId.Replace("PER_", ""), fame = MathF.Round(x.Fame),
                ludus = LudusNameOf(x.LudusId) }).ToList();
        return JsonSerializer.Serialize(new { ok = true, targets = list }, JsonOpts);
    }

    /// <summary>패싸움(#14 다대다): 내 선수 + 아군 vs 라이벌 무리 — **MeleeSim**(매트릭스 분리 난투 엔진)으로
    /// N명 동시 난전을 실제 시뮬 → melee.json(뷰어 재생). 관계(원수)는 이 난투 한정 ATK 소폭 상승으로 개입.</summary>
    public string GangBrawlJson(string fighterId)
    {
        if (_playerless) return Err("CLI 모드");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        if (g.InjuryMatches > 0) return Err("부상 중 — 패싸움은 무리다");
        if (g.Fatigue >= 80) return Err("너무 지쳤다 — 휴식이 먼저");
        var rng = new SimRandom(_worldSeed ^ 0x6A46_B0A5UL + (ulong)(_streetSeq++) * 37UL);

        // 우리 편: g + 건강한 내 선수(최대 2) → 부족하면 g의 친구 라이벌 1
        var myside = new List<Gladiator> { g };
        myside.AddRange(_cast.Where(x => x.IsPlayer && x.Id != g.Id && x.InjuryMatches == 0).Take(2));
        if (myside.Count == 1)
        {
            var friend = _cast.FirstOrDefault(x => !x.IsPlayer && _ledger.Get(x.Id, g.Id).Classify(x.PersonalityId) == RelationType.Friend);
            if (friend != null) myside.Add(friend);
        }
        // 상대 무리: 우리 편 수만큼(±1) 라이벌 — g에게 적대적인 상대 우선
        int foeCount = Math.Clamp(myside.Count + (rng.Roll(0.5f) ? 1 : 0), 2, 3);
        var mineIds = myside.Select(x => x.Id).ToHashSet();
        var foes = _cast.Where(x => !x.IsPlayer && !mineIds.Contains(x.Id))
            .OrderByDescending(x => { var rt = _ledger.Get(x.Id, g.Id).Classify(x.PersonalityId);
                return rt is RelationType.Nemesis or RelationType.Fear or RelationType.Envy ? 1 : 0; })
            .ThenBy(_ => rng.NextUInt64()).Take(foeCount).ToList();
        if (foes.Count < 2) return Err("패싸움 상대가 부족하다");

        var notes = new List<string> { $"🥊 패싸움 — {string.Join("·", myside.Select(m => m.Name))} vs {string.Join("·", foes.Select(f => f.Name))}" };
        string venue = rng.Roll(0.5f) ? "street" : "bar";

        // 로스터(def, team) 구성. 관계 개입: g의 원수인 상대는 이 난투 한정 ATK +15%(melee 전용 def, 영속 X)
        var roster = new List<(FighterDef Def, int Team)>();
        foreach (var m in myside) roster.Add((ToDef(m, null, 0f), 0));
        foreach (var f in foes)
        {
            var d = ToDef(f, null, 0f);
            if (_ledger.Get(f.Id, g.Id).Classify(f.PersonalityId) == RelationType.Nemesis)
            { d = d with { Stats = d.Stats with { Atk = d.Stats.Atk * 1.15f } }; notes.Add($"↳ 원수 {f.Name}, {g.Name}에게 이를 갈며 더 거세게 친다"); }
            roster.Add((d, 1));
        }

        var (mres, frames, umeta) = new MeleeSim().Run(roster, rng.NextUInt64());
        WriteMeleeJson(mres, frames, umeta, venue);
        bool won = mres.WinningTeam == 0;

        // 집계: 참여자 피로, 관계 악화·상대 원한, 승패별 인기·부상(난투 결과 MinHpPct 반영)
        foreach (var m in myside.Where(x => x.IsPlayer)) m.Fatigue = Math.Min(100, m.Fatigue + 8);
        foreach (var f in foes) { f.PendingEmotions.Add(EmotionTable.Grudge);
            _ledger.Get(f.Id, g.Id).Affinity = Math.Clamp(_ledger.Get(f.Id, g.Id).Affinity - 12f, -100f, 100f); }
        if (won)
        {
            foreach (var m in myside.Where(x => x.IsPlayer)) { m.Popularity += 15f; if (SeasonActive) m.PendingEmotions.Add(EmotionTable.Motivated); }
            notes.Add("🏆 완승! 뒷골목을 평정했다 — 인기 대폭 상승");
        }
        else
        {
            g.Popularity += 4f;
            // 크게 얻어맞은 내 선수(HP 20%↓ 생존/전멸)는 부상 위험
            foreach (var m in myside.Where(x => x.IsPlayer))
            {
                var oc = mres.Outcomes.FirstOrDefault(o => o.Name == m.Name);
                if (oc != null && (!oc.Survived || oc.MinHpPct <= 0.20f) && rng.Roll(0.40f))
                { m.InjuryMatches = Math.Max(m.InjuryMatches, 1); notes.Add($"💢 {m.Name} 다구리에 당했다 — 부상(1경기)"); }
            }
            notes.Add("💢 수적 난전에 밀렸다 — 굴욕");
        }
        _story.Add((0, "brawl", notes[0] + $" → {(won ? "완승" : "패배")}"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return JsonSerializer.Serialize(new { ok = true, notes, venue, won, myWins = won ? 1 : 0, melee = true }, JsonOpts);
    }

    /// <summary>은퇴(세대·혈통): 프리시즌에 내 선수를 명예롭게 보낸다 → 명예의 전당(★).
    /// 세 진로(교관·스승·스카우터)는 각각 자격 기준을 넘어야 하며, 미달 시 단순 은퇴(명전 등록 없음).</summary>
    public string RetireJson(string fighterId, string path = "")
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);   // #3 시즌 중에도 가능
        if (g == null) return Err("내 선수 아님");
        // 자격 검증(진로 지정 시)
        if (path == "instructor" && g.Fame < InstructorFameMin) return Err($"교관 자격 미달 — 명성 {InstructorFameMin:F0}+ 필요 (현재 {g.Fame:F0})");
        if (path == "master" && g.Fame < MasterFameMin) return Err($"스승 자격 미달 — 명성 {MasterFameMin:F0}+ 필요 (현재 {g.Fame:F0})");
        if (path == "scout" && g.CKoW < ScoutKoMin && g.Fame < ScoutFameMin) return Err($"스카우터 자격 미달 — 통산 KO {ScoutKoMin}+ 또는 명성 {ScoutFameMin:F0}+ 필요");

        PurgeRemainingMatches(g.Id);
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        bool hall = path is "instructor" or "master" or "scout";
        if (hall) Unlock("kingmaker");   // 명장의 산실 — 진로 은퇴자 배출
        if (hall) _hall.Add(new HallRec(g.Name, g.WeaponId.Replace("WPN_", ""), MathF.Round(g.Fame),
            $"{g.CW}-{g.CL}-{g.CD}", g.Age, Math.Max(1, _seasonsPlayed), true));

        switch (path)
        {
            case "instructor":
            {
                // 교관: 생전 최고 스탯 축의 상한을 내 루두스 전체에 +보너스(누적). 스탯이 높을수록 큰 유산.
                int axis = StrongestAxis(g.Stats);
                float bonus = 6f + (AxisValue(g.Stats, axis) - 80f) * 0.12f;   // 80 기준 초과분 가산
                bonus = Math.Clamp(bonus, 4f, 16f);
                _axisCapBonus[axis] += bonus;
                _story.Add((0, "retire", $"🎓 {g.Name} 은퇴 → 교관 — {AxisName(axis)} 상한 +{bonus:F0} (루두스 전체·누적)"));
                break;
            }
            case "master":
                // 스승: 특성·전술을 한 선수에게 1회 전수(추가). bestow로 소비.
                _masterName = g.Name;
                _masterTrait = g.TraitIds.Length > 0 ? g.TraitIds[0] : null;
                _masterTactic = g.TacticPool.Length > 0 ? g.TacticPool[^1] : null;
                _mentorName = g.Name;   // 영입 유산도 겸함(기존 스승 효과 유지)
                _story.Add((0, "retire", $"📜 {g.Name} 은퇴 → 스승 — 특성·전술을 물려줄 준비 (한 선수에게 1회 전수)"));
                break;
            case "scout":
                _scoutLevel++;
                _story.Add((0, "retire", $"🔭 {g.Name} 은퇴 → 스카우터 (Lv{_scoutLevel}) — 영입 안목 향상·후보 정보 공개"));
                break;
            default:
                _story.Add((0, "retire", $"👋 {g.Name} 조용히 검을 내려놓다 (자격 미달 — 명예의 전당 미등재)"));
                break;
        }
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>스승의 유산 전수(1회 소비): 한 선수에게 특성·전술 추가(교체 아님).</summary>
    public string BestowJson(string fighterId)
    {
        if (_masterTrait == null && _masterTactic == null) return Err("전수할 스승의 유산이 없다");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 선수 아님");
        var added = new List<string>();
        if (_masterTrait != null && !g.TraitIds.Contains(_masterTrait))
        { g.TraitIds = g.TraitIds.Append(_masterTrait).ToArray(); added.Add("특성 " + TraitTable.Get(_masterTrait).Name); }
        if (_masterTactic != null && !g.TacticPool.Contains(_masterTactic))
        { g.TacticPool = g.TacticPool.Append(_masterTactic).ToArray(); added.Add("전술 " + _masterTactic.Replace("TAC_","")); }
        _story.Add((0, "master", $"📜 스승 {_masterName}의 유산 — {g.Name}에게 {(added.Count > 0 ? string.Join("·", added) : "이미 보유")} 전수"));
        _masterTrait = null; _masterTactic = null;   // 소비
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    private const float MentorFameMin = 60f, InstructorFameMin = 40f, MasterFameMin = 60f, ScoutFameMin = 30f;
    private const int ScoutKoMin = 12;
    private string? _mentorName;   // 루두스의 스승(은퇴 전설) — 영입 유산
    private string? _masterName, _masterTrait, _masterTactic;   // 스승 전수 대기(소비성)
    private int _scoutLevel;                                     // 스카우터 누적 레벨
    private readonly float[] _axisCapBonus = new float[6];       // 교관 상한 보너스(축별 누적)
    private static int StrongestAxis(FighterStats s)
    {
        float[] v = { s.Atk, s.Def, s.HpMax / 10f, s.Spd, s.Aspd, s.Rct };
        int best = 0; for (int i = 1; i < 6; i++) if (v[i] > v[best]) best = i; return best;
    }
    private static float AxisValue(FighterStats s, int a) => a switch { 0 => s.Atk, 1 => s.Def, 2 => s.HpMax / 10f, 3 => s.Spd, 4 => s.Aspd, _ => s.Rct };
    private static string AxisName(int a) => a switch { 0 => "공격", 1 => "방어", 2 => "체력", 3 => "이동", 4 => "공속", _ => "반응" };

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

        // 경기 전 승률 스냅샷(잭팟 연출용 배당) — 상태 조회로 캐시돼 있으면 재사용, 내 경기는 필요 시 산출(15판)
        float? preProbA = _oddsCursor == _cursor ? _oddsProbA
            : (ById(_schedule[_cursor].A).IsPlayer || ById(_schedule[_cursor].B).IsPlayer) ? CursorProbA() : null;

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
            if (cupW.IsPlayer) { _gold += CupWinPrize; AddRep(RepCupTitle); AddGlory(GloryCup); Unlock("first_cup");
                                 if (_edict is { Type: "cup" }) MarkEdictDone(); }
            else AddRivalRep(cupW.LudusId, RepCupTitle);
        }
        else if (s.Kind == "cup_sf" && res.Winner >= 0)   // 4강 진출 상금(내 선수)
        {
            var w = res.Winner == 0 ? A : B;
            if (w.IsPlayer) _gold += CupSemiPrize;
        }
        else if (s.Kind == "gauntlet" && res.Winner >= 0)   // 🏟 초청전: 승당 하사, 전승 시 대관
        {
            var w = res.Winner == 0 ? A : B;
            if (w.IsPlayer)
            {
                _gauntletWins++; _gold += 100f; AddGlory(5f);
                _story.Add((s.Round, "gauntlet", $"🏟 초청전 {_gauntletWins}승 — {w.Name} (💰+100 ✨+5)"));
                if (_gauntletWins >= 3)
                { AddGlory(15f); _story.Add((s.Round, "gauntlet", $"👑 초청전 전승! {w.Name}, 황제 앞에서 대관하다 (✨+15)")); }
            }
        }

        // 베팅 정산: 이 경기에 걸었으면 승패 판정 (결과 카드 연계 노트 포함)
        bool betWon = false; string? betNote = null;
        if (_betCursor == _cursor - 1)
        {
            _betCursor = -1;
            string on = _betSide == 0 ? A.Name : B.Name;
            bool won = res.Winner == _betSide;
            float payout = won ? MathF.Round(_betAmount * _betOdds) : 0f;   // 마진은 배당에 내장
            if (won)
            {
                _gold += payout; _seasonBetNet += payout;
                if (++_betHits >= 10) Unlock("gambler");   // 행운의 도박사
                _story.Add((s.Round, "bet", $"🎲 적중! {on} 승 — 배당금 +{payout:F0}"));
            }
            else _story.Add((s.Round, "bet", $"🎲 빗나감 — {_betAmount:F0} 데나리우스가 모래에 묻혔다"));
            betWon = won;
            betNote = won ? $"🎲 적중! {on}에 건 {_betAmount:F0} → 배당금 +{payout:F0} (×{_betOdds:F2})"
                          : $"🎲 빗나감 — {on}에 건 {_betAmount:F0} 데나리우스가 모래에 묻혔다";
            _betLog.Add(new BetLogRec(_seasonNo, on, _betAmount, _betOdds, won, payout));
            while (_betLog.Count > 60) _betLog.RemoveAt(0);
        }

        EnsureSchedule();   // 다음 페이즈 편성(예: 4강 후 결승) — 종료 판정 전에
        bool last = _cursor >= _schedule.Count && _cupStage == 3;
        if (!last) MaybeSpawnEvent(A.IsPlayer ? A : B.IsPlayer ? B : null);   // 내 경기 후 서사 이벤트(2b)
        if (last) FinalizeSeason();
        else SaveWorld();
        if (_interactive) WriteSeasonJson();

        // 승자의 경기 전 배당(잭팟 연출) — 승률 스냅샷이 있을 때만(0 = 산출 불가 → 연출 생략)
        float winnerOdds = preProbA.HasValue && res.Winner >= 0
            ? MathF.Round(BetOdds(res.Winner == 0 ? preProbA.Value : 1f - preProbA.Value) * 100f) / 100f : 0f;
        return new MatchSummary(_seasonNo, s.Round, s.IsEvent, A.Name, B.Name,
            res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason, last, newSeason,
            A.IsPlayer || B.IsPlayer, income, incomeNote, mine,
            _lastFates.Count > 0 ? _lastFates.ToList() : null,
            _lastHype, _lastInjuries.Count > 0 ? _lastInjuries.ToList() : null,
            _lastUpset, winnerOdds, betWon, betNote);
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
            if (top.Count < 4)                                // 1부가 작으면(초기·소규모 리그) 종합 상위로 보충
                top = top.Concat(Standings(2).Take(4 - top.Count)).ToList();
            if (top.Count < 4) { _cupStage = 3; return; }   // 그래도 부족 → 컵 생략
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

        // 컵 종료 → 🏟 황제의 초청전(건틀릿): 총애 6+ 루두스의 간판이 리그 최강 3인과 연전 (총애 트랙의 정점)
        if (_cupStage == 3 && _gauntletStage == 0)
        {
            _gauntletStage = 1;
            if (_favor >= 6 && !_playerless && _cast.Any(g => g.IsPlayer))
            {
                var champ = _cast.Where(g => g.IsPlayer).OrderByDescending(g => g.Fame).First();
                var rivals = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame).Take(3).ToList();
                if (rivals.Count == 3)
                {
                    _gauntletWins = 0;
                    foreach (var r in rivals)
                        _schedule.Add(new SchedRec(_rounds + 4, champ.Id, r.Id, true, 0f, "gauntlet"));
                    _story.Add((_rounds + 4, "gauntlet", $"🏟 황제의 초청전 — 총애받는 {champ.Name}, 최강 3인({string.Join("·", rivals.Select(x => x.Name))})과 연전!"));
                }
            }
        }
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
        public required string InitialTactic;   // 개막 전술 — 같은 전술 재선택 판정용
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

        _live = new LiveMatch { MyId = mine.Id, MyPool = mine.TacticPool, InitialTactic = mine.TacticId, Switches = new() };
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
        string full = tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId;
        if (!_live.MyPool.Contains(full)) return Err("전술풀에 없는 전술");
        // 그 시각에 이미 적용 중인 전술을 다시 고르면 = 변화 없음 → 기회 미차감(#12)
        string activeNow = _live.Switches.Where(x => x.Time <= time).OrderBy(x => x.Time).LastOrDefault()?.TacticId ?? _live.InitialTactic;
        if (full == activeNow)
            return JsonSerializer.Serialize(new { ok = true, remaining = 2 - _live.Switches.Count, nochange = true }, JsonOpts);
        if (_live.Switches.Count >= 2) return Err("전술 변경은 경기당 2회");
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

    /// <summary>명경기 재관전: 보관함의 스냅샷+시드로 결정론 재시뮬(시즌 무관 영속).</summary>
    public string WatchGreatJson(int idx)
    {
        if (idx < 0 || idx >= _greatest.Count) return Err("명경기 없음");
        var e = _greatest[idx].Entry;
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(e.DefA, e.DefB, e.Seed, events, frames);
        ViewerExport.WriteDoc(e.DefA, e.DefB, e.Seed, res, frames, events, "viewer.json",
            EndowOf(e.AId, e.DefA), EndowOf(e.BId, e.DefB));
        return JsonSerializer.Serialize(new { ok = true, a = e.AName, b = e.BName, round = e.Round, isEvent = e.IsEvent }, JsonOpts);
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

    /// <summary>관전 아카이브 목록(#1): 이번 시즌 + 지난 시즌들 전 경기 — 시즌·라운드 메타(리플레이는 별도).</summary>
    public string ArchiveListJson()
    {
        var rows = new List<ArchDoc>();
        // 지난 시즌들(아카이브) — 오래된 → 최신
        foreach (var a in _archive)
            rows.Add(new ArchDoc(-1 - _archive.IndexOf(a), a.Season, a.Entry.Round, a.Entry.IsEvent,
                a.Entry.AName, a.Entry.BName, a.Entry.Winner, a.Entry.Reason, a.Entry.IsPlayerMatch));
        // 이번 시즌(현행 로그) — Idx 그대로(양수) → WatchJson 재사용
        int cur = Math.Max(1, _seasonNo);
        foreach (var e in _matchLog)
            rows.Add(new ArchDoc(e.Idx, cur, e.Round, e.IsEvent, e.AName, e.BName, e.Winner, e.Reason, e.IsPlayerMatch));
        return JsonSerializer.Serialize(new { ok = true, matches = rows, currentSeason = cur }, JsonOpts);
    }

    /// <summary>아카이브 경기 재관전: idx>=0 = 이번 시즌(WatchJson), idx&lt;0 = 지난 시즌 아카이브(−1−pos).</summary>
    public string WatchArchiveJson(int idx)
    {
        if (idx >= 0) return WatchJson(idx);
        int pos = -1 - idx;
        if (pos < 0 || pos >= _archive.Count) return Err("아카이브 경기 없음");
        var e = _archive[pos].Entry;
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
        _lastInjuries.Clear();
        _lastHype = MathF.Round((A.Popularity + B.Popularity) * (exec ? 2f : isEvent ? 1.5f : 1f) + (A.Fame + B.Fame) * 0.1f);   // 경기 관심도(#5)
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
                if (exec) Unlock("executioner");                            // 처형전 승리
                if (other.Fame >= self.Fame * 2f && other.Fame >= 30f) Unlock("giant_killer");  // 거인 사냥꾼
                if (self.CW + 1 >= 50) Unlock("veteran");                   // 통산 50승(이번 승 포함)
            }
            if (self.Fame >= 100f) Unlock("legend");
            income += own;
            if (self == A) { incA = own; noteA = string.Join(" · ", notes); } else { incB = own; noteB = string.Join(" · ", notes); }
        }
        _gold += income;
        if (_gold >= 2000f) Unlock("tycoon");   // 대부호
        incomeNote = string.Join(" · ", new[] { noteA, noteB }.Where(n => n.Length > 0));

        // 순위/커리어 + 관계 + 감정 (경기 인덱스 파생 스트림 = 미드시즌 재개 결정론)
        Record(A, B, res, standing: !isEvent);
        // 황제의 특명 진행: 지목 상대 격파(beat)는 여기서, 연승/N승은 CheckEdict에서
        if (_edict is { Type: "beat" } && !_edictDone && win != null && win.IsPlayer && lose?.Id == _edict.TargetId)
            MarkEdictDone();
        CheckEdict();
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

        // 명경기 판정: 드라마 스코어 — 대역전·이변·복수·KO·처형전·넉다운 (아래 보관 판정 전에 이번 경기 것으로 갱신)
        _lastUpset = upset;
        _lastDrama = (comeback ? 3f : 0f) + (upset ? 2f : 0f) + (revenge ? 2f : 0f) + (ko ? 1f : 0f)
                   + (exec ? 2f : 0f) + (win != null ? winStats.Knockdowns * 0.5f : 0f);

        // 경기 로그 (스냅샷+시드 = 재관전) + 내 선수 변경사항(결과 화면)
        string winner = res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name);
        var entry = new LogEntry(_matchIdx, round, isEvent, A.Id, B.Id, A.Name, B.Name,
            winner, res.Reason, A.IsPlayer || B.IsPlayer, seed, defA, defB);
        _matchLog.Add(entry);
        // 명경기 보관함: 드라마 스코어 4+ → 시즌을 넘어 영속 보관(top 12, 스냅샷+시드 재관전)
        if (_lastDrama >= 4f)
        {
            _greatest.Add(new GreatRec(_seasonNo, _lastDrama, entry));
            if (_greatest.Count > 12) _greatest.Remove(_greatest.OrderBy(x => x.Drama).First());
            _story.Add((round, "greatest", $"🎞 명경기 — {A.Name} vs {B.Name} (드라마 {_lastDrama:F1}) 보관함 등재"));
        }
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
                _lastInjuries.Add($"{g.Name} ({dur}경기)");
                _story.Add((round, "injury", $"🩹 부상! {g.Name} — 향후 {dur}경기 실효 스탯 저하"));
            }
        }
    }

    /// <summary>경기 자동 성장 +0.5pt. 성장한 축 이름 반환(결과 화면 표시용), 상한 도달 시 null.</summary>
    private string? Grow(Gladiator g, SimRandom rng)
    {
        if (BudgetUsed(g.Stats) + 0.5f > g.PotentialBudget) return null;   // 상한 도달 — 더 안 큼
        int axis = (int)(rng.NextFloat01() * 6f);
        float amt = g.TraitIds.Contains(TraitTable.Genius) ? 0.8f : 0.5f;   // 천재(#16): 경기 성장 속도↑
        g.Stats = WithAxis(g.Stats, axis, amt);
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

    /// <summary>배당 캘리브레이션 프로브: 캐스트 전 페어를 N판씩 시뮬 → 예측식 후보별 MAE(예측-실승률) 리포트.</summary>
    public void OddsProbe(int simsPerPair)
    {
        var pairs = new List<(Gladiator A, Gladiator B, float actual)>();
        for (int i = 0; i < _cast.Count; i++)
            for (int j = i + 1; j < _cast.Count; j++)
            {
                var (dA, dB) = BuildDefs(_cast[i], _cast[j], "normal");
                int wins = 0, decided = 0;
                for (ulong s = 1; s <= (ulong)simsPerPair; s++)
                {
                    var r = new MatchSim().Run(dA, dB, s * 7919UL);
                    if (r.Winner >= 0) { decided++; if (r.Winner == 0) wins++; }
                }
                if (decided > 10) pairs.Add((_cast[i], _cast[j], (float)wins / decided));
            }
        Console.WriteLine($"  페어 {pairs.Count}개 × {simsPerPair}판 — 예측식 후보별 MAE(%p):");
        float MaeRatio() => pairs.Average(p => MathF.Abs(Power(p.A) / (Power(p.A) + Power(p.B)) - p.actual)) * 100f;
        float MaeSig(float k) => pairs.Average(p => MathF.Abs(1f / (1f + MathF.Exp(-k * (Power(p.A) - Power(p.B)))) - p.actual)) * 100f;
        Console.WriteLine($"    현행 비율식: {MaeRatio():F1}");
        foreach (float k in new[] { 0.01f, 0.02f, 0.03f, 0.05f, 0.08f })
            Console.WriteLine($"    시그모이드 k={k}: {MaeSig(k):F1}");
        // 시뮬 기반(다른 시드 스트림 K판 추정) — 상성 매트릭스를 그대로 반영
        foreach (int k in new[] { 9, 15, 25 })
        {
            float mae = pairs.Average(p =>
            {
                var (dA, dB) = BuildDefs(p.A, p.B, "normal");
                return MathF.Abs(SimProb(dA, dB, 0xBE77_0000UL, k) - p.actual);
            }) * 100f;
            Console.WriteLine($"    시뮬 {k}판 추정: {mae:F1}");
        }
    }

    /// <summary>시뮬 기반 승률 추정(A 기준) — 본경기와 다른 시드 스트림 K판, 라플라스 스무딩.</summary>
    private static float SimProb(FighterDef dA, FighterDef dB, ulong seedBase, int k)
    {
        int wins = 0, decided = 0;
        for (int t = 1; t <= k; t++)
        {
            var r = new MatchSim().Run(dA, dB, seedBase + (ulong)t * 104729UL);
            if (r.Winner >= 0) { decided++; if (r.Winner == 0) wins++; }
        }
        return Math.Clamp((wins + 1f) / (decided + 2f), 0.05f, 0.95f);
    }

    /// <summary>배당용 전력 근사 — 진단(oddsprobe) 전용. 실배당은 시뮬 기반(CursorProbA) — 상성 매트릭스 지배라 스탯합은 예측력이 없다(MAE 35%p).</summary>
    private static float Power(Gladiator g)
    {
        float s = g.Stats.Atk + g.Stats.Def + g.Stats.HpMax / 10f + g.Stats.Spd + g.Stats.Aspd + g.Stats.Rct;
        return s + g.Fame * 0.15f + g.Streak * 2f - (g.InjuryMatches > 0 ? 15f : 0f);
    }

    private int _oddsCursor = -1; private float _oddsProbA;   // 커서별 배당 캐시(시뮬 15판 — MAE ~10%p)
    private float _seasonBetNet;                              // 시즌 베팅 수지(결산 표시)
    private int _gauntletStage, _gauntletWins;                // 황제의 초청전: 0=미편성 1=편성됨 · 승수

    /// <summary>커서 경기의 A 승률(시뮬 15판, 캐시) — 본경기와 다른 시드 스트림이라 결과 유출 없음. 전술은 경기와 동일 로직으로 예측.</summary>
    private float CursorProbA()
    {
        if (_oddsCursor == _cursor) return _oddsProbA;
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);   // PlayNext와 동일 소비 순서
        string tA = A.IsPlayer ? A.TacticId : SelectTacticAi(A, B, tacRng);
        string tB = B.IsPlayer ? B.TacticId : SelectTacticAi(B, A, tacRng);
        var (dA, dB) = BuildDefs(A, B, s.Format);
        dA = dA with { TacticsId = tA }; dB = dB with { TacticsId = tB };
        _oddsProbA = SimProb(dA, dB, SeasonSeed ^ 0xBE77_0DD5UL + (ulong)_matchIdx * 977UL, 15);
        _oddsCursor = _cursor;
        return _oddsProbA;
    }

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

        _candidates.Clear(); _lastReveal.Clear();
        var rng = new SimRandom(_worldSeed ^ 0x6ACA_6ACAUL + (ulong)(++_gachaCount) * 2654435761UL);
        var usedNames = _cast.Select(g => g.Name).Concat(_candidates.Select(c => c.Name)).ToHashSet();
        var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
        var pers = PersonalityTable.All.Select(p => p.Id).ToArray();
        int scouting = 1 + LudusTier() + (_mentorName != null ? 1 : 0) + _scoutLevel;   // 등급 + 스승 안목 + 스카우터 유산 = 원석 품질
        for (int i = 0; i < 3; i++)
        {
            string name = PickName(rng, usedNames); usedNames.Add(name);
            var g = RollGladiator(rng,
                id: $"GLA_R{_gachaCount}_{i}", name,
                wpn: wpns[(int)(rng.NextFloat01() * wpns.Length)],
                per: pers[(int)(rng.NextFloat01() * pers.Length)],
                sigTactic: null, isPlayer: true, ageMin: 15, ageMax: 30, talentRolls: scouting);
            _candidates.Add(g);
        }
        // 스카우터 유산: 레벨만큼 후보 정보를 미리 엿본다(천부/잠재/특성/전술 중 랜덤 하나)
        _candHints.Clear();
        for (int k = 0; k < _scoutLevel && k < 6; k++)
        {
            int ci = (int)(rng.NextFloat01() * _candidates.Count);
            var c = _candidates[ci];
            string hint = (rng.NextUInt64() % 4) switch
            {
                0 => "천부 " + ViewerExport.TalentName(c.Talent),
                1 => "잠재 " + ViewerExport.PotentialName(c.Potential),
                2 => c.TraitIds.Length > 0 ? "특성 " + TraitTable.Get(c.TraitIds[0]).Name : "특성 없음",
                _ => "전술 " + c.TacticPool[^1].Replace("TAC_",""),
            };
            if (!_candHints.TryGetValue(ci, out var l)) _candHints[ci] = l = new();
            if (!l.Contains(hint)) l.Add(hint);
        }
        SaveWorld();
        return StateJson();
    }
    private readonly Dictionary<int, List<string>> _candHints = new();   // 스카우터 후보 힌트(메모리 전용)

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
        // 미선택 후보 공개 + 일부는 라이벌 루두스로 편입(#8) — 지나친 원석이 적이 되어 돌아온다
        _lastReveal.Clear();
        var others = _candidates.Where((_, i) => i != idx).ToList();
        var rRng = new SimRandom(_worldSeed ^ 0x0A11_5EED + (ulong)_gachaCount * 17UL);
        foreach (var o in others)
        {
            string? joinedRival = null;
            if (!_playerless && _cast.Count(x => !x.IsPlayer) < 9 && rRng.Roll(0.40f))
            {
                var rivals = ActiveRivalLudi.ToList();
                var rl = rivals.Count > 0 ? rivals[(int)(rRng.NextUInt64() % (ulong)rivals.Count)] : default;
                o.IsPlayer = false; o.LudusId = rl.Id ?? "RIV"; o.Division = 2;
                _cast.Add(o); joinedRival = rl.Name ?? "라이벌 검투소";
                _story.Add((0, "recruit", $"👤 놓친 원석 — {o.Name}이(가) {joinedRival}에 합류했다"));
            }
            _lastReveal.Add(new RevealDoc(o.Name, o.WeaponId.Replace("WPN_", ""), o.PersonalityId.Replace("PER_", ""),
                o.Age, ViewerExport.TalentName(o.Talent), ViewerExport.PotentialName(o.Potential),
                o.TraitIds.Select(t => TraitTable.Get(t).Name).ToArray(), joinedRival));
        }
        _candidates.Clear();          // 선택되지 않은 나머지는 후보 목록에서 제거(일부는 위에서 라이벌로 갔다)
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
        float axisCap = 150f + _axisCapBonus[a];   // 교관 유산: 해당 축 상한 상향(누적)
        if (AxisVal(g.Stats, a) >= axisCap) return Err($"축 상한({axisCap:F0})");
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
        WorldV2? w = TryRead(_worldPath);
        if (w == null && File.Exists(_worldPath + ".bak"))
        {
            w = TryRead(_worldPath + ".bak");
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
        _archive.Clear(); if (w.Archive != null) _archive.AddRange(w.Archive);
        _masterName = w.MasterName; _masterTrait = w.MasterTrait; _masterTactic = w.MasterTactic; _scoutLevel = w.ScoutLevel;
        if (w.AxisCapBonus != null) for (int i = 0; i < 6 && i < w.AxisCapBonus.Length; i++) _axisCapBonus[i] = w.AxisCapBonus[i];
        _betHits = w.BetHits; _patronage = w.Patronage;
        _betLog.Clear(); if (w.BetLog != null) _betLog.AddRange(w.BetLog);
        _streetSeq = w.StreetSeq;
        _lastSummary = w.LastSummary;
        _champions.Clear(); if (w.Champions != null) _champions.AddRange(w.Champions);
        _hall.Clear(); if (w.Hall != null) _hall.AddRange(w.Hall);
        _ludusRep = w.LudusRep; _glory = w.Glory; _pendingProposalOpp = w.PendingProposalOpp;
        _ludusName = string.IsNullOrWhiteSpace(w.LudusName) ? "내 루두스" : w.LudusName!;
        _mentorName = w.Mentor;
        _perks.Clear(); if (w.Perks != null) foreach (var p in w.Perks) _perks[p.Id] = (int)p.Rep;
        _rookieSeq = w.RookieSeq; _debt = w.Debt; _sparCount = w.SparCount;
        _edict = w.Edict; _edictDone = w.EdictDone;
        _greatest.Clear(); if (w.Greatest != null) _greatest.AddRange(w.Greatest);
        _betCursor = w.BetCursor; _betSide = w.BetSide; _betAmount = w.BetAmount; _betOdds = w.BetOdds;
        _favor = w.Favor; _favorLv = w.FavorLv; _proposalExec = w.ProposalExec;
        _seasonBetNet = w.SeasonBetNet; _gauntletStage = w.GauntletStage; _gauntletWins = w.GauntletWins;
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
        try { if (File.Exists(_worldPath)) File.Copy(_worldPath, _worldPath + ".bak", true); } catch { }   // 저장 전 스냅샷
        File.WriteAllText(_worldPath, JsonSerializer.Serialize(new WorldV2(
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
            _rookieSeq, _debt, _sparCount, _edict, _edictDone,
            _greatest.Count > 0 ? _greatest.ToList() : null,
            _betCursor, _betSide, _betAmount, _betOdds, _favor, _favorLv, _proposalExec,
            _seasonBetNet, _gauntletStage, _gauntletWins,
            _archive.Count > 0 ? _archive.ToList() : null,
            _masterName, _masterTrait, _masterTactic, _scoutLevel,
            _axisCapBonus.Any(x => x != 0f) ? _axisCapBonus.ToArray() : null, _betHits, _patronage,
            _betLog.Count > 0 ? _betLog.ToList() : null, _streetSeq), JsonOpts));
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
            g.Fatigue, g.InjuryMatches > 0, g.Division, g.CKoW)).ToList();
        var rels = _ledger.AllRelations(PersOf)
            .Select(x => new RelDoc(ById(x.Self).Name, ById(x.Opp).Name, RelationTable.Get(x.Type).Name,
                                    MathF.Round(x.State.Affinity), x.State.Wins, x.State.Losses)).ToList();
        int total = _schedule.Count;
        if (SeasonActive)
        {
            if (!_eventsAppended) total += Math.Max(2, _cast.Count / 2);   // 이벤트 미편성분
            if (_cupStage == 0 && _cast.Count >= 4) total += 3;           // 컵 미편성분(4강2+결승1)
        }
        // 달력: 전 일정(치른 경기=로그 이름/승자, 남은 경기=캐스트 이름) + 로마 날짜(스케줄 위치 비례)
        var cal = new List<CalDoc>();
        for (int i = 0; i < _schedule.Count; i++)
        {
            var s = _schedule[i];
            int day = (int)((float)i / Math.Max(1, _schedule.Count) * 239f);
            string month = RomanMonths[Math.Min(RomanMonths.Length - 1, day / 30)];
            bool played = i < _cursor && i < _matchLog.Count;
            string an, bn; string? winner = null; int idx = -1; bool mine;
            float hype = 0f;   // 예정 경기의 기대 흥행도(#5) — 치른 경기는 0
            if (played)
            {
                var e = _matchLog[i];
                an = e.AName; bn = e.BName; winner = e.Winner; idx = e.Idx; mine = e.IsPlayerMatch;
            }
            else
            {
                var ga = _cast.FirstOrDefault(g => g.Id == s.A); var gb = _cast.FirstOrDefault(g => g.Id == s.B);
                an = ga?.Name ?? s.A; bn = gb?.Name ?? s.B;
                mine = (ga?.IsPlayer ?? false) || (gb?.IsPlayer ?? false);
                float ev = s.Format == "execution" ? 2f : s.IsEvent ? 1.5f : 1f;
                hype = MathF.Round(((ga?.Popularity ?? 0) + (gb?.Popularity ?? 0)) * ev + ((ga?.Fame ?? 0) + (gb?.Fame ?? 0)) * 0.1f);
            }
            cal.Add(new CalDoc(idx, month, day % 30 + 1, an, bn, s.Kind, s.Format, winner, mine, SeasonActive && i == _cursor, hype));
        }
        return new SeasonDoc(SchemaVer, Math.Max(1, _seasonNo), _rounds, _matchIdx, total, !SeasonActive,
            next != null ? ById(next.A).Name : null, next != null ? ById(next.B).Name : null, next?.IsEvent ?? true,
            standings[0].Name, fighters, rels, _eventDocs.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList(),
            _matchLog.Select(e => new MatchLogDoc(e.Idx, e.Round, e.IsEvent, e.AName, e.BName, e.Winner, e.Reason, e.IsPlayerMatch)).ToList(),
            _champions.Count > 0 ? _champions.ToList() : null,
            _hall.Count > 0 ? _hall.OrderByDescending(h => h.Fame).ToList() : null,
            cal, 680 + Math.Max(1, _seasonNo));
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
            g.MGrit, g.MRecover, g.MShow, g.MPay, g.CKoW)).ToList();

        var cands = _candidates.Select((c, i) => new CandidateDoc(i, c.Name,
            c.WeaponId.Replace("WPN_", ""), c.PersonalityId.Replace("PER_", ""),
            c.TacticPool[0].Replace("TAC_", ""), c.Age,
            _candHints.TryGetValue(i, out var h) ? h.ToArray() : null)).ToList();   // ★ 마스킹 + 스카우터 힌트

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
            float probA = CursorProbA();   // 시뮬 15판(상성 반영) — VS 승률·배당 공용
            float myPraw = mine == null ? 0.5f : mine == A ? probA : 1f - probA;
            float myP = Math.Clamp(myPraw, 0.15f, 0.85f);   // VS 표시용은 극단 완화(멘탈 보호)
            int pctInt = (int)MathF.Round(myP * 100f);      // 배당은 이 정수 승률에서 파생 → 표시 정합
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
                MyWinPct: pctInt, MyOdds: MathF.Round(10000f / pctInt) / 100f, OppOdds: MathF.Round(10000f / (100 - pctInt)) / 100f,
                CrowdFavorsMe: mine != null && mine.Popularity >= opp.Popularity,
                Hype: MathF.Round((A.Popularity + B.Popularity) * (s.Format == "execution" ? 2f : s.IsEvent ? 1.5f : 1f) + (A.Fame + B.Fame) * 0.1f),
                OddsA: MathF.Round(BetOdds(probA) * 100f) / 100f, OddsB: MathF.Round(BetOdds(1f - probA) * 100f) / 100f,
                // 예상 수익(#15): Play의 출전료 공식과 동일 — 인기 hype·이벤트·처형전·협상 마스터리 반영
                FeeEstimate: mine == null ? 0f : MathF.Round(
                    (FeeBase + (mine.Popularity + opp.Popularity) * FeePopScale)
                    * (s.Format == "execution" ? 3f : s.IsEvent ? 2f : 1f) * IncomeMult * (1f + 0.08f * mine.MPay)),
                WinBonusEstimate: mine == null ? 0f : MathF.Round(WinBonus * IncomeMult));
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
            MathF.Round(_debt), RomanDate(),
            _edict != null ? new EdictDoc(_edict.Desc, _edictDone) : null,
            _greatest.Count > 0 ? _greatest.OrderByDescending(x => x.Drama)
                .Select((x, i) => new GreatDoc(_greatest.IndexOf(x), x.Season, x.Entry.AName, x.Entry.BName,
                    x.Entry.Winner, x.Entry.Reason, MathF.Round(x.Drama * 10) / 10)).ToList() : null,
            _betCursor == _cursor && SeasonActive && _cursor < _schedule.Count
                ? new BetDoc(ById(_betSide == 0 ? _schedule[_cursor].A : _schedule[_cursor].B).Name, _betAmount, _betOdds) : null,
            _favor,
            HasMyMatchAhead: SeasonActive && Enumerable.Range(_cursor, Math.Max(0, _schedule.Count - _cursor))
                .Any(i => ById(_schedule[i].A).IsPlayer || ById(_schedule[i].B).IsPlayer),
            RecruitReveal: _lastReveal.Count > 0 ? _lastReveal.ToList() : null,
            MasterPending: (_masterTrait != null || _masterTactic != null) ? _masterName : null,
            ScoutLevel: _scoutLevel,
            Legacy: BuildLegacyNote(),
            Patronage: MathF.Round(_patronage),
            Gamble: new GambleDoc(MathF.Round(_seasonBetNet), _betLog.Count(b => b.Won), _betLog.Count,
                _betLog.AsEnumerable().Reverse().Take(40).ToList())), JsonOpts);
    }
    private string? BuildLegacyNote()
    {
        var parts = new List<string>();
        for (int a = 0; a < 6; a++) if (_axisCapBonus[a] > 0f) parts.Add($"{AxisName(a)}상한 +{_axisCapBonus[a]:F0}");
        if (_scoutLevel > 0) parts.Add($"스카우터 Lv{_scoutLevel}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
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

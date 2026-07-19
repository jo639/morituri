using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 라니스타(루두스) 모드 게임 상태 기계 (배포[12] W2 — 매니지먼트).
/// 관전자 → 라니스타: 내 루두스 선수단(영입·전술 선택·성장·시설)과 AI 소속 6명이 한 리그에서 싸운다.
///  - 모든 선수는 고유 천부/잠재력(StatGen)·특성(TraitGen)·전술 3종 풀을 부여받는다. Sim 무변경(전부 기존 조립).
///  - 내 선수: 매 경기 전 전술 택1(라니스타 수싸움). AI: 상대 맞춤 휴리스틱 + 시드 노이즈로 자기 풀에서 선택.
///  - 경제(데나리우스): 경기별 출전료(양 선수 인기=hype)·승리/서사 보너스 / 뽑기·시설·시즌말 급여.
///  - 성장: 경기 자동 소량 + 3경기당 훈련 포인트(라니스타 분배). 상한 = 잠재력 버짓 — 노화(30+ 랜덤)로 상한 자체가 감소.
///  - 영속: world.json v2 — 매 변이 후 저장, 미드시즌 완전 재개(모든 난수는 저장된 카운터에서 파생 = 결정론).
/// </summary>
public sealed partial class Game
{
    private const int SchemaVer = 2;      // v1(관전 시즌) 파일은 비호환 → 새 세계
    private const int ConstantsVer = 1;
    private readonly string _worldPath = "world.json";   // 세이브 슬롯: 슬롯별 world{n}.json
    private bool _autosave = true;   // false면 SaveWorld() 자동 기록 생략 → 수동 저장(ManualSave) 시점만 디스크 반영

    // ── 경제 상수 (초안 — 튜닝 전제) ──
    private const float GachaCost = 100f, StartGold = 50f;
    private const int StartFreeGachas = 2;
    private const float FeeBase = 5f, FeePopScale = 0.05f, WinBonus = 10f, KoBonus = 3f, DramaBonus = 5f;
    private const float MainEventHype = 90f, SponsorPopReq = 50f, SponsorScale = 0.4f;   // 인기(#3) 페이오프: 메인이벤트 흥행 기준·스폰서 자격 인기·후원 배율
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
        public required string TacticId;                            // 현재 선택 (내 선수=라니스타, AI=경기마다 자동)
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
        public int BestStreak, Executions;                          // 모리튜리 기록(#2): 최다 연승·통산 처형 (은퇴 후에도 보존)
        public float TotalMatchTime, TotalDamage, TotalDamageTaken; // 모리튜리 기록(#2): 통산 경기시간·가한 피해·받은 피해
        public int TotalBlocks, TotalDodges;                        // 기록실: 통산 방어 성공·회피 성공
        public readonly List<string> PermInjuries = new();          // 영구 부상(#6): arm/ribs/eye/leg — 부위별 코어 스탯 영구 감소
        public int SeasonBrutals;                                   // 이번 시즌 격전(KO패·빈사) 횟수 — 극적 운명 게이트
        public int GrudgeCount;                                     // 통산 원한(굴욕적 KO패) 횟수 — 성격 드리프트 입력(감정 아닌 관계로 대체)
        public int MGrit, MRecover, MShow, MPay;                    // 마스터리(0~5) — 투혼·회복력·흥행·협상 (비스탯, 메타 전용)
        public readonly List<string> PendingEmotions = new();
        public readonly Dictionary<string, int> EmoHistory = new(); // 커리어 감정 이력(누적) — 성격 변화(Phase 4)의 입력
        public string[] SkillIds = Array.Empty<string>();           // T12 패시브 스킬(장착형 특성) — 슬롯: 챔피언+ 2, 그외 1
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
        int MGrit = 0, int MRecover = 0, int MShow = 0, int MPay = 0,
        Dictionary<string, int>? EmoHistory = null,   // 감정 이력(성격 드리프트 입력)
        string[]? Skills = null,                      // T12 패시브 스킬
        int GrudgeCount = 0,                          // 통산 원한 횟수(감정→관계 전환)
        int BestStreak = 0, int Executions = 0, float TotalMatchTime = 0f, float TotalDamage = 0f,  // 모리튜리 기록(#2)
        float TotalDamageTaken = 0f, int TotalBlocks = 0, int TotalDodges = 0,   // 기록실: 받은 피해·방어·회피

        string[]? PermInjuries = null);               // 영구 부상(#6)
    private sealed record SchedRec(int Round, string A, string B, bool IsEvent, float Score, string Kind = "regular",
        string Format = "normal");   // 특수 형식: execution(처형전)
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
        int BetStreak = 0,   // 연속 적중(스트릭 보너스)
        bool Redemption = false, int MyCupTitles = 0,   // 재기의 서약(강등 아크)·내 컵 우승 횟수(엔드게임)
        string? FixFighterId = null, float FixReward = 0f,   // 승부조작 가담 예약(영속)
        List<BetLogRec>? BetLog = null, int StreetSeq = 0,   // 베팅 이력·거리 시비 카운터
        int SurgerySeq = 0,   // 의무실 수술 카운터(시드 결정론)
        string? StoryStage = null, List<string>? StoryBeats = null, string? StoryCtx = null,   // [13] 캠페인 (null=구세이브=chronicle)
        string? FixChoice = null, List<string>? GhostClues = null,   // 서막 선택·(구)유품함 단서 문자열 — 마이그레이션용
        float Unrest = 0f, List<LegendRec>? Legends = null, int LegendRefs = 0,   // 반란 지수·전설·카토 참조 카운터
        int FavorAtE1 = 0,   // 「황제의 게임」 E2 게이트 기준점(E1 시점 총애)
        List<KeepsakeRec>? Keepsakes = null, List<DebtTxnRec>? DebtLog = null,   // 보관함 유품·채무 원장
        string? TiebreakWinner = null,   // {scales} 우승 결정전 승자(시즌 한정)
        string[]? MasterTraitPool = null, string[]? MasterTacticPool = null,   // 스승 전수 후보 풀
        int BanquetSeason = 0,   // 후원자 연회(시즌 1회) 마지막 시즌
        int CampSeason = 0, int SparCupSeason = 0,   // (구) 프리시즌 1회 플래그 — 폐지, 호환용
        List<PressIssue>? PressArchive = null,   // 콜로세움 월보 영속 아카이브(#1)
        int PreWeek = 0,   // [19] 프리시즌 준비 주간 진척
        int FestStage = 0, List<string>? FestSlots = null,   // {masks} 대항전 단계·진출자
        string? FestRepId = null, string? FestChampion = null);   // {fest} 내 대표 지명·우승자
    private sealed record LudusRepRec(string Id, float Rep);
    private sealed record DebtTxnRec(string Reason, float Delta, int Season);   // 채무 원장 항목(영속)

    // ── season.json / API 문서 ──
    private sealed record EventDoc(string A, string B, float Score, string Winner, bool Ko);
    private sealed record FighterDoc(string Id, string Name, string Weapon, string Tactic, string Personality, int Age,
        int W, int L, int D, int Points, int Streak, int CW, int CL, int CD, float Fame, float Popularity, bool IsPlayer,
        string[]? Epithets = null, int Fatigue = 0, bool Injured = false, int Division = 1, int CKoW = 0,
        string Ludus = "");
    private sealed record RelDoc(string Self, string Opp, string Type, float Affinity, int Wins, int Losses);
    private sealed record StoryDoc(int Round, string Kind, string Text);
    private sealed record SeasonDoc(int SchemaVer, int SeasonNo, int Rounds, int Matches, int TotalMatches, bool Completed,
        string? NextA, string? NextB, bool NextIsEvent, string Champion,
        List<FighterDoc> Fighters, List<RelDoc> Relations, List<EventDoc> Events, List<StoryDoc> Story,
        List<MatchLogDoc> MatchLog, List<ChampionRec>? Champions = null, List<HallRec>? Hall = null,
        List<CalDoc>? Calendar = null, int Auc = 0,
        string? CurMonth = null, int CurDay = 0);   // 달력: 전 일정(과거+미래)+로마 날짜 · Cur* = 현재 날짜(오늘 강조)
    private sealed record CalDoc(int Idx, string Month, int Day, string A, string B, string Kind, string Format,
        string? Winner, bool IsPlayerMatch, bool IsNext, float Hype,
        bool Hot = false, string? Title = null);   // Idx = 재관전용 matchLog 인덱스(미래 경기는 -1) · Hot = 참가자 3연승+ · Title = 타이틀전 라벨(#5)

    private sealed record StatsDoc(float Atk, float Def, float Hp, float Spd, float Aspd, float Rct);
    private sealed record MyFighterDoc(string Id, string Name, string Weapon, string Personality, int Age, bool Aging,
        string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] TacticPool, string Tactic, int TrainingPoints,
        int W, int L, int D, int CW, int CL, int CD, float Fame, float Popularity,
        string[] Emotions,    // 다음 경기에 실릴 감정 ({speech} 예고)
        string[]? Epithets = null,    // 획득 이명
        int Fatigue = 0, bool Injured = false,   // 피로도(0쌩쌩~100탈진)·부상 여부
        bool AtCap = false, int BreakthroughCost = 0,   // 상한 도달·잠재력 돌파 비용(영광)
        int MGrit = 0, int MRecover = 0, int MShow = 0, int MPay = 0,   // 마스터리 레벨
        int CKoW = 0,   // 통산 KO승(스카우터 은퇴 자격 표시)
        string[]? Skills = null, int SkillSlots = 1,   // T12 패시브 스킬(id) + 슬롯 수
        PermInjuryInfo[]? PermInjuries = null);   // 영구 부상(#6) — 부위명 + 스탯 저하(수술 부위 선택용)
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
        float OddsA = 2f, float OddsB = 2f,   // 범용 배당(A/B 승 — AI 경기 베팅용)
        float FeeEstimate = 0f, float WinBonusEstimate = 0f,   // 예상 출전료·승리 보너스(#15 수익 가시화)
        float OddsAKo = 2f, float OddsADec = 2f, float OddsBKo = 2f, float OddsBDec = 2f,   // 승자×방식 조합 배당
        string? AId = null, string? BId = null,   // AI 경기 양측 선수 id(도박장 상세 열람용)
        string? MyQuote = null, string? OppQuote = null,   // 전투 직전 대사(#4) — 내 선수·상대
        bool IsMirror = false, string? OppId = null, string[]? OppPool = null, string? OppTactic = null);   // 내전(#6): 양측 다 내 모리튜리 → 이중 조종
    private sealed record LudusDoc(float Rep, int Tier, string TierName, string? NextTierName, float NextTierRep, float IncomeMult);
    private sealed record AchDoc(string Id, string Name, string Desc, bool Unlocked);
    private sealed record CupMatchDoc(string Stage, string A, string B, string? Winner);
    private sealed record LudusStandingDoc(string Name, float Rep, string TierName, int Members,
        string? TopFighter, int SeasonW, int SeasonL, int SeasonD, bool IsPlayer, float Treasury,
        string Persona = "", string Motto = "",   // 개성(W10b): gold/youth/blood + 좌우명
        string Lanista = "", string PatronName = "",   // [10] 검투소 구체화 — 주인·후원자
        string Id = "");   // [18] 상세 명부 열람용
    private sealed record RelRow(string OppName, string RelName, string RelIcon, int W, int L, int Enc, bool OppIsMine);
    private sealed record FighterProfileDoc(string Id, string Name, string Weapon, string Personality, int Age,
        bool IsPlayer, bool Aging, string Talent, string Potential, float PotentialBudget, float BudgetUsed,
        StatsDoc Stats, string[] Traits, string[] Epithets, string[] TacticPool, string Tactic,
        int W, int L, int D, int CW, int CL, int CD, int CKoW, int Titles, float Fame, float Popularity,
        RelRow[] Relations, string[] Emotions, string[] Chronicle, int Fatigue, bool Injured, string Ludus,
        string? EmoBio = null,   // 커리어 감정 이력 요약(심리 기질 — W10a)
        int BestStreak = 0, int Executions = 0, float AvgTime = 0f, float AvgDamage = 0f,   // 모리튜리 기록(#2)
        PermInjuryInfo[]? PermInjuries = null);   // 영구 부상(#6) — 부위명 + 스탯 저하
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
        List<MasterGiftDoc>? MasterTraits = null, List<string>? MasterTactics = null,   // 스승 전수 후보(선택식)
        float Patronage = 0f,   // 후원자 관계도(#7)
        GambleDoc? Gamble = null,   // 도박장 탭(#32)
        bool Redemption = false,   // 재기의 서약(강등 아크) 진행 중
        string? FixTarget = null,   // 승부조작 가담 예약 — 이 선수가 다음 경기를 던져야 한다
        CampaignDoc? Campaign = null, UnrestDoc? Unrest = null, List<LegendRec>? Legends = null,   // [13] 캠페인·반란 지수·전설
        DebtDoc? DebtInfo = null, List<KeepsakeRec>? Keepsakes = null,   // 채무 상세·보관함 유품
        PreseasonDoc? Preseason = null,   // [19] 프리시즌 준비 주간
        FestivalDoc? Festival = null);   // {masks} 사투르날리아 대항전(미드시즌)
    private sealed record FestivalDoc(int Stage, string? MyRep, bool Pickable, string? Champion, List<CupMatchDoc>? Bracket);   // {masks} 대항전
    private sealed record GambleDoc(float SeasonNet, int Hits, int Total, List<BetLogRec> Log, int Streak = 0);
    private sealed record DebtTxnDoc(string Reason, float Delta, int Season);
    private sealed record DebtDoc(float Total, List<DebtTxnDoc> Log, float Trust, string TrustLabel, float LoanLimit);
    private sealed record EdictDoc(string Desc, bool Done);
    private sealed record BetDoc(string On, float Amount, float Odds);
    private sealed record PerkDoc(string Id, string Name, string Desc, int Lv, int Max, int NextCost);
    private sealed record MasterGiftDoc(string Id, string Name);   // 스승 전수 후보 특성

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
        bool BetWon = false, string? BetNote = null,      // 이 경기 베팅 정산(결과 카드 연계)
        ExecVerdict? Exec = null,                         // {skull}처형전 엄지 판정(격전 패배 시)
        string? FixNote = null, bool FixBad = false,      // 승부조작 결말(가담 선수 경기 시)
        string? Cato = null);                             // 카토의 한 줄 평([13] 상시 코멘터리)

    /// <summary>{skull}처형전 엄지 판정 — 죽음은 주사위가 아니라 '군중과 황제의 마음'. 인기·드라마가 자비를 부른다.</summary>
    public sealed record ExecVerdict(string Loser, int DeathPct, bool Spared, string Factors);

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
        float BetNet = 0f, int GreatCount = 0, int Favor = 0, int GauntletWins = 0,   // 결산 대통합
        string? FestChampion = null, bool FestChampionMine = false,   // {masks} 대항전 우승자
        List<string>? Awards = null);   // {ludus} 시상식 — MVP·최다 KO·신인왕·인기왕

    /// <summary>세계 역사 — 역대 챔피언·명예의 전당(은퇴자) 영속 기록.</summary>
    private sealed record ChampionRec(int SeasonNo, string Name, string Record, bool IsPlayer);
    private sealed record HallRec(string Name, string Weapon, float Fame, string Career, int Age, int RetiredSeason, bool IsPlayer,
        int BestStreak = 0, int Executions = 0, int CKoW = 0, int Games = 0, float AvgTime = 0f, float AvgDmg = 0f);   // 모리튜리 기록(#2) 보존

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── 상태 ──
    private readonly List<Gladiator> _cast = new();
    private readonly List<Gladiator> _candidates = new();     // 대기 뽑기 후보 (전체 데이터 — JSON엔 마스킹)
    private readonly List<RevealDoc> _lastReveal = new();     // 직전 영입에서 공개된 미선택 후보(#8, 메모리 전용)
    private readonly RelationLedger _ledger = new();
    private int _rounds;   // 정규 라운드 수 — 라운드로빈 편성 시 부 인원으로 확정(개막·로드 시 재계산)
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
    // {masks} 사투르날리아 대항전(미드시즌) — 루두스별 대표 1인 토너먼트(순위 무관 축제전, 루두스의 명예)
    private int _festStage;                                  // 0=미개최 1~3=단계 진행 4=종료/생략
    private List<string> _festSlots = new();                 // 다음 단계 진출자(시드 순 — 부전승 선기입·승자 순차 기입)
    private string? _festRepId;                              // 라니스타가 지명한 내 대표(미지정=간판)
    private string? _festChampion;                           // 대항전 우승자 이름(시즌 한정 표시)
    private string? _pendingEventId, _pendingEventFighter;   // 시즌 중 텍스트 이벤트(2b) — 선택 대기
    private string? _fixFighterId;   // 승부조작: 이 선수가 다음 경기를 던져야 한다(가담 예약)
    private float _fixReward;        // 승부조작 성공 보수
    private string? _lastFixNote;    // 직전 경기의 승부조작 결말(결과 카드 표시)
    private bool _lastFixBad;        // 직전 승부조작 결말이 나쁜 것(발각·보복)인가 — 색 구분
    private float _patronage;   // 후원자 관계도(−100 압박 ~ +100 총애) — #7. 선택으로 변동, 시즌말 정산
    private void Patron(float d) => _patronage = Math.Clamp(_patronage + d, -100f, 100f);
    private string? _pendingProposalOpp;                     // 빅매치 제안(라니스타 개입) — 출전 선택 대기 상대 id
    private bool _proposalExec;                              // 제안이 원수의 처형전 도전장인가
    private readonly List<string> _lastFates = new();        // 직전 경기의 극적 운명(결과 화면 표시용)
    private readonly List<string> _lastInjuries = new();      // 직전 경기 신규 부상자(결과 카드 표시)
    private float _lastHype;                                   // 직전 경기 흥행도
    private bool _lastUpset;                                   // 직전 경기 대이변 여부(잭팟 연출)
    private ExecVerdict? _lastExec;                            // 직전 처형전 엄지 판정(연출)
    private float _lastDrama;                                // 직전 경기 드라마 스코어(명경기 보관 판정)
    private readonly List<GreatRec> _greatest = new();       // 명경기 보관함(top 12, 영속 — 스냅샷+시드 재관전)
    private int _rookieSeq;                                  // 신인 id 시리얼(중복 방지, 영속)
    private float _debt;                                     // 사채(이벤트 빚) — 시즌말 이자·상환·명성 압박
    private readonly List<DebtTxnRec> _debtLog = new();      // 채무 원장(발생·이자·상환 거래 — 도박장 빚 상세 열람)
    private readonly Dictionary<string, float> _rivalRep = new();   // 라이벌 루두스별 명성(경쟁 순위표)
    private int _emoGen;

    // ── 업적 정의 (조건은 코드에서 체크) ──
    // 업적: 보상 차등(골드·영광·명성). 종류·보상 다양화(#5).
    private static readonly (string Id, string Name, string Desc, float Gold, float Glory, float Rep)[] AchievementDefs =
    {
        ("first_win",    "첫 승리",       "내 모리튜리의 첫 승",           50f,  2f,  10f),
        ("first_title",  "리그 제패",     "리그 시즌 우승",              200f, 10f, 30f),
        ("first_cup",    "챔피언십 정복", "챔피언십 컵 우승",            300f, 14f, 40f),
        ("caesar",       "카이사르 발굴", "카이사르 천부 영입",          0f,   12f, 20f),
        ("legend",       "살아있는 전설", "내 모리튜리 명성 100 돌파",     0f,   16f, 30f),
        ("streak10",     "무패의 투사",   "내 모리튜리 10연승",            150f, 8f,  20f),
        ("empire",       "제국의 정점",   "루두스 최고 등급 달성",       0f,   20f, 0f),
        ("dynasty",      "왕조",          "리그 3연패",                  500f, 25f, 50f),
        // 신규(#5)
        ("executioner",  "콜로세움의 사형집행인", "처형전에서 승리",      200f, 8f,  25f),
        ("gambler",      "행운의 도박사",  "베팅 누적 10회 적중",        300f, 6f,  10f),
        ("giant_killer", "거인 사냥꾼",   "명성 2배 이상 상대 격파(이변)", 100f, 10f, 20f),
        ("kingmaker",    "명장의 산실",   "교관·스승·스카우터 배출",     0f,   12f, 25f),
        ("perfect",      "무결점 시즌",   "시즌 전승(내 모리튜리 전원)",   400f, 20f, 40f),
        ("tycoon",       "대부호",        "금고 2000 데나리우스 돌파",   0f,   10f, 15f),
        ("veteran",      "백전노장",      "내 모리튜리 통산 50승",         200f, 12f, 25f),
        // 엔드게임(C3): 명시적 최종 목표 — 끝이 보여야 완주 동기가 생긴다
        ("immortal_ludus", "불멸의 루두스", "명예의 전당 5인 배출 · 컵 3회 우승 · 카이사르 발굴 · 최고 등급 — 모든 것을 이룬 자", 1000f, 50f, 100f),
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
            var pool = RivalLudiPool.FirstOrDefault(r => r.Id == id);
            list.Add(new LudusStandingDoc(LudusNameOf(id), MathF.Round(rep), TierNameForRep(rep),
                m.Count, top?.Name, m.Sum(x => x.W), m.Sum(x => x.L), m.Sum(x => x.D), isPlayer, treasury,
                pool.Persona ?? "", pool.Motto ?? "",
                isPlayer ? "나" : LanistaOf(id), isPlayer ? "" : LudusPatronOf(id), id));
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
        if (def.Gold > 0) { _gold += def.Gold; rw.Add($"{{coin}}{def.Gold:F0}"); }
        if (def.Glory > 0) { AddGlory(def.Glory); rw.Add($"{{glory}}{def.Glory:F0}"); }
        if (def.Rep > 0) { _ludusRep += def.Rep; rw.Add($"명성 +{def.Rep:F0}"); }
        _story.Add((0, "achievement", $"{{laurel}} 업적 — {def.Name}: {def.Desc} ({string.Join(" ", rw)})"));
    }

    private int RosterCap => 3 + _quartersLv;
    private ulong SeasonSeed => _worldSeed + (ulong)_seasonNo * 1000003UL;
    private Gladiator ById(string id) => _cast.First(g => g.Id == id);
    private string PersOf(string id) => ById(id).PersonalityId;
    private int TitlesOf(Gladiator g) => _champions.Count(c => c.Name == g.Name);

    /// <summary>명전 등재용 레코드 — 은퇴/전사 시 모리튜리 기록(#2, 연승·처형·평균)을 박제해 은퇴 후에도 보존.</summary>
    private HallRec MakeHall(Gladiator g, string career, int season)
    {
        int n = g.CW + g.CL + g.CD;
        return new HallRec(g.Name, g.WeaponId.Replace("WPN_", ""), MathF.Round(g.Fame), career, g.Age, season, g.IsPlayer,
            g.BestStreak, g.Executions, g.CKoW, n, n > 0 ? g.TotalMatchTime / n : 0f, n > 0 ? g.TotalDamage / n : 0f);
    }

    private static uint HashId(string s) { uint h = 2166136261u; foreach (char c in s) { h ^= c; h *= 16777619u; } return h; }

    /// <summary>별명(#7) — 캐릭터성이 서린 특별한 경우에만 부여(업적·기질이 만든 별명). 평범한 모리튜리는 별명 없음(null). 단순 성격+무기 조합은 부여하지 않는다.</summary>
    private string? Nickname(Gladiator g)
    {
        int games = g.CW + g.CL + g.CD;
        // ── 캐릭터성이 서린 별명 — 조건이 강할수록 위(우선). 무기·성격·기록·신규 계측 스탯을 폭넓게 활용 ──
        if (games >= 35 && g.Fame >= 90f) return "콜로세움의 망령";                            // 오래 살아남은 전설
        if (g.Executions >= 6) return "학살자";                                               // 처형을 즐긴 자
        if (g.PersonalityId == "PER_CRUEL" && g.CKoW >= 8) return "피의 폭풍";                 // 잔혹 × 다수 KO
        if (g.BestStreak >= 12) return "무패의 질주";                                          // 대연승 기록
        if (g.WeaponId == "WPN_SHIELD" && g.Stats.Def >= 95f && games >= 12) return "난공불락"; // 뚫리지 않는 방패
        if (g.WeaponId == "WPN_AXE" && g.CKoW >= 6) return "붉은 늑대";                        // 도끼 × KO 사냥꾼
        if (g.WeaponId == "WPN_DUALBLADES" && g.Stats.Aspd >= 90f && g.CW >= 10) return "두 개의 달"; // 쌍검 속공
        if (g.WeaponId == "WPN_GREATSWORD" && g.TotalDamage >= 12000f) return "일격의 거인";    // 대검 파괴력
        if (g.WeaponId == "WPN_HAMMER" && g.Executions >= 3) return "대지를 부수는 자";          // 망치 처형
        if (g.WeaponId == "WPN_WHIP" && games >= 15 && g.TotalDodges >= games * 8) return "모래뱀"; // 채찍 회피
        if (g.WeaponId == "WPN_SPEAR" && g.Popularity >= 60f) return "황금 창";                // 군중을 홀린 창잡이
        if (g.WeaponId == "WPN_SWORD" && g.CW >= 20 && g.CL * 3 <= g.CW) return "검성";         // 검 × 압도적 승률
        if (g.PersonalityId == "PER_SHOWMAN" && g.Popularity >= 70f) return "콜로세움의 총아"; // 최고 인기 쇼맨
        if (g.PersonalityId == "PER_COWARD" && games >= 25 && g.CKoW == 0) return "불사조";     // 겁쟁이 × 오래 생존(무처형)
        if (g.PersonalityId == "PER_BOLD" && g.BestStreak >= 6 && g.CL >= 5) return "역전의 명수"; // 대담 × 부침
        if (games >= 20 && g.TotalDamageTaken > 0 && g.TotalDamageTaken <= games * 55) return "그림자"; // 안 맞는 자
        if (games >= 18 && g.TotalBlocks >= games * 12) return "성벽";                          // 막아내는 자
        if (g.Age <= 22 && g.Fame >= 55f) return "신동";                                       // 어린 나이의 명성
        return null;   // 별명 없음 — 별명은 아무나 얻는 게 아니다
    }

    /// <summary>획득 이명 — 특별 별명(#7, 조건부) + 통산 전적·KO·연승·우승·연륜 파생(저장 안 함, 읽을 때 계산). 넴시스 서사의 표지.</summary>
    private string[] Epithets(Gladiator g)
    {
        var e = new List<string>();
        if (Nickname(g) is { } nick) e.Add("{tag} " + nick);   // 별명은 있을 때만, 첫 자리
        int games = g.CW + g.CL + g.CD, titles = TitlesOf(g);
        if (titles >= 3) e.Add("{crown} 패왕");
        else if (titles >= 1) e.Add("{crown} 챔피언");
        if (g.CL == 0 && g.CW >= 5) e.Add("{shield} 불패");
        if (g.Streak >= 6) e.Add("{bolt} 파죽지세");
        if (g.CKoW >= 4 && g.CKoW * 2 >= Math.Max(1, g.CW)) e.Add("{skull} 처형자");
        if (g.Fame >= 120f) e.Add("{star} 전설");
        if (g.Popularity >= 60f) e.Add("{masks} 군중의 연인");
        if (g.GrudgeCount >= 3) e.Add("{swords} 원한의 화신");                    // 다수와 척진 자
        if (g.CD >= 5 && g.CD * 2 >= Math.Max(1, games)) e.Add("{scales} 판정의 달인"); // 판정으로 사는 자
        if (g.Age >= 34 || games >= 40) e.Add("{laurel} 백전노장");
        if (games >= 1 && games <= 4) e.Add("{sprout} 신예");                    // 데뷔 직후 — 첫 경기 이후 지급(#4)
        return e.Take(4).ToArray();   // 별명 1 + 획득 이명 최대 3
    }

    private static string WpnKo(string wid) => wid switch
    {
        "WPN_SWORD" => "검", "WPN_SPEAR" => "창", "WPN_AXE" => "도끼", "WPN_GREATSWORD" => "대검",
        "WPN_DUALBLADES" => "쌍검", "WPN_HAMMER" => "망치", "WPN_WHIP" => "채찍", "WPN_SHIELD" => "방패", _ => wid.Replace("WPN_", ""),
    };

    /// <summary>성격 한글명 — 성격 변화 표기(이전 → 새) 등에 사용.</summary>
    private static string PerKo(string id) => id switch
    {
        "PER_CALM" => "냉철", "PER_RECKLESS" => "충동", "PER_ARROGANT" => "오만", "PER_HONORABLE" => "고결",
        "PER_COWARD" => "겁쟁이", "PER_SHOWMAN" => "쇼맨", "PER_OPPORTUNIST" => "기회주의", "PER_CRUEL" => "잔혹",
        "PER_BOLD" => "대담", "PER_WARY" => "신중", _ => id.Replace("PER_", ""),
    };

    /// <summary>성격별 전투 직전 대사 풀(#4) — 쇼맨=화려·고결=예의·잔혹=살벌. 관계·감정이 없을 때의 기본.</summary>
    private static string[] PersonaQuotes(string pid) => pid switch
    {
        "PER_SHOWMAN"   => new[] { "관중이여, 오늘 최고의 쇼를 보여주마!", "피와 환호 — 그것이 나의 무대다!", "눈을 떼지 마라. 순식간에 끝날 테니!" },
        "PER_HONORABLE" => new[] { "정정당당한 승부를 바라오.", "모리튜리의 명예를 걸고 싸우겠소.", "그대의 무운을 빈다 — 모래 위에서 만나지." },
        "PER_CRUEL"     => new[] { "오늘 반드시 죽이겠다.", "네 비명이 벌써 들리는군.", "천천히… 아주 천천히 끝내주마." },
        "PER_CALM"      => new[] { "그는 내 상대가 아니다.", "감정은 필요 없다. 오직 검뿐.", "끝은 이미 정해져 있다." },
        "PER_RECKLESS"  => new[] { "다 덤벼! 상관없다!", "몸이 근질거린다!", "생각 따윈 필요 없어 — 부딪칠 뿐!" },
        "PER_ARROGANT"  => new[] { "감히 나와 같은 모래를 밟다니.", "이 몸의 상대가 될 줄 알았나?", "무릎 꿇을 준비는 됐나?" },
        "PER_COWARD"    => new[] { "제발… 무사히 끝나기를.", "왜 하필 나란 말인가…", "살아남는 게 이기는 거다." },
        "PER_BOLD"      => new[] { "난 아직 끝나지 않았다.", "물러설 곳은 없다. 나아갈 뿐.", "두려움? 그런 건 버린 지 오래다." },
        "PER_WARY"      => new[] { "방심은 금물. 한 수 한 수 신중히.", "상대의 빈틈을 놓치지 않겠다.", "서두를 이유가 없다." },
        _               => new[] { "기회는 한 번. 놓치지 않는다.", "네 실수가 곧 나의 승리다.", "언제 찌를지는 내가 정한다." },   // OPPORTUNIST·기타
    };

    /// <summary>전투 직전 대사(#4) — 관계(원수·라이벌·공포·친구) &gt; 감정(자만·트라우마·동기부여·자신감) &gt; 성격 순으로 유기적 선택. 연출 전용(Sim 무관).</summary>
    private string PreMatchQuote(Gladiator self, Gladiator opp)
    {
        var h2h = _ledger.Get(self.Id, opp.Id);
        var rel = h2h.Classify(self.PersonalityId);
        string emo = self.PendingEmotions.FirstOrDefault() ?? "";
        string[] pool =
            rel == RelationType.Nemesis ? new[] { "오늘, 그 빚을 피로 갚겠다.", $"{opp.Name}… 이 순간을 얼마나 기다렸는지.", "다시는 일어서지 못하게 해주지." }
          : rel == RelationType.Fear    ? new[] { "…또 저 자와 싸워야 하는가.", "침착하자. 이번엔 다르다.", "두렵지 않다… 두렵지 않다." }
          : rel == RelationType.Rival   ? new[] { $"{opp.Name}, 오늘은 내가 위라는 걸 증명하겠다.", "너와의 승부는 언제나 짜릿하지.", "실력으로 가리자." }
          : rel == RelationType.Friend  ? new[] { "미안하다, 친구. 봐주진 않겠다.", "모래 위에선 우리도 남이다.", "좋은 승부가 되길." }
          : emo == EmotionTable.Hubris     ? new[] { $"{opp.Name}? 이름도 기억나지 않는군.", "이건 경기가 아니라 처형이다." }
          : emo == EmotionTable.Trauma     ? new[] { "…아직 그날의 통증이 가시지 않았다.", "이번엔… 반드시 넘어서겠다." }
          : emo == EmotionTable.Motivated  ? new[] { "지난 패배가 나를 더 강하게 만들었다.", "오늘, 모든 걸 쏟아붓겠다." }
          : emo == EmotionTable.Confident  ? new[] { "몸이 가볍다. 오늘은 이긴다.", "준비는 끝났다." }
          : PersonaQuotes(self.PersonalityId);
        ulong seed = HashId(self.Id) ^ ((ulong)(uint)_cursor * 2654435761UL) ^ SeasonSeed;
        return pool[(int)(new SimRandom(seed).NextFloat01() * pool.Length) % pool.Length];
    }

    /// <summary>타이틀전 다양성(#5) — 컵/처형/복수전/라이벌전/신인전/노장전/빅매치를 관계·경력으로 분류. 없으면 null(평범한 경기).</summary>
    private string? BoutTitle(Gladiator A, Gladiator B, SchedRec s)
    {
        if (s.Kind == "cup_final") return "{crown} 챔피언십 컵 결승";
        if (s.Kind == "cup_sf") return "{trophy} 챔피언십 컵 4강";
        if (s.Kind == "fest_final") return "{masks} 사투르날리아 대항전 결승 — 루두스의 명예";
        if (s.Kind == "fest_sf") return "{masks} 사투르날리아 대항전 4강";
        if (s.Kind == "fest_qf") return "{masks} 사투르날리아 대항전 8강";
        if (s.Kind == "tiebreak") return "{scales} 우승 결정전 — 동률의 저울, 단판 승부";
        if (s.Format == "execution") return "{skull} 처형전 — 패자는 죽을 수 있다 (보상 ×3)";
        var ab = _ledger.Get(A.Id, B.Id); var ba = _ledger.Get(B.Id, A.Id);
        var ra = ab.Classify(A.PersonalityId); var rb = ba.Classify(B.PersonalityId);
        if (ra == RelationType.Nemesis || rb == RelationType.Nemesis) return "{swords} 복수전 — 원한의 재대결";
        if (ra == RelationType.Rival || rb == RelationType.Rival) return "{flame} 라이벌전";
        if (A.CW + A.CL + A.CD == 0 || B.CW + B.CL + B.CD == 0) return "{sprout} 신인전 — 데뷔 무대";
        if (A.Age >= A.AgingStartAge + 5 || B.Age >= B.AgingStartAge + 5) return "{laurel} 노장의 무대";
        if (s.IsEvent) return "{star} 빅매치";
        return null;
    }

    // ── 기록실(#2): 리그 전체 모리튜리의 통산 지표 — 클라가 내림차순 막대그래프로 렌더 ──
    private sealed record RecordRow(string Name, string Ludus, bool Mine, int BestStreak, int Executions,
        float DamageDealt, float DamageTaken, int Blocks, int Dodges, float Fame, float Popularity,
        int KoWins, int Wins, float AvgTime, int Titles);
    public string RecordsJson()
    {
        if (_playerless) return Err("CLI 모드");
        var rows = _cast.Select(g =>
        {
            int games = g.CW + g.CL + g.CD;
            return new RecordRow(g.Name, LudusNameOf(g.LudusId), g.IsPlayer, g.BestStreak, g.Executions,
                MathF.Round(g.TotalDamage), MathF.Round(g.TotalDamageTaken), g.TotalBlocks, g.TotalDodges,
                MathF.Round(g.Fame), MathF.Round(g.Popularity), g.CKoW, g.CW,
                games > 0 ? MathF.Round(g.TotalMatchTime / games * 10f) / 10f : 0f, TitlesOf(g));
        }).ToList();
        return JsonSerializer.Serialize(new { ok = true, rows }, JsonOpts);
    }

    // ── 후원자(#1): 명명·직급 + 검투소 관계표 ──
    private static readonly (string Name, string Rank)[] PatronPool =
    {
        ("루키우스 코르넬리우스", "원로원 의원"), ("가이우스 아우렐리우스", "재무관(콰이스토르)"),
        ("마르쿠스 발레리우스", "법무관(프라이토르)"), ("퀸투스 파비우스", "조영관(아이딜리스)"),
        ("티투스 클라우디우스", "속주 총독"), ("푸블리우스 세르빌리우스", "기사 계급 부호(에퀴테스)"),
    };
    private sealed record PatronDoc(string Name, string Rank, string Ludus, int Relation, string RelationLabel, string Note);
    private sealed record LudusRelRow(string Ludus, string Persona, string Motto, int Wins, int Losses, int Grudges, string Relation, string Icon,
        string Lanista = "", string PatronName = "");   // [10] 검투소 구체화
    public string PatronJson()
    {
        if (_playerless) return Err("CLI 모드");
        var pt = PatronPool[(int)(_worldSeed % (ulong)PatronPool.Length)];
        string label = _patronage >= 40f ? "총애" : _patronage >= 10f ? "호의적" : _patronage > -10f ? "관망" : _patronage > -40f ? "냉담" : "적대";
        string note = _patronage >= 40f ? "\"자네 검투소라면 얼마든 대주지. 계속 이기게.\""
                    : _patronage >= 10f ? "\"지켜보고 있네. 실망시키지 말게.\""
                    : _patronage > -10f ? "\"아직 자네에 대한 판단은 미루고 있네.\""
                    : _patronage > -40f ? "\"요즘 자네 소문이 영 좋지 않더군.\""
                    : "\"자네에게 걸었던 내 이름값이 아깝네.\"";
        var patron = new PatronDoc(pt.Name, pt.Rank, PlayerLudusName, (int)MathF.Round(_patronage), label, note);

        var mine = _cast.Where(g => g.IsPlayer).ToList();
        var rels = ActiveRivalLudi.Select(r =>
        {
            var theirs = _cast.Where(g => g.LudusId == r.Id).ToList();
            int w = 0, l = 0, grudge = 0;
            foreach (var me in mine)
                foreach (var op in theirs)
                {
                    var e = _ledger.Get(me.Id, op.Id); w += e.Wins; l += e.Losses;
                    if (e.Classify(me.PersonalityId) is RelationType.Nemesis) grudge++;
                    if (_ledger.Get(op.Id, me.Id).Classify(op.PersonalityId) is RelationType.Nemesis) grudge++;
                }
            (string rl, string ic) = (grudge >= 2 || l > w + 2) ? ("앙숙", "{swords}")
                : grudge >= 1 ? ("라이벌", "{flame}")
                : w > l + 2 ? ("우세", "{fist}")
                : w + l == 0 ? ("접점 없음", "·")
                : ("경쟁", "{handshake}");
            return new LudusRelRow(r.Name, r.Persona, r.Motto, w, l, grudge, rl, ic, LanistaOf(r.Id), LudusPatronOf(r.Id));
        }).ToList();
        return JsonSerializer.Serialize(new { ok = true, patron, ludusRelations = rels }, JsonOpts);
    }

    // ── [18] 검투소 상세 명부(클릭 열람) ──
    private sealed record LudusRosterRow(string Name, string Weapon, string Personality, int Age, float Fame, float Popularity, string Career);
    public string LudusDossierJson(string id)
    {
        var pool = RivalLudiPool.FirstOrDefault(r => r.Id == id);
        bool isPlayer = id == PlayerLudusId;
        if (pool.Id == null && !isPlayer) return Err("검투소를 찾을 수 없다");
        var members = _cast.Where(g => g.LudusId == (isPlayer ? PlayerLudusId : id))
            .OrderByDescending(g => g.Fame)
            .Select(g => new LudusRosterRow(g.Name, WpnKo(g.WeaponId), PerKo(g.PersonalityId), g.Age,
                MathF.Round(g.Fame), MathF.Round(g.Popularity), $"{g.CW}-{g.CL}-{g.CD}")).ToList();
        float rep = isPlayer ? _ludusRep : _rivalRep.GetValueOrDefault(id);
        var lc = isPlayer ? ("나 자신", "\"모래가 곧 이야기다.\"") : LanistaCharOf(id);
        var dyn = isPlayer ? null : LudusDynamic(id, Math.Max(1, _seasonNo));
        return JsonSerializer.Serialize(new
        {
            ok = true,
            name = isPlayer ? PlayerLudusName : pool.Name,
            persona = isPlayer ? "" : pool.Persona,
            motto = isPlayer ? "" : pool.Motto,
            lanista = isPlayer ? "나 (라니스타)" : LanistaOf(id),
            lanistaTrait = lc.Item1, lanistaQuote = lc.Item2,
            patron = isPlayer ? "—" : LudusPatronOf(id),
            patronRel = isPlayer ? "" : PatronEmperorRel(id),
            philosophy = isPlayer ? "직접 쓰는 이야기" : PhilosophyOf(pool.Persona),
            tier = TierNameForRep(rep), rep = MathF.Round(rep),
            treasury = isPlayer ? MathF.Round(_gold) : MathF.Round(rep * 6f + members.Sum(m => m.Popularity) * 2f),
            dynamic = dyn == null ? null : new { dyn.Icon, dyn.Title, dyn.Desc },
            members
        }, JsonOpts);
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
                var icon = t.type switch { RelationType.Nemesis => "{swords}", RelationType.Fear => "{skull}",
                    RelationType.Rival => "{flame}", RelationType.Obsession => "{target}", RelationType.Envy => "{eye}",
                    RelationType.Respect => "{handshake}", RelationType.Friend => "{heart}", _ => "{person}" };
                var opp = _cast.FirstOrDefault(c => c.Id == t.x.Opp);
                return new RelRow(opp?.Name ?? t.x.Opp, rd.Name, icon, t.x.Wins, t.x.Losses, t.x.Encounters,
                    opp?.IsPlayer ?? false); })
            .ToArray();

        // 연대기: 통산 우승 이력(영속) + 현 시즌 이 선수가 등장한 서사
        var chron = new List<string>();
        foreach (var c in _champions.Where(c => c.Name == g.Name))
            chron.Add($"{{trophy}} 시즌 {c.SeasonNo} 리그 챔피언 ({c.Record})");
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
            g.Fatigue, g.InjuryMatches > 0, LudusNameOf(g.LudusId),
            EmoBio: g.EmoHistory.Count > 0
                ? string.Join(" · ", g.EmoHistory.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => $"{EmotionTable.Get(kv.Key).Name} ×{kv.Value}"))
                : null,
            BestStreak: g.BestStreak, Executions: g.Executions,
            AvgTime: (g.CW + g.CL + g.CD) > 0 ? g.TotalMatchTime / (g.CW + g.CL + g.CD) : 0f,
            AvgDamage: (g.CW + g.CL + g.CD) > 0 ? g.TotalDamage / (g.CW + g.CL + g.CD) : 0f,
            PermInjuries: g.PermInjuries.Count > 0 ? PermInjuryInfos(g) : null);
        return JsonSerializer.Serialize(doc, JsonOpts);
    }

    // ── 시즌 중 텍스트 이벤트(2b) — 라니스타의 선택. 효과는 전부 기존 메커니즘(재화·명성·인기·훈련·감정·스탯). ──
    private sealed record TextEventDoc(string Id, string Icon, string Title, string Body, string[] Choices, string Kind = "dialogue", string? From = null);
    private sealed record ProposalPickDoc(string Id, string Name, string Weapon, string Personality, int Fatigue, bool Injured);
    private sealed record ProposalDoc(string OppName, string OppWeapon, string OppPersonality, int OppAge, float OppFame,
        string OppCareer, ProposalPickDoc[] Roster, bool Execution = false);
    private sealed class EvtTemplate
    {
        public required string Id, Icon, Title;
        public required bool NeedsFighter;
        public required Func<string, string> Body;                       // 대상 이름 → 본문
        public required (string Label, Func<Gladiator?, string> Apply)[] Choices;
        public string Kind = "dialogue";                                 // "letter" = 화면 중앙 편지 개봉 UI(초상 없음), "dialogue" = 초상 대화
    }

    /// <summary>스탯을 상한(잠재력 버짓) 내에서 영구 조정 — 여유 없으면 훈련 포인트로 환급. axis: Atk/Def/Rct.</summary>
    private string NudgeStat(Gladiator g, string axis, float amt)
    {
        if (BudgetUsed(g.Stats) + amt > g.PotentialBudget) { g.TrainingPoints += 1; return "상한이 꽉 차 훈련 포인트로 전환"; }
        int idx = axis switch { "Atk" => 0, "Def" => 1, "Rct" => 5, _ => 0 };
        g.Stats = WithAxis(g.Stats, idx, amt);
        return $"{axis} +{amt:F0}";
    }

    /// <summary>채무 증감 1건 = 원장 기록 + 총액 반영(음수 = 상환). 총액은 0 밑으로 내려가지 않는다.</summary>
    private void DebtTxn(string reason, float delta)
    {
        _debt = MathF.Max(0f, _debt + delta);
        _debtLog.Add(new DebtTxnRec(reason, MathF.Round(delta), Math.Max(1, _seasonNo)));
        if (_debtLog.Count > 40) _debtLog.RemoveRange(0, _debtLog.Count - 40);   // 원장 상한(최근 40건)
    }

    /// <summary>이벤트 지불: 골드가 부족해도 거래는 성사된다 — 부족분은 사채(원금 1.5배)로. 빚은 시즌말 이자·명성 압박.</summary>
    private string SpendOrDebt(float cost, string reason = "긴급 사채")
    {
        if (_gold >= cost) { _gold -= cost; return $"골드 −{cost:F0}"; }
        float shortfall = cost - _gold; _gold = 0f;
        DebtTxn($"{reason} (원금 1.5배)", shortfall * 1.5f);
        _story.Add((0, "debt", $"{{coin}} 사채 — 부족분 {shortfall:F0}을 빚으로 (원금 1.5배, 채무 {_debt:F0})"));
        return $"골드 바닥 → 부족분 {shortfall:F0} 사채(채무 {_debt:F0})";
    }

    // ── 채권자의 신뢰·대출 (검은 인장 사채업 — 도박장 빚 상세 탭) ──
    /// <summary>채권자의 신뢰 0~100 — 명성·등급이 올리고, 현재 빚이 갉아먹는다.</summary>
    private float DebtTrust => Math.Clamp(30f + LudusTier() * 12f + _ludusRep * 0.1f - _debt * 0.15f, 0f, 100f);
    /// <summary>추가로 빌릴 수 있는 한도 — 신뢰·등급에 비례, 현재 빚을 제한다.</summary>
    private float LoanLimit => MathF.Max(0f, MathF.Round(DebtTrust * (2f + LudusTier())) - _debt);
    private static string TrustLabel(float t) => t >= 75f ? "귀한 손님" : t >= 50f ? "쓸 만한 고객" : t >= 25f ? "지켜보는 중" : "믿을 수 없는 자";

    /// <summary>검은 인장에서 대출 — 즉시 골드, 채무는 원금 1.2배(계획적 차입은 비상 사채 ×1.5보다 유리).</summary>
    public string LoanJson(float amount)
    {
        amount = MathF.Round(amount);
        if (amount <= 0f) return Err("금액이 올바르지 않다");
        if (amount > LoanLimit + 0.5f) return Err($"검은 인장이 그만큼은 내주지 않는다 (한도 {LoanLimit:F0})");
        _gold += amount;
        DebtTxn("검은 인장 대출 (원금 1.2배)", amount * 1.2f);
        _story.Add((_rounds + 1, "debt", $"{{heart}} 대출 — 검은 인장에서 {amount:F0}을 빌렸다 (채무 +{MathF.Round(amount * 1.2f):F0})"));
        SaveWorld(); return StateJson();
    }
    /// <summary>임의 상환 — 골드·잔여 채무 한도에서 갚는다.</summary>
    public string RepayJson(float amount)
    {
        if (_debt <= 0f) return Err("갚을 빚이 없다");
        float pay = MathF.Min(MathF.Min(MathF.Round(amount), _gold), _debt);
        if (pay <= 0f) return Err("상환할 골드가 없다");
        _gold -= pay; DebtTxn("상환", -pay);
        _story.Add((_rounds + 1, "debt", $"{{coin}} 상환 — {pay:F0}을 갚았다 (잔여 채무 {_debt:F0})"));
        SaveWorld(); return StateJson();
    }
    private DebtDoc BuildDebtDoc() => new(MathF.Round(_debt),
        _debtLog.Count > 0 ? _debtLog.Select(x => new DebtTxnDoc(x.Reason, x.Delta, x.Season)).ToList() : new(),
        MathF.Round(DebtTrust), TrustLabel(DebtTrust), MathF.Round(LoanLimit));

    private List<EvtTemplate> EvtTemplates() => new()
    {
        new EvtTemplate { Id = "training", Icon = "{fist}", Title = "혹독한 훈련", NeedsFighter = true,
            Body = n => $"{n}이(가) 땀에 젖은 채 훈련장에 남아 라니스타을 노려본다.\n{{speech}} {n}: \"더 강해질 수 있습니다. 몸이 부서지더라도 — 허락해 주십시오.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("강행군 (훈련 포인트 +2, 인기 −5)", g => { g!.TrainingPoints += 2; g.Popularity = MathF.Max(0, g.Popularity - 5); return $"{g.Name} 훈련 포인트 +2, 인기 −5"; }),
                ("휴식 (인기 +5)", g => { g!.Popularity += 5; return $"{g.Name} 인기 +5"; }) } },

        new EvtTemplate { Id = "patron", Icon = "{coin}", Title = "후원자의 제안", NeedsFighter = false,
            Body = _ => "부유한 원로원 의원 그라쿠스가 두둑한 금화 주머니를 탁자에 던진다.\n{speech} 그라쿠스: \"자네 루두스의 이름을 내 연회에 좀 빌리세. 서로 좋은 거래 아닌가?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받는다 (골드 +80, 명성 −15, 후원 +15)", _ => { _gold += 80f; _ludusRep = MathF.Max(0, _ludusRep - 15f); Patron(15f); return "골드 +80, 명성 −15, 후원 +15"; }),
                ("거절한다 (명성 +20, 후원 −10)", _ => { AddRep(20f); Patron(-10f); return "명성 +20, 후원 −10 — \"고집스러운 친구로군.\""; }) } },

        // ── 신규 미션(#13) — 수락/거절 · 대사 포함(#9) · 일부는 후원 관계(#7) 변동 ──
        new EvtTemplate { Id = "fix", Icon = "{dice}", Title = "승부조작 제안", NeedsFighter = true,
            Body = n => $"복면의 사내가 도박장의 뒷돈 냄새를 풍기며 다가온다.\n{{speech}} 복면인: \"다음 경기, {n}이(가) 져주기만 하면 되네. 보수는 지고 나서. 허튼짓하면… 알지?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                // 선입금 없음 — 실제로 그 선수가 다음 경기에서 져야 보수 지급(발각 리스크). 이기면 뒷돈 주인의 보복.
                ("가담한다 (다음 경기에서 져야 골드 +150 · 이기면 보복)", g => {
                    if (!SeasonActive) return "시즌이 시작되면 다시 오라 — 던질 경기가 없다";
                    _fixFighterId = g!.Id; _fixReward = 150f;
                    return $"{{dice}} 검은 거래 성립 — {g.Name}이(가) 다음 경기를 던져야 한다. 이기거나 비기면 뒷돈의 주인이 가만있지 않는다"; }),
                ("거절 (명성 +15, 후원 +10)", g => { AddRep(15f); Patron(10f); return "명성 +15, 후원 +10 — \"청렴한 라니스타라, 흔치 않지.\""; }) } },

        new EvtTemplate { Id = "tribute", Icon = "{ludus}", Title = "총독의 조공 요구", NeedsFighter = false,
            Body = _ => "속주 총독의 전령이 두루마리를 펼친다.\n{speech} 전령: \"총독께서 검투 흥행세를 인상하셨소. 성의를 보이는 게 좋을 거요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("바친다 (골드 −70 · 부족분 빚, 후원 +20)", _ => { var pay = SpendOrDebt(70f); Patron(20f); return $"{pay}, 후원 +20 — 총독의 눈에 들었다"; }),
                ("버틴다 (후원 −20, 다음 시즌 압박)", _ => { Patron(-20f); return "후원 −20 — \"기억해 두겠소.\" (관계 악화)"; }) } },

        new EvtTemplate { Id = "duel", Icon = "{swords}", Title = "결투 신청", NeedsFighter = true,
            Body = n => $"경쟁 검투소의 투사가 {n}의 면전에 장갑을 던진다.\n{{speech}} 도전자: \"소문난 실력, 모래 위에서 증명해보시지. 겁이 나거든 물러서든가.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받아들인다 (인기 +14, 다음 경기 '투지')", g => { g!.Popularity += 14f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated); return $"{g.Name} 인기 +14, 다음 경기 '투지'"; }),
                ("품위있게 거절 (명성 +5, 인기 −4)", g => { g!.Fame += 5f; g.Popularity = MathF.Max(0, g.Popularity - 4f); return $"{g.Name} 명성 +5, 인기 −4"; }) } },

        new EvtTemplate { Id = "brawl", Icon = "{mug}", Title = "술집 시비", NeedsFighter = true,
            Body = n => $"선술집에서 취객 무리가 {n}의 탁자를 걷어찬다.\n{{speech}} 취객: \"검투장 밖에선 별 것 아니구만? 어디 한 번 놀아보자고!\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("주먹으로 답한다 (인기 +10, 부상 위험)", g => {
                    var rng = new SimRandom(SeasonSeed ^ 0xB4A_1234UL + (ulong)_matchIdx * 17UL);
                    g!.Popularity += 10f;
                    if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated);
                    if (rng.Roll(0.30f)) { g.InjuryMatches = Math.Max(g.InjuryMatches, 1); return $"{g.Name} 인기 +10, 다음 경기 '투지' — 하지만 난투 중 부상(1경기)"; }
                    return $"{g.Name} 취객들을 때려눕혔다 — 인기 +10, 다음 경기 '투지'"; }),
                ("자리를 뜬다 (인기 −4)", g => { g!.Popularity = MathF.Max(0, g.Popularity - 4f); return $"{g.Name} 조용히 물러났다 — 인기 −4"; }) } },

        new EvtTemplate { Id = "temple", Icon = "{ludus}", Title = "신전 봉헌", NeedsFighter = false,
            Body = _ => "마르스 신전의 사제가 향을 피우며 청한다.\n{speech} 사제: \"승리의 신께 봉헌하라, 라니스타여. 신들은 관대한 자를 굽어살피신다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("봉헌한다 (골드 −50 · 부족분 빚, {{glory}}+3)", _ => { var pay = SpendOrDebt(50f); AddGlory(3f); return $"{pay}, {{glory}}+3 — 신들의 가호"; }),
                ("검약한다 (골드 보존)", _ => "정중히 향만 올렸다.") } },

        new EvtTemplate { Id = "crowd", Icon = "{masks}", Title = "군중의 갈망", NeedsFighter = true,
            Body = n => $"관중석에서 {n}의 이름을 연호하는 함성이 터진다.\n{{speech}} 흥행주: \"군중이 피와 볼거리를 원하네! 자네 모리튜리, 쇼를 보여줄 수 있겠나?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("응한다 (인기 +12, 다음 경기 흥분)", g => { g!.Popularity += 12f; if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated); return $"{g.Name} 인기 +12, 다음 경기 '동기부여'"; }),
                ("침착하게 (명성 +8)", g => { g!.Fame += 8f; return $"{g.Name} 명성 +8"; }) } },

        new EvtTemplate { Id = "taunt", Icon = "{speech}", Title = "라이벌의 조롱", NeedsFighter = true,
            Body = n => {
                var self = _cast.FirstOrDefault(g => g.Id == _pendingEventFighter);
                var foe = self != null ? PickGrudgeTarget(self) : null;
                string fn = foe?.Name ?? "한 모리튜리";
                return $"광장에서 {fn}이(가) {n}을(를) 향해 침을 뱉으며 비웃는다.\n{{speech}} {fn}: \"{n}? 겁쟁이한테 붙은 과분한 이름이지. 모래 위에서 울게 해주마.\""; },
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("맞받아친다 (인기 +6, 라이벌에게 원한을 새긴다)", g => { g!.Popularity += 6f;
                    var t = PickGrudgeTarget(g);
                    if (t != null) { _ledger.DeepenGrudge(g.Id, t.Id, 20f); return $"{g.Name} 인기 +6 — {t.Name}을(를) 숙적으로 새겼다 (원한)"; }
                    return $"{g.Name} 인기 +6"; }),
                ("무시한다 (명성 +6)", g => { g!.Fame += 6f; return $"{g.Name} 명성 +6"; }) } },

        new EvtTemplate { Id = "mentor", Icon = "{scroll}", Title = "노장의 지도", NeedsFighter = true,
            Body = n => $"한쪽 눈에 흉터가 있는 늙은 모리튜리가 {n}을 지켜보다 입을 연다.\n{{speech}} 노장: \"자네, 재능은 있군. 허나 다듬지 않은 검은 무디지. 며칠만 내게 맡겨보게 — 공짜는 아니네만.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("수련한다 (골드 −40 · 부족분은 빚)", g => { var pay = SpendOrDebt(40f); var r = NudgeStat(g!, "Rct", 3f); return $"{pay}, {g!.Name} {r}"; }),
                ("사양한다", g => "정중히 사양했다.") } },

        new EvtTemplate { Id = "rival_letter", Icon = "{blood}", Title = "라이벌 루두스의 서신", NeedsFighter = true, Kind = "letter",
            Body = n => { var b = ActiveRivalLudi.FirstOrDefault(r => r.Persona == "blood");
                string ln = b.Name ?? "경쟁 검투소";
                var self = _cast.FirstOrDefault(g => g.Id == _pendingEventFighter);
                var foe = self != null ? PickGrudgeTarget(self, b.Id) : null;
                string fn = foe?.Name ?? "간판 모리튜리";
                return $"{ln}의 인장이 찍힌 서신이 도착했다 — {fn}의 이름으로 온 도발이다.\n{{speech}} {fn}: \"{n} 따위를 모리튜리라 부르나? 우리 모래 위에선 한 합도 못 버틸 것을. — {fn}, {ln}\""; },
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("공개 답신으로 맞받아친다 (인기 +8, 그 검투소에 원한)", g => { g!.Popularity += 8f;
                    var bl = ActiveRivalLudi.FirstOrDefault(r => r.Persona == "blood");
                    var t = PickGrudgeTarget(g, bl.Id);
                    if (t != null) { _ledger.DeepenGrudge(g.Id, t.Id, 22f); return $"{g.Name} 인기 +8 — {t.Name}({LudusNameOf(t.LudusId)})에게 원한을 품었다"; }
                    return $"{g.Name} 인기 +8 — 관중이 두 검투소의 신경전을 즐긴다"; }),
                ("품위를 지킨다 (루두스 명성 +8)", _ => { AddRep(8f); return "루두스 명성 +8 — \"짖는 개는 물지 않는 법.\""; }) } },

        new EvtTemplate { Id = "blackmarket", Icon = "{sword}", Title = "암시장 무기상", NeedsFighter = true,
            Body = n => $"후드를 쓴 상인이 천을 걷어 시퍼런 칼날을 드러낸다.\n{{speech}} 무기상: \"{n}에게 딱이지. 규정보다 조금… 예리할 뿐이야. 심판이 눈치채지만 않으면 돼.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("산다 (골드 −60 · 부족분은 빚)", g => { var pay = SpendOrDebt(60f); var r = NudgeStat(g!, "Atk", 3f); return $"{pay}, {g!.Name} {r}"; }),
                ("정직하게 (명성 +10)", g => { AddRep(10f); return "루두스 명성 +10"; }) } },
    };

    /// <summary>원한 이벤트용 실제 표적 선정 — 원한은 특정 상대에게 향한다. 이미 척진 상대(음의 affinity 최대) 우선,
    /// 없으면 (선호 검투소의) 라이벌 AI 무작위. 추상적 도발을 실제 관계 그래프로 못박아 다음 대결의 서사를 만든다.</summary>
    private Gladiator? PickGrudgeTarget(Gladiator self, string? preferLudus = null)
    {
        var ai = _cast.Where(g => !g.IsPlayer && g.Id != self.Id).ToList();
        if (ai.Count == 0) return null;
        var enemy = ai.OrderBy(g => _ledger.Get(self.Id, g.Id).Affinity).First();
        if (_ledger.Get(self.Id, enemy.Id).Affinity < 0f) return enemy;   // 이미 척진 상대에게 원한이 깊어진다
        var rng = new SimRandom(_worldSeed ^ 0x6D0E_5EEDUL + (ulong)(_matchIdx * 7 + 1));
        var pool = preferLudus != null ? ai.Where(g => g.LudusId == preferLudus).ToList() : ai;
        if (pool.Count == 0) pool = ai;
        return pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
    }

    /// <summary>플레이어 경기 후 확률적으로 이벤트 스폰(결정론 — 시드 파생). 대상=방금 싸운 내 선수.
    /// 스토리([13]) 마일스톤 이벤트가 랜덤 이벤트보다 우선한다.</summary>
    private void MaybeSpawnEvent(Gladiator? subject)
    {
        if (_pendingEventId != null) return;
        if (MaybeSpawnStoryEvent(afterMatch: subject is { IsPlayer: true })) return;
        if (subject == null || !subject.IsPlayer) return;
        var rng = new SimRandom(SeasonSeed ^ 0xE7E7_0A11UL + (ulong)_matchIdx * 131UL);
        if (!rng.Roll(0.22f)) return;                             // ~22% 발생
        var pool = EvtTemplates();
        var t = pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
        _pendingEventId = t.Id;
        _pendingEventFighter = t.NeedsFighter ? subject.Id : null;
    }

    /// <summary>템플릿 탐색 — 스토리([13]) 이벤트는 story_ 접두로 구분(별도 템플릿 풀).</summary>
    private EvtTemplate? FindTemplate(string id) =>
        (id.StartsWith("story_") ? StoryTemplates() : EvtTemplates()).FirstOrDefault(x => x.Id == id);

    private TextEventDoc? PendingEventDoc()
    {
        if (_pendingEventId == null) return null;
        var t = FindTemplate(_pendingEventId);
        if (t == null) return null;
        string nm = _pendingEventFighter != null ? (_cast.FirstOrDefault(g => g.Id == _pendingEventFighter)?.Name ?? "선수") : "";
        return new TextEventDoc(t.Id, t.Icon, t.Title, t.Body(nm), t.Choices.Select(c => c.Label).ToArray(),
            t.Kind, t.Kind == "letter" ? LetterSender(t.Id) : null);
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
        if (me == null) return Err("내 모리튜리가 아니다");
        int round = SeasonActive && _cursor < _schedule.Count ? _schedule[_cursor].Round : _rounds + 1;
        _schedule.Insert(_cursor, new SchedRec(round, me.Id, opp.Id, true, 0f, "proposal",
            _proposalExec ? "execution" : "normal"));   // 다음 경기로 삽입(전시 — 도전장이면 {skull}처형전)
        _story.Add((0, "proposal", _proposalExec
            ? $"{{skull}} 처형전 성사 — {me.Name} vs {opp.Name}. 둘 중 하나는 걸어 나오지 못할 수 있다"
            : $"{{speech}} 빅매치 성사 — {me.Name} vs {opp.Name}(도전장)"));
        _pendingProposalOpp = null; _proposalExec = false; SaveWorld();
        return StateJson();
    }

    /// <summary>이벤트 선택 적용 → 결과 문구. 대상 선수가 사라졌으면(방출 등) 이벤트 취소.</summary>
    public string ChooseEventJson(int choiceIdx)
    {
        var t = _pendingEventId == null ? null : FindTemplate(_pendingEventId);
        if (t == null) return Err("대기 중인 이벤트가 없다");
        if (choiceIdx < 0 || choiceIdx >= t.Choices.Length) return Err("그런 선택지가 없다");
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
        if (t.Kind == "letter")   // 발신 문서 = 열람한 서신을 보관함에 편철
            ArchiveLetter(LetterSender(t.Id), t.Title, t.Body(subj?.Name ?? MyFirst?.Name ?? ""));
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
        InitStoryNewWorld();                    // [13] 서막 개시(장례 S0) + 창세 전설 시드
        SaveWorld();
        if (_interactive) WriteSeasonJson();
    }

    // ── 캐스트/후보 생성 ──

    // 24인 풀 — worldSeed가 12인을 선발(커리어마다 다른 캐스트 = 변칙성)
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
        ("GLA_VARRO",   "바로",       "WPN_SWORD",      "PER_HONORABLE",   "TAC_DECISION"),
        ("GLA_ASHUR",   "아슈르",     "WPN_DUALBLADES", "PER_OPPORTUNIST", "TAC_GAMBLER"),
        ("GLA_THEO",    "테오콜레스", "WPN_GREATSWORD", "PER_ARROGANT",    "TAC_PRESSURE"),
        ("GLA_SEGOVAX", "세고박스",   "WPN_SPEAR",      "PER_WARY",        "TAC_COUNTER"),
        ("GLA_SAXA",    "삭사",       "WPN_DUALBLADES", "PER_RECKLESS",    "TAC_BRAWLER"),
        ("GLA_MIRA",    "미라",       "WPN_WHIP",       "PER_SHOWMAN",     "TAC_ZONER"),
        ("GLA_POLLUX",  "폴룩스",     "WPN_HAMMER",     "PER_CALM",        "TAC_DEFENDER"),
        ("GLA_CASTOR",  "카스토르",   "WPN_SHIELD",     "PER_WARY",        "TAC_DEFENDER"),
        ("GLA_PRISCUS", "프리스쿠스", "WPN_SWORD",      "PER_HONORABLE",   "TAC_HUNTER"),
        ("GLA_VERUS",   "베루스",     "WPN_AXE",        "PER_BOLD",        "TAC_BRAWLER"),
        ("GLA_FLAMMA",  "플람마",     "WPN_SHIELD",     "PER_SHOWMAN",     "TAC_PRESSURE"),
        ("GLA_ATTILIUS","아틸리우스", "WPN_SPEAR",      "PER_CALM",        "TAC_EVADER"),
    };

    private static readonly string[] RecruitNames =
    {
        "루푸스","펠릭스","카시우스","세베루스","티투스","옥타비우스","다리우스","발레리우스",
        "트라야누스","아우렐리우스","콤모두스","페르티낙스","알비누스","마크리누스","고르디아누스","필리푸스",
        "데키우스","갈루스","플라비우스","루키우스","퀸투스","세르비우스","아피우스","호라티우스",
    };

    // 라이벌 루두스 — AI 모리튜리가 소속된 경쟁 검투소(명성 순위표). 플레이어는 "PLAYER".
    private const string PlayerLudusId = "PLAYER";
    private string PlayerLudusName => "★ " + _ludusName;   // 라니스타 명명 반영
    // 6종 전부 활성(새 세계) — 7개 루두스 구도(대항전 8강의 뼈대). 구 세이브는 3곳 그대로 이어간다.
    // 개성(W10b): gold=재력(이적 큰손·내 스타를 노린다) / youth=육성(놓친 원석을 주워간다) / blood=잔혹(처형전·도발 서신)
    private static readonly (string Id, string Name, string Persona, string Motto)[] RivalLudiPool =
    {
        ("LUD_BATIATUS", "바티아투스 검투소",   "gold",  "돈이 곧 검이다"),
        ("LUD_SOLONIUS", "솔로니우스 양성소",   "youth", "원석은 우리가 먼저 본다"),
        ("LUD_CRASSUS",  "크라수스 투기장",     "blood", "피가 관중을 부른다"),
        ("LUD_GLABER",   "글라베르 원형경기장", "blood", "굴복시켜라"),
        ("LUD_COSSUTIUS","코수티우스 검투단",   "gold",  "명성은 사는 것"),
        ("LUD_OVIDIUS",  "오비디우스 양성소",   "youth", "내일의 챔피언은 오늘의 소년"),
    };
    private string LudusNameOf(string id) => id == PlayerLudusId ? PlayerLudusName
        : RivalLudiPool.FirstOrDefault(r => r.Id == id).Name ?? id;
    private static string PersonaOf(string id) => RivalLudiPool.FirstOrDefault(r => r.Id == id).Persona ?? "";
    /// <summary>라이벌 검투소의 라니스타(주인) — 검투소명과 짝지어진 고정 인물(세계관 실감·[10] 검투소 구체화).</summary>
    private static string LanistaOf(string id) => id switch
    {
        "LUD_BATIATUS" => "퀸투스 바티아투스", "LUD_SOLONIUS" => "마르쿠스 솔로니우스",
        "LUD_CRASSUS" => "리키니우스 크라수스", "LUD_GLABER" => "가이우스 글라베르",
        "LUD_COSSUTIUS" => "푸블리우스 코수티우스", "LUD_OVIDIUS" => "티투스 오비디우스",
        _ => "무명의 라니스타",
    };
    /// <summary>라이벌 검투소의 후원자 — 시드 파생, 내 후원자와 절대 겹치지 않게 오프셋.</summary>
    private string LudusPatronOf(string id)
    {
        int mine = (int)(_worldSeed % (ulong)PatronPool.Length);
        int h = StableHash(id);
        int idx = (mine + 1 + h % (PatronPool.Length - 1)) % PatronPool.Length;
        var pt = PatronPool[idx];
        return $"{pt.Name} · {pt.Rank}";
    }
    private static int StableHash(string s) { int h = 0; foreach (char c in s) h = h * 31 + c; return h & 0x7fffffff; }

    /// <summary>특성 1개 추가 부여 — 배타 축을 지키며 아직 없는 것 중에서.
    /// 결정론: 세계 시드 + 선수 id + 나이로 고정해 같은 세계는 늘 같은 특성을 준다([7]§6.1).</summary>
    private void GrantExtraTrait(Gladiator g, string why)
    {
        var owned = g.TraitIds.Where(TraitTable.Exists).Select(TraitTable.Get).ToList();
        var pool = TraitTable.All.Where(t => !g.TraitIds.Contains(t.Id)
                        && !(t.ExclAxis.Length > 0
                             && owned.Any(o => o.ExclAxis == t.ExclAxis && o.ExclPolarity == -t.ExclPolarity)))
                    .ToArray();
        if (pool.Length == 0) return;
        var trng = new SimRandom(_worldSeed ^ (ulong)StableHash(g.Id) ^ ((ulong)g.Age * 2654435761UL));
        var add = pool[Math.Min(pool.Length - 1, (int)(trng.NextFloat01() * pool.Length))];
        g.TraitIds = g.TraitIds.Append(add.Id).ToArray();
        if (g.IsPlayer)
            _story.Add((_rounds + 1, "trait", $"{{sprout}} {g.Name}({g.Age}세) — {why}: 「{add.Name}」을(를) 얻었다"));
    }

    // ── [18] 살아있는 검투소 명부 — 라니스타 인물·후원자 정치·시즌 동향 ──
    private static readonly (string Trait, string Quote)[] LanistaTraits =
    {
        ("냉혹한 계산가", "\"검투사는 자산이다. 감정은 장부에 적지 않지.\""),
        ("허영에 찬 귀족", "\"내 검투소의 이름값이 곧 로마의 취향이다.\""),
        ("피에 굶주린 흥행사", "\"관중은 피를 원해. 나는 그저 공급할 뿐.\""),
        ("노회한 노예상", "\"싸구려를 사서 챔피언으로 판다 — 그게 장사지.\""),
        ("몰락한 명문의 후예", "\"조상의 이름을 모래로 되사겠다.\""),
        ("신흥 벼락부자", "\"돈으로 못 사는 명예? 아직 값을 못 불렀을 뿐이지.\""),
    };
    private (string Trait, string Quote) LanistaCharOf(string id) => LanistaTraits[StableHash(id + "L") % LanistaTraits.Length];
    private string PatronEmperorRel(string id) => (StableHash(id + "P") % 4) switch
    {
        0 => "황제의 총신 — 궁정에서 목소리가 크다",
        1 => "원로원 강경파 — 검투 흥행을 정치에 쓴다",
        2 => "재정난에 빠진 가문 — 후원이 예전 같지 않다",
        _ => "야심가 — 이 검투소를 발판 삼는다",
    };
    private string PhilosophyOf(string persona) => persona switch
    {
        "gold" => "재력으로 완성된 별을 사들여 즉시 전력화한다",
        "youth" => "원석을 싸게 사 오래 다듬는다 — 인내가 곧 철학",
        "blood" => "처형전과 도발로 관중을 끓인다 — 피가 곧 흥행",
        _ => "균형 잡힌 운영",
    };

    private sealed record LudusDynamicRec(string Icon, string Title, string Desc, float RepDelta);
    /// <summary>이 시즌 검투소의 동향(결정론 — worldSeed·id·시즌 파생). 명성에 소폭 반영돼 순위가 '스스로' 출렁인다.</summary>
    private LudusDynamicRec LudusDynamic(string id, int season)
    {
        var pool = new (string Icon, string Title, string Desc, float Rep)[]
        {
            ("{coin}", "영입 공세", "노예 시장을 휩쓸며 별들을 쓸어 담는다 — 이번 시즌 야심이 크다", +8f),
            ("{chart}", "재정난", "금고가 얇아졌다. 급여 체불 소문에 검투사들이 동요한다", -7f),
            ("{star}", "간판의 각성", "간판 검투사가 물이 올랐다 — 검투소가 그 등에 올라탄다", +6f),
            ("{news}", "추문", "라니스타의 승부조작 소문이 포룸을 돈다 — 이름값이 깎인다", -8f),
            ("{crown}", "후원자의 영광", "후원자가 궁정에서 승진했다 — 뒷배가 든든해졌다", +5f),
            ("{dove}", "간판의 은퇴", "노장 간판이 목검을 받았다 — 세대교체의 진통", -4f),
            ("{sprout}", "원석 발굴", "숨은 원석을 값싸게 주웠다는 소문 — 미래가 밝다", +3f),
            ("{candle}", "평온한 시즌", "특별한 소식 없이 묵묵히 칼을 벼린다", 0f),
        };
        var pick = pool[StableHash(id + season) % pool.Length];
        return new LudusDynamicRec(pick.Icon, pick.Title, pick.Desc, pick.Rep);
    }
    /// <summary>개막 시 각 라이벌 검투소의 동향을 명성에 반영 + 신문 소식으로 — 살아있는 세계.</summary>
    private void ApplyLudusDynamics()
    {
        foreach (var r in ActiveRivalLudi)
        {
            var dyn = LudusDynamic(r.Id, _seasonNo);
            if (dyn.RepDelta != 0f) AddRivalRep(r.Id, dyn.RepDelta);
            if (dyn.Title != "평온한 시즌")
                _story.Add((0, "ludus", $"{dyn.Icon} {r.Name} — {dyn.Title}: {dyn.Desc}"));
        }
    }
    /// <summary>이 세계에 실존하는 라이벌 루두스(캐스트 소속 + 명성 기록 보유) — 풀 순서 유지.</summary>
    private IEnumerable<(string Id, string Name, string Persona, string Motto)> ActiveRivalLudi =>
        RivalLudiPool.Where(r => _rivalRep.ContainsKey(r.Id) || _cast.Any(g => g.LudusId == r.Id));

    private void CreateAiCast()
    {
        var rng = new SimRandom(_worldSeed ^ 0xCA57_CA57UL);
        var picks = AiCastDef.OrderBy(_ => rng.NextUInt64()).Take(12).ToList();         // 24인 풀 → 12인
        var ludi = RivalLudiPool.OrderBy(_ => rng.NextUInt64()).ToList();               // 6곳 전부 활성(대항전 구도)
        int i = 0;
        foreach (var (id, name, wpn, per, sig) in picks)
        {
            var g = RollGladiator(rng, id, name, wpn, per, sigTactic: sig, isPlayer: false,
                                  ageMin: 20, ageMax: 28);
            g.LudusId = ludi[i / 2 % ludi.Count].Id;   // 2명씩 6개 라이벌 루두스로 편성
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
        // 선천 스킬([7] 개정 — 라니스타 결정): 수련이 아니라 타고난다. 슬롯 상한 없음.
        // 자격 있는 스킬마다 독립 추첨 → 같은 등급이어도 0개인 자와 여럿 지닌 자가 갈린다.
        // 사생아 특성은 Ⅱ급 천부 천장을 무시한다([6]§1.5 계급 천장 예외).
        var skills = SkillGen.Roll(rng, wpn, per, end.Talent, traits.Contains(TraitTable.Bastard));
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
            TraitIds = traits, SkillIds = skills, IsPlayer = isPlayer,
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
            _story.Add((_rounds + 1, "relegate", $"↓ 강등 — {down.Name}({down.W}승 {down.L}패) → {DivName(2)}"));
            _story.Add((_rounds + 1, "promote", $"↑ 승격 — {up.Name}({up.W}승 {up.L}패) → {DivName(1)}"));
            if (up.IsPlayer) { AddGlory(GloryPromote); _promotedFlag = true; }   // 승격 = 위신 + [13] 종막 게이트
            // 재기 아크(C2): 실패를 리셋 충동이 아니라 새 목표로 — 강등=서약, 복귀=씻어낸 굴욕
            if (up.IsPlayer && _redemption)
            {
                _redemption = false; AddGlory(10f);
                _story.Add((_rounds + 1, "redemption", $"{{flame}} 재기 완수 — {up.Name}, 투기장에서 콜로세움으로. 굴욕을 씻었다 ({{glory}}+10)"));
            }
            if (down.IsPlayer && !_redemption)
            {
                _redemption = true;
                _story.Add((_rounds + 1, "redemption", "{flame} 재기의 서약 — 굴욕의 밤. 1부로 돌아가는 날, 영광이 두 배가 된다 (복귀 시 {glory}+10)"));
            }
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

    /// <summary>정규전 편성 — 부 내 전원 라운드로빈(서클 메서드): 모두가 모두와 딱 한 번, 공정한 순위·승강의 명분.
    /// 라이벌 재대결·흥행 서사는 이벤트 빅매치·처형전 도전장·예고가 짊어진다. 결정론(시즌시드 파생).</summary>
    private void BuildDivisionRoundRobin(int div, List<SchedRec> cards)
    {
        var pool = _cast.Where(g => g.Division == div).ToList();
        if (pool.Count < 2) return;
        var rng = new SimRandom(SeasonSeed ^ (0xCA5D_0000UL + (ulong)div));
        var arr = pool.OrderBy(_ => rng.NextUInt64()).Select(g => (string?)g.Id).ToList();   // 시즌별 결정론 셔플
        if (arr.Count % 2 == 1) arr.Add(null);   // 홀수 인원 → 라운드마다 한 명 부전(휴식)
        int n = arr.Count;
        for (int r = 1; r < n; r++)
        {
            for (int i = 0; i < n / 2; i++)
            {
                string? a = arr[i], b = arr[n - 1 - i];
                if (a == null || b == null) continue;
                cards.Add(new SchedRec(r, a, b, false, 0f));
            }
            arr.Insert(1, arr[^1]); arr.RemoveAt(arr.Count - 1);   // 서클 회전(0번 고정)
        }
    }

    private void StartSeason()
    {
        _seasonNo = _seasonsPlayed + 1;
        _matchIdx = 0; _emoGen = 0; _cursor = 0; _eventsAppended = false;
        _cupStage = 0; _cupSeeds = new(); _cupChampion = null; _seasonNewAch.Clear(); _oddsCursor = -1;
        _seasonBetNet = 0f; _gauntletStage = 0; _gauntletWins = 0; _tbWinnerId = null;
        _festStage = 0; _festSlots = new(); _festRepId = null; _festChampion = null;   // {masks} 대항전 리셋(대표는 시즌마다 지명)
        _promotedFlag = false; _legendRefs = 0;   // [13] 종막 게이트·카토 전설 참조(시즌 2회) 리셋
        _preWeek = 0;   // [19] 프리시즌 준비 주간 리셋(개막 시)
        // 관전 아카이브(#1): 직전 시즌 경기를 시즌 태그와 함께 영속 보관(재관전용). 최근 400경기로 롤링(파일 비대 방지)
        foreach (var e in _matchLog) _archive.Add(new ArchRec(Math.Max(1, _seasonsPlayed), e));
        while (_archive.Count > 400) _archive.RemoveAt(0);
        _story.Clear(); _eventDocs.Clear(); _schedule.Clear(); _matchLog.Clear();
        SeasonActive = true;
        foreach (var g in _cast) { g.W = g.L = g.D = g.Streak = 0; g.PendingEmotions.Clear(); g.Fatigue = 0; g.InjuryMatches = 0; g.SeasonBrutals = 0; }   // 시즌 사이 휴식 = 완전 회복
        if (_seasonNo == 1) AssignDivisions();   // 초기 배치만 명성 — 이후 승강은 시즌말 성적 스왑
        else RebalanceDivisions();               // 영입·은퇴로 어긋난 인원만 보정

        // 라이벌 간 이적(W10b v2): 재력 루두스가 개막 전 타 루두스의 간판을 사들인다 — 세계가 스스로 움직인다
        var trRng = new SimRandom(SeasonSeed ^ 0x7124_A17BUL);
        var goldLudi = ActiveRivalLudi.Where(r => r.Persona == "gold").ToList();
        if (_seasonNo > 1 && goldLudi.Count > 0 && trRng.Roll(0.35f))
        {
            var buyer = goldLudi[(int)(trRng.NextUInt64() % (ulong)goldLudi.Count)];
            var target = _cast.Where(g => !g.IsPlayer && g.LudusId != buyer.Id && g.Fame >= 15f)
                              .OrderByDescending(g => g.Fame).FirstOrDefault();
            if (target != null)
            {
                string from = LudusNameOf(target.LudusId);
                target.LudusId = buyer.Id;
                AddRivalRep(buyer.Id, 6f);
                _story.Add((0, "transfer", $"{{handshake}} 라이벌 이적 — {buyer.Name}, {from}의 간판 {target.Name}을(를) 사들였다 (\"{buyer.Motto}\")"));
            }
        }

        if (_seasonNo > 1) ApplyLudusDynamics();   // [18] 살아있는 검투소 — 시즌 동향을 명성에 반영·신문에 실음

        // 정규전: 부 내 전원 라운드로빈 — 두 부를 라운드 순으로 통합 정렬(달력·페이즈 흐름 일치)
        var cards = new List<SchedRec>();
        BuildDivisionRoundRobin(1, cards);
        BuildDivisionRoundRobin(2, cards);
        _schedule.AddRange(cards.OrderBy(c => c.Round));
        _rounds = _schedule.Count > 0 ? _schedule.Max(s => s.Round) : 1;
        int d1 = _cast.Count(g => g.Division == 1);
        _story.Add((0, "season", $"{{ludus}} 시즌 {_seasonNo} 개막 — {DivName(1)} {d1}인 · {DivName(2)} {_cast.Count - d1}인"));

        RollEdict();   // 황제의 특명(시즌 계약)

        // 빅매치 제안(라니스타 개입): 원수의 처형전 도전장(우선) 또는 명망 도전자와의 전시 카드.
        _pendingProposalOpp = null; _proposalExec = false;
        if (!_playerless && _cast.Count(g => g.IsPlayer) >= 2)
        {
            var pRng = new SimRandom(SeasonSeed ^ 0x0B16_A7C4UL);
            // {skull} 원수의 도전장: 내 선수를 '원수'로 여기는 AI가 있으면 50%로 처형전을 걸어온다 (관계 발화)
            var nemesis = _cast.Where(ai => !ai.IsPlayer && _cast.Any(my => my.IsPlayer &&
                _ledger.Get(ai.Id, my.Id).Classify(ai.PersonalityId) == RelationType.Nemesis)).FirstOrDefault();
            // 잔혹 개성(W10b): 피를 원하는 루두스 소속 원수는 처형전을 더 자주 걸어온다
            if (nemesis != null && pRng.Roll(PersonaOf(nemesis.LudusId) == "blood" ? 0.75f : 0.5f))
            {
                _pendingProposalOpp = nemesis.Id; _proposalExec = true;
                _story.Add((0, "proposal", $"{{skull}} 도전장 — 원수 {nemesis.Name}이(가) 처형전을 요구한다!"));
            }
            else if (pRng.Roll(0.6f))
                _pendingProposalOpp = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault()?.Id;
        }

        TeaseNext(0);   // {horn} 개막 예고 — 첫 상대 공개
    }

    /// <summary>{horn} 예고 시스템 — 다음 '내 경기'를 관계·경력·대진 종류로 분류해 기대감 한 줄을 스토리에 쏜다.
    /// 내 경기 정산 직후·개막 시 1회씩 — 이번 경기의 보상보다 다음 경기의 기대가 플레이를 끈다. 메커니즘 무영향(연출 전용).</summary>
    private void TeaseNext(int round)
    {
        if (_playerless) return;
        SchedRec? next = null;
        for (int i = _cursor; i < _schedule.Count; i++)
        {
            var c = _schedule[i];
            if (ById(c.A).IsPlayer || ById(c.B).IsPlayer) { next = c; break; }
        }
        if (next == null) return;
        var A = ById(next.A); var B = ById(next.B);
        if (A.IsPlayer && B.IsPlayer)
        { _story.Add((round, "tease", $"{{horn}} 예고 — 한 지붕 아래의 검이 서로를 겨눈다: {A.Name} vs {B.Name} (내전)")); return; }
        var mine = A.IsPlayer ? A : B; var opp = A.IsPlayer ? B : A;
        var rel = _ledger.Get(mine.Id, opp.Id);
        var type = rel.Classify(mine.PersonalityId);
        string wpn = opp.WeaponId.Replace("WPN_", "") switch
        {
            "SWORD" => "검", "SPEAR" => "창", "AXE" => "도끼", "GREATSWORD" => "대검",
            "DUALBLADES" => "쌍검", "HAMMER" => "망치", "WHIP" => "채찍", "SHIELD" => "방패", _ => "무기",
        };
        string text =
            next.Kind == "fest_final" ? $"{{horn}} 예고 — 축제의 왕관이 한 경기 앞: {mine.Name}, 결승에서 {opp.Name}과(와) 맞선다" :
            next.Kind.StartsWith("fest_") ? $"{{horn}} 예고 — 사투르날리아의 모래 위, {mine.Name}이(가) 루두스의 명예를 걸고 {opp.Name}을(를) 만난다" :
            next.Kind == "cup_final" ? $"{{horn}} 예고 — 챔피언십 컵 결승! {opp.Name}만 넘으면 왕관이다" :
            next.Kind == "cup_sf" ? $"{{horn}} 예고 — 컵 4강 대진 공개: {opp.Name}. 관중석이 벌써 뜨겁다" :
            next.Format == "execution" ? $"{{skull}} 예고 — 처형전이 잡혔다. {opp.Name}… 패자는 모래를 떠나지 못할 수도 있다" :
            type == RelationType.Nemesis ? $"{{skull}} 예고 — 원수 {opp.Name}이(가) 기다린다. 관중이 피의 재대결을 외친다" :
            type == RelationType.Rival ? $"{{flame}} 예고 — 라이벌 {opp.Name}과(와)의 재대결이 공개됐다. 도시가 술렁인다" :
            rel.Losses > rel.Wins ? $"{{swords}} 예고 — 다음 상대는 {opp.Name} (상대전적 {rel.Wins}승 {rel.Losses}패). 갚아야 할 빚이 있다" :
            opp.CW + opp.CL + opp.CD == 0 ? $"{{sprout}} 예고 — 신예 {opp.Name}이(가) {mine.Name}에게 공개 도전장을 보냈다" :
            opp.Fame >= 40f ? $"{{crown}} 예고 — {wpn}의 명수 {opp.Name}과(와)의 빅카드. 황제의 사자가 경기장을 찾는다는 소문이 돈다" :
            $"{{horn}} 예고 — 다음 상대 공개: {wpn}을(를) 쓰는 {opp.Name} ({LudusNameOf(opp.LudusId)})";
        _story.Add((round, "tease", text));
    }

    private void FinalizeSeason()
    {
        SeasonActive = false;
        _seasonsPlayed = _seasonNo;
        // 승부조작 미이행(가담 선수가 시즌 내 더 안 싸움): 뒷돈 주인이 배신으로 간주 — 협박 채무·명성 압박
        if (_fixFighterId != null)
        {
            var fixName = _cast.FirstOrDefault(g => g.Id == _fixFighterId)?.Name ?? "그 모리튜리";
            _ludusRep = MathF.Max(0f, _ludusRep - 25f); DebtTxn("검은 인장의 협박 채무", _fixReward);
            _story.Add((_rounds + 1, "fix", $"{{dice}} 미이행 — {fixName}이(가) 끝내 경기를 던지지 않았다. 뒷돈의 주인이 배신으로 여긴다 (명성 −25·협박 채무 +{_fixReward:F0})"));
            _fixFighterId = null; _fixReward = 0f;
        }
        var standings = Standings(1);                       // 리그 챔피언 = 1부 우승자
        var champ = standings[0];
        var d2 = Standings(2);
        _story.Add((_rounds + 1, "season", $"{{trophy}} 시즌 {_seasonNo} 종료 — {DivName(1)} 챔피언 {champ.Name} ({champ.W}승 {champ.L}패)"));
        if (d2.Count > 0) _story.Add((_rounds + 1, "season", $"{{trophy}} {DivName(2)} 우승 — {d2[0].Name}"));

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
            _story.Add((_rounds + 1, "season", $"{{coin}} 시즌 정산 — 순위 보너스 +{bonusPaid:F0} · 급여·유지비 −{salaryPaid:F0}{(upkeep > 0 ? $"(시설 {upkeep:F0})" : "")} (잔고 {_gold:F0})"));

            // 후원자 정산(#7): 높은 관계 = 시즌말 하사금, 낮은 관계 = 압박(명성 삭감). 관계는 매 시즌 중앙으로 감쇠.
            if (_patronage >= 40f) { float gift = MathF.Round(_patronage * 2f); _gold += gift; _story.Add((_rounds + 1, "patron", $"{{coin}} 후원자의 하사 — 관계 {_patronage:F0} → 금화 +{gift:F0} (\"올해도 즐거웠네.\")")); }
            else if (_patronage <= -40f) { float pen = MathF.Round(-_patronage * 0.5f); _ludusRep = MathF.Max(0f, _ludusRep - pen); _story.Add((_rounds + 1, "patron", $"{{sword}} 후원자의 냉대 — 관계 {_patronage:F0} → 루두스 명성 −{pen:F0} (뒷말이 돈다)")); }
            _patronage *= 0.6f;

            // 사채 정산: 이자 20% → 잔고에서 자동 상환 → 남으면 채권자의 압박(루두스 명성 −10)
            if (_debt > 0f)
            {
                DebtTxn("채권자의 이자 (20%)", _debt * 0.2f);
                float pay = MathF.Min(_gold, _debt); _gold -= pay;
                if (pay > 0f) DebtTxn("시즌말 자동 상환", -pay);
                if (_debt > 0.5f)
                {
                    _ludusRep = MathF.Max(0f, _ludusRep - 10f);
                    _story.Add((_rounds + 1, "debt", $"{{coin}} 채권자의 압박 — 이자 20% · 상환 {pay:F0} · 잔여 채무 {_debt:F0} · 루두스 명성 −10"));
                }
                else _story.Add((_rounds + 1, "debt", $"{{coin}} 빚 청산 — {pay:F0} 상환, 채무에서 벗어났다"));
            }
        }

        // 나이/노화: 시즌당 +1세, 노화 시작 후 잠재력 상한 점진 감소 (의무실은 내 선수만 감면)
        var agingNotes = new List<string>();
        foreach (var g in _cast)
        {
            g.Age++;
            // 선천 특성 추가 부여 2종([7]§6.1·§6.2) — 둘 다 같은 규칙(배타 축 준수·미보유 중 추첨).
            //  · 20세 달성: 1개 추가 (성장 서사 — [7]§6.1, 나이 시스템이 생겨 이제 구현 가능)
            //  · 노련함 보유자: 10년마다 1개 추가 (세월이 쌓아 올린 지혜)
            if (g.Age == 20) GrantExtraTrait(g, "스무 살 — 몸이 제 결을 찾았다");
            if (g.TraitIds.Contains(TraitTable.Veteran) && g.Age % 10 == 0) GrantExtraTrait(g, "노련함이 값을 한다");
            if (g.Age >= g.AgingStartAge)
            {
                float relief = g.IsPlayer ? 0.25f * (_medicalLv - 1) : 0f;
                if (g.TraitIds.Contains(TraitTable.SlowAge)) relief = Math.Min(0.9f, relief + 0.4f);   // 저속노화(#16): 감소폭 −40%p
                if (g.TraitIds.Contains(TraitTable.Veteran)) relief = Math.Min(0.9f, relief + 0.25f);  // 노련함([7]§6.2): 노화 완화
                g.PotentialBudget = MathF.Max(MinPotentialBudget, g.PotentialBudget - AgingDecayPerSeason * (1f - relief));
                float excess = BudgetUsed(g.Stats) - g.PotentialBudget;
                if (excess > 0f)
                {
                    // 상한 아래로 — 현재 스탯도 깎인다. RCT 가중 50%([3]6.3 노화는 반응속도부터) + 나머지 균등.
                    g.Stats = WithAxis(g.Stats, 5, -excess * 0.5f);
                    for (int a = 0; a < 5; a++) g.Stats = WithAxis(g.Stats, a, -excess * 0.1f);
                    if (g.IsPlayer)
                    {
                        _story.Add((_rounds + 1, "aging", $"{{hourglass}} {g.Name}({g.Age}세) — 세월이 몸을 갉아먹는다 (상한 {g.PotentialBudget:F0})"));
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
        // 불멸의 루두스(엔드게임 그랜드슬램): 명전 5인 배출 + 컵 3회 + 카이사르 + 최고 등급
        if (_hall.Count(h => h.IsPlayer) >= 5 && _myCupTitles >= 3
            && _achievements.Contains("caesar") && _achievements.Contains("empire")) Unlock("immortal_ludus");
        // 무결점 시즌: 내 모리튜리 전원이 정규 시즌 무패(최소 1경기 이상)
        var myFighters = _cast.Where(g => g.IsPlayer).ToList();
        if (myFighters.Count > 0 && myFighters.All(g => g.L == 0 && g.W + g.D > 0)) Unlock("perfect");

        // 특명 미달성 = 황제의 실망(루두스 명성 하락)
        if (_edict != null && !_edictDone)
        {
            _ludusRep = MathF.Max(0f, _ludusRep - EdictFailRep);
            _favor = Math.Max(0, _favor - 1);   // 총애도 식는다
            _story.Add((_rounds + 1, "edict", $"{{scroll}} 특명 실패 — \"{_edict.Desc}\" · 황제의 실망 (루두스 명성 −{EdictFailRep:F0})"));
        }
        _edict = null; _edictDone = false;

        SwapDivisions();   // 승강(성적 기반) — 다음 시즌 배치 확정. 챔피언은 1부 1위라 강등 불가

        // 콜로세움 월보 박제(#1) — 시즌이 넘어가면 이번 시즌 호(號)들을 아카이브에 영속(로그 초기화 전에)
        _pressArchive.InsertRange(0, BuildSeasonIssues(_seasonNo));
        if (_pressArchive.Count > 40) _pressArchive.RemoveRange(40, _pressArchive.Count - 40);

        TickUnrest();        // [13] 살아있는 세계 — 반란 지수 시즌 틱(사이클, 결정론)
        PromoteLegends();    // [13] 명전 → 전설 승격(시즌 1명)
        CheckStoryFinale();  // [13] 종막 판정 — 승격 or 시즌 3 소프트 종료 → 라니스타가 되는 의식

        // AI 세대교체: 노화 6시즌 경과(36~42세) 또는 상한 바닥 → 은퇴(명예의 전당) → 신인 AI 데뷔 (리그 영속성).
        // 내 선수는 은퇴 없음 — 방출은 라니스타 권한(약해진 채 데리고 있을 자유).
        var retirements = new List<string>();
        var rookieRng = new SimRandom(_worldSeed ^ 0xA1A1_A1A1UL + (ulong)_seasonNo * 97UL);
        // 신인 파동(변칙성): 시즌마다 원석 품질이 출렁인다 — 풍년(20%)=천부 2롤, 평년=1롤
        bool rookieBoom = rookieRng.Roll(0.20f);
        if (rookieBoom) _story.Add((_rounds + 1, "season", "{sprout} 신인 풍년 — 이번 세대엔 유망한 원석이 많다"));
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
                _hall.Add(MakeHall(old, $"{old.CW}-{old.CL}-{old.CD}", _seasonNo));
            var rookie = SpawnRookie(old.LudusId, old.Division);
            string note = aged
                ? $"{old.Name}({old.Age}세, 명성 {old.Fame:F0}) 은퇴 → 신인 {rookie.Name} 데뷔"
                : $"{old.Name}({old.CW}승 {old.CL}패) 방출 → 신인 {rookie.Name} 데뷔";
            retirements.Add(note);
            _story.Add((_rounds + 1, aged ? "retire" : "release", (aged ? "{ludus} " : "{thumbdown} ") + note + (aged ? " — 명예의 전당 등재" : "")));
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
            Favor: _favor, GauntletWins: _gauntletWins,
            FestChampion: _festChampion,
            FestChampionMine: _festChampion != null && _cast.Any(g => g.IsPlayer && g.Name == _festChampion),
            Awards: BuildSeasonAwards());

        SaveWorld();
    }

    /// <summary>{ludus} 시즌 시상식 — MVP(승수→KO)·최다 KO·신인왕(데뷔 시즌 최다 승)·인기왕. 매치로그·시즌 스탯에서 산출.</summary>
    private List<string>? BuildSeasonAwards()
    {
        if (_cast.Count == 0 || _matchLog.Count == 0) return null;
        var awards = new List<string>();
        var kos = new Dictionary<string, int>();   // 이름 → 시즌 KO승(매치로그)
        foreach (var m in _matchLog.Where(m => m.Reason == "KO" && m.Winner != "무승부"))
            kos[m.Winner] = kos.GetValueOrDefault(m.Winner) + 1;

        var mvp = _cast.OrderByDescending(g => g.W).ThenByDescending(g => kos.GetValueOrDefault(g.Name)).First();
        if (mvp.W > 0) awards.Add($"{{swords}} 시즌 MVP — {mvp.Name} ({mvp.W}승)");
        if (kos.Count > 0)
        {
            var koKing = kos.OrderByDescending(kv => kv.Value).First();
            if (koKing.Value >= 2) awards.Add($"{{impact}} 최다 KO — {koKing.Key} ({koKing.Value}회)");
        }
        // 신인왕: 통산 경기 = 이번 시즌 출장수(매치로그)인 데뷔 시즌 선수 중 최다 승 — MVP와 겹치면 생략
        int SeasonGames(string name) => _matchLog.Count(m => m.AName == name || m.BName == name);
        var rookie = _cast.Where(g => g.CW + g.CL + g.CD > 0 && g.CW + g.CL + g.CD == SeasonGames(g.Name))
                          .OrderByDescending(g => g.W).ThenByDescending(g => kos.GetValueOrDefault(g.Name)).FirstOrDefault();
        if (rookie != null && rookie.W > 0 && rookie != mvp) awards.Add($"{{sprout}} 신인왕 — {rookie.Name} ({rookie.W}승 데뷔)");
        var star = _cast.OrderByDescending(g => g.Popularity).First();
        if (star.Popularity >= 10f) awards.Add($"{{fest}} 군중의 연인 — {star.Name} (인기 {star.Popularity:F0})");
        return awards.Count > 0 ? awards : null;
    }

    /// <summary>시즌 중 선수 제거 시 잔여 일정에서 그 선수 경기를 뺀다(#3 — 은퇴/방출 중도 허용).</summary>
    private void PurgeRemainingMatches(string fid)
    {
        if (!SeasonActive) return;
        for (int i = _schedule.Count - 1; i >= _cursor; i--)
            if (_schedule[i].A == fid || _schedule[i].B == fid) _schedule.RemoveAt(i);
    }

    /// <summary>방출(#3 시즌 중에도 가능): 관계 청산 + 잔여 일정 정리.</summary>
    /// <summary>방출 — 비정한 계약 해지. 은퇴와 달리 리그에서 사라지지 않는다: 라이벌 검투소(육성가 우선)가
    /// 주워가 리그에 잔류하고, 옛 루두스의 간판에게 원한을 새긴다 + 루두스 명성 소폭 하락(비정한 처사).</summary>
    public string ReleaseJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        var rng = new SimRandom(_worldSeed ^ 0x8E1E_A5EDUL + (ulong)(g.CW * 7 + g.CL * 3 + _seasonNo));
        var pool = ActiveRivalLudi.ToList();
        var youth = pool.Where(r => r.Persona == "youth").ToList();
        var picks = youth.Count > 0 && rng.Roll(0.6f) ? youth : pool;   // 육성가 검투소가 원석을 먼저 줍는다(60%)
        var dest = picks[(int)(rng.NextUInt64() % (ulong)picks.Count)];
        g.IsPlayer = false;
        g.LudusId = dest.Id;
        var star = _cast.Where(x => x.IsPlayer && x.Id != g.Id).OrderByDescending(x => x.Fame).FirstOrDefault();
        if (star != null) _ledger.DeepenGrudge(g.Id, star.Id, 25f);   // 버림받은 자의 원한 — 모래 위에서 갚는다
        _ludusRep = MathF.Max(0f, _ludusRep - 5f);
        _story.Add((0, "release", $"{{thumbdown}} 방출 — {g.Name}, 짐을 싸기도 전에 {dest.Name}이(가) 주워갔다. 그는 잊지 않을 것이다 (루두스 명성 −5)"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    private int _sparCount;   // 스파링 시드 카운터(영속 — 결정론)

    // ── 콜로세움 도박장 — AI 경기에 골드 베팅(내 경기 금지=승부조작 방지). 배당은 베팅 시점 고정 ──
    private int _betCursor = -1, _betSide; private float _betAmount, _betOdds;
    private sealed record BetLogRec(int Season, string On, float Amount, float Odds, bool Won, float Payout);
    private readonly List<BetLogRec> _betLog = new();   // 베팅 이력(최근 60, 영속)

    /// <summary>확률 p → 배당(decimal). odds = 1 + 마진×(1−p)/p — 순이익에 하우스 마진(10%) 부과.
    /// 상한 없음(희귀 조합=진짜 고배당). 항상 &gt;1(적중 시 반드시 이득), 확률 다르면 배당도 다르다(동일 배당 방지).
    /// p 하한은 0으로 나눔 방지 안전장치(0.004)만 — 실제 스무딩된 p는 훨씬 크다.</summary>
    private static float BetOdds(float p) => 1f + 0.9f * (1f - MathF.Min(p, 0.999f)) / MathF.Max(p, 0.004f);

    /// <summary>베팅 종류 라벨: 0=A승 1=B승 2=A KO승 3=A 판정승 4=B KO승 5=B 판정승.</summary>
    private static string BetLabel(int side, Gladiator A, Gladiator B) => side switch
    {
        0 => $"{A.Name} 승", 1 => $"{B.Name} 승",
        2 => $"{A.Name} KO승", 3 => $"{A.Name} 판정승",
        4 => $"{B.Name} KO승", 5 => $"{B.Name} 판정승", _ => "?",
    };

    /// <summary>다음 AI 경기에 베팅: side 0=A승 1=B승 2=A KO승 3=A판정승 4=B KO승 5=B판정승 — 누가 어떻게 이길지까지.
    /// 경기당 1회, 배당 고정. 연속 적중 3회부터 배당 +10%(스트릭 보너스).</summary>
    public string BetJson(int side, float amount)
    {
        if (!SeasonActive || _cursor >= _schedule.Count) return Err("다음 경기가 없다");
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        if (A.IsPlayer || B.IsPlayer) return Err("내 루두스 경기엔 걸 수 없다 (승부조작 금지)");
        if (_betCursor == _cursor) return Err("이미 이 경기에 걸었다");
        if (side is < 0 or > 5) return Err("그런 선택지가 없다");
        amount = MathF.Min(MathF.Floor(amount), _gold);
        if (_gold - amount < 1f) amount = _gold;   // 전액 베팅: 잔돈(1 미만) 남기지 않고 전부 건다
        if (amount < 5) return Err("최소 5 데나리우스 (잔고 부족)");
        float odds = BetOddsFor(side);   // 승자×방식 조합 확률에서 산정(상성 반영)
        if (_betStreak >= 2) odds = MathF.Round(odds * 1.10f * 100f) / 100f;   // {flame} 스트릭 보너스
        _gold -= amount; _seasonBetNet -= amount;
        _betCursor = _cursor; _betSide = side; _betAmount = amount; _betOdds = odds;
        _story.Add((s.Round, "bet", $"{{dice}} 베팅 — {BetLabel(side, A, B)}에 {amount:F0} (배당 {odds:F2}{(_betStreak >= 2 ? " · 스트릭 보너스" : "")})"));
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
        if (SeasonActive) return Err("이적은 프리시즌에만 열린다");
        var rng = new SimRandom(_worldSeed ^ 0x7124_5FE2UL + (ulong)_seasonsPlayed * 41UL);
        var pool = _cast.Where(g => !g.IsPlayer).OrderBy(_ => rng.NextUInt64()).Take(3)
            .Select(g => new TransferBuyDoc(g.Id, g.Name, g.WeaponId.Replace("WPN_", ""), g.PersonalityId.Replace("PER_", ""),
                g.Age, MathF.Round(g.Fame), g.Division, LudusNameOf(g.LudusId), (int)TransferPrice(g))).ToList();

        TransferSellDoc? sell = null;
        var star = _cast.Where(g => g.IsPlayer).OrderByDescending(g => g.Fame).FirstOrDefault();
        if (star != null && star.Fame >= 20f && rng.Roll(0.6f))
        {
            // 재력 개성(W10b): 큰손 루두스가 내 스타를 더 자주 노리고, 더 비싸게 부른다(×1.35)
            var all = ActiveRivalLudi.ToList();
            var golds = all.Where(b => b.Persona == "gold").ToList();
            var buyer = (golds.Count > 0 && rng.Roll(0.7f) ? golds : all).OrderBy(_ => rng.NextUInt64()).First();
            float mult = buyer.Persona == "gold" ? 1.35f : 1.2f;
            sell = new TransferSellDoc(star.Id, star.Name, buyer.Name, (int)(TransferPrice(star) * mult));
        }
        return JsonSerializer.Serialize(new { ok = true, Buyables = pool, SellOffer = sell }, JsonOpts);
    }

    /// <summary>AI 선수 인수: 골드 지불 → 내 로스터로. 판 검투소는 신인으로 공석 승계.</summary>
    public string TransferBuyJson(string id)
    {
        if (SeasonActive) return Err("이적은 프리시즌에만 열린다");
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
        _story.Add((0, "transfer", $"{{handshake}} 이적 — {g.Name}, {LudusNameOf(oldLudus)}에서 우리 루두스로 (이적료 {price}) · 공석엔 신인 {rk.Name}"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>인수 제안 수락: 내 스타를 라이벌 루두스에 판다 — 골드는 크지만 전력을 잃는다.</summary>
    public string TransferSellJson(string id)
    {
        if (SeasonActive) return Err("이적은 프리시즌에만 열린다");
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
        _story.Add((0, "transfer", $"{{coin}} 이적 — {g.Name}, {buyerName}(으)로 (이적료 +{offer}). 잘 가라, 모리튜리여"));
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
        _story.Add((0, "edict", $"{{scroll}} 황제의 특명 — {_edict.Desc} (달성: {{glory}}{EdictGlory:F0}·{{coin}}{EdictGold:F0} / 실패: 명성 −{EdictFailRep:F0})"));
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
        _story.Add((0, "edict", $"{{scroll}} 특명 달성! — {_edict.Desc} ({{glory}}+{EdictGlory:F0} {{coin}}+{EdictGold:F0})"));
        // 황제의 총애: 특명을 거듭 완수하면 눈에 든다 — 단계 도달 시 1회성 하사품
        _favor++;
        (int Need, float Glory, string Title)[] tiers = { (3, 10f, "황제의 눈에 들다"), (6, 20f, "황제의 총신"), (10, 40f, "콜로세움의 총아") };
        for (int i = _favorLv; i < tiers.Length; i++)
            if (_favor >= tiers[i].Need)
            {
                _favorLv = i + 1; AddGlory(tiers[i].Glory);
                _story.Add((0, "favor", $"{{crown}} {tiers[i].Title} — 총애 {_favor} ({{glory}}+{tiers[i].Glory:F0})"));
            }
    }
    private int _favor, _favorLv;   // 황제의 총애(특명 달성 누적)·도달한 단계
    private int _streetSeq;         // 거리 시비 시드 카운터(영속)
    private int _surgerySeq;        // 의무실 수술 시드 카운터(영속)
    private bool _redemption;       // 재기의 서약(C2) — 강등의 굴욕, 승격 복귀로 씻는다
    private int _myCupTitles;       // 내 컵 우승 누계(엔드게임 업적)
    private string? _prepKind, _prepId;   // 경기 전 방침(C1) — 이번 경기 한정, 시뮬 무영향(메타만)

    // 의무실 수술 튜닝 — 일시(가벼운 처치)와 영구(대수술)를 확률·비용·페널티로 이원화.
    private const float TempSurgeryCost = 40f, PermSurgeryCost = 220f;

    /// <summary>일시 부상 완치 확률 — 가벼운 처치라 높다(의무실·회복 마스터리로 거의 확정까지). 프론트 표기와 동기화.</summary>
    private float TempHealChance(Gladiator g) => Math.Clamp(0.72f + 0.08f * (_medicalLv - 1) + 0.04f * g.MRecover, 0.72f, 0.94f);
    /// <summary>영구 부상 복원 확률 — 대수술이라 낮다(끽해야 절반 남짓). 실패는 돈만, 악화는 상한 영구 삭감.</summary>
    private float PermHealChance(Gladiator g) => Math.Clamp(0.30f + 0.08f * (_medicalLv - 1) + 0.05f * g.MRecover, 0.30f, 0.60f);

    /// <summary>의무실 수술(도박): "내가 굴리는 주사위". kind=perm이면 영구 부상 복원 대수술(고비용·저확률·악화 위험),
    /// 그 외엔 일시 부상 처치(저비용·고확률·가벼운 실패). 의무실 Lv·회복 마스터리가 확률을 올린다. 시드 결정론.</summary>
    public string SurgeryJson(string fighterId, string kind = "temp", string part = "")
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        var rng = new SimRandom(_worldSeed ^ 0x5069_CA1FUL + (ulong)(++_surgerySeq) * 53UL);
        float roll = rng.NextFloat01();
        bool healed = false; string outcome;

        if (kind == "perm")
        {
            if (g.PermInjuries.Count == 0) return Err("영구 부상이 아니다");
            if (_gold < PermSurgeryCost) return Err($"잔고 부족 (대수술비 {PermSurgeryCost:F0})");
            _gold -= PermSurgeryCost;
            string chosen = g.PermInjuries.Contains(part) ? part : g.PermInjuries[0];   // 부위 선택(없으면 가장 오래된 것)
            var k = PermInjuryKinds.FirstOrDefault(x => x.Id == chosen);
            float heal = PermHealChance(g);
            if (roll < heal)
            {
                // 복원: 부위별 스탯·상한을 되돌린다(PermInjure의 역연산)
                float removed = -k.Pts + (k.Axis2 >= 0 ? -k.Pts2 : 0f);
                g.Stats = WithAxis(g.Stats, k.Axis, -k.Pts);
                if (k.Axis2 >= 0) g.Stats = WithAxis(g.Stats, k.Axis2, -k.Pts2);
                g.PotentialBudget += removed;
                g.PermInjuries.Remove(chosen);
                g.Fatigue = Math.Min(100, g.Fatigue + 10); healed = true;
                outcome = $"{{medic}} 대수술 성공 — {g.Name}, {k.Name}을(를) 딛고 일어섰다 (부위 스탯·상한 복원 · 피로 +10)";
            }
            else if (roll < heal + 0.50f * (1f - heal))   // 남은 확률의 절반은 단순 실패
            {
                g.Fatigue = Math.Min(100, g.Fatigue + 12);
                outcome = $"{{medic}} 대수술 실패 — {k.Name}은(는) 그대로다. 돈과 체력만 잃었다 (피로 +12)";
            }
            else   // 나머지는 악화 — 칼이 더 깊이 들어갔다
            {
                g.PotentialBudget = MathF.Max(MinPotentialBudget, g.PotentialBudget - 12f);
                g.Fatigue = Math.Min(100, g.Fatigue + 18);
                outcome = $"{{skull}} 대수술 악화 — 칼이 더 깊이 빗나갔다. {g.Name} 잠재력 상한 −12 (상한 {g.PotentialBudget:F0}) · 피로 +18";
            }
        }
        else   // 일시 부상 — 가벼운 처치(저비용·고확률·가벼운 실패, 영구 페널티 없음)
        {
            if (g.InjuryMatches <= 0) return Err("부상이 아니다");
            if (_gold < TempSurgeryCost) return Err($"잔고 부족 (수술비 {TempSurgeryCost:F0})");
            _gold -= TempSurgeryCost;
            float heal = TempHealChance(g);
            if (roll < heal)
            {
                g.InjuryMatches = 0; g.Fatigue = Math.Max(0, g.Fatigue - 10); healed = true;
                outcome = $"{{medic}} 수술 성공 — {g.Name}, 붕대를 풀었다 (부상 완치 · 피로 −10)";
            }
            else
            {
                g.InjuryMatches += 1;
                outcome = $"{{medic}} 수술 실패 — 회복이 더뎌졌다 (부상 +1경기 · 돈만 날렸다). 요양이면 낫는 부상이었다";
            }
        }
        _story.Add((0, "surgery", outcome));
        SaveWorld();
        return JsonSerializer.Serialize(new { ok = true, healed, outcome }, JsonOpts);
    }

    /// <summary>친선/난투 등 리그 외 전투를 viewer.json으로 내보낸다(#2 — 실제 경기화면). 시드 결정론, 무기록.</summary>
    private MatchResult RunExhibition(Gladiator a, Gladiator b, ulong seed)
    {
        var (dA, dB) = BuildDefs(a, b);
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
        if (g == null) return Err("내 모리튜리가 아니다");
        if (g.InjuryMatches > 0) return Err("부상 중 — 거리 싸움은 무리다");
        if (g.Fatigue >= 85) return Err("너무 지쳤다 — 휴식이 먼저");
        var rng = new SimRandom(_worldSeed ^ 0x5417_B4A1UL + (ulong)(_streetSeq++) * 29UL);
        var target = _cast.FirstOrDefault(x => x.Id == targetId && !x.IsPlayer)
                     ?? _cast.Where(x => !x.IsPlayer).OrderBy(_ => rng.NextUInt64()).FirstOrDefault();
        if (target == null) return Err("시비 걸 상대가 없다");
        var res = RunExhibition(g, target, rng.NextUInt64());   // 실제 난투 시뮬 → viewer.json(길거리)
        bool win = res.Winner == 0;
        // 원한 = 관계(그 상대 한정): 시비 걸린 상대는 g에게 원한을 품는다(상대→나 강하게, 나→상대 약하게)
        _ledger.DeepenGrudge(target.Id, g.Id, 20f);
        _ledger.DeepenGrudge(g.Id, target.Id, 8f);
        g.Fatigue = Math.Min(100, g.Fatigue + 5);
        string note;
        if (win)
        {
            g.Popularity += 12f;
            if (SeasonActive) g.PendingEmotions.Add(EmotionTable.Motivated);
            note = $"{{mug}} {g.Name}이(가) {target.Name}을(를) 길거리에서 눕혔다 — 인기 +12 · {target.Name}이(가) 이를 갈다";
        }
        else
        {
            g.Popularity += 4f;
            if (res.StatsA.MinHpPct <= 0.20f && rng.Roll(0.40f)) { g.InjuryMatches = Math.Max(g.InjuryMatches, 1); note = $"{{mug}} {g.Name}, {target.Name}과의 난투에서 밀렸다 — 부상(1경기) · 인기 +4"; }
            else note = $"{{mug}} {g.Name} vs {target.Name} 난투 — {(res.Winner < 0 ? "팽팽했다" : "졌다")} · 인기 +4";
        }
        _story.Add((0, "brawl", note + $" ({target.Name}이(가) {g.Name}에게 원한을 품었다)"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return JsonSerializer.Serialize(new { ok = true, note, target = target.Name, won = win, venue = "street", a = g.Name, b = target.Name }, JsonOpts);
    }

    /// <summary>친선 스파링(#2 실제 경기화면): 같은 부 AI와 연습 경기(투기장 배경) — 무기록·부상 없음, 성장 소량 + 가벼운 피로.</summary>
    public string SparringJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);   // #3 시즌 중에도 가능
        if (g == null) return Err("내 모리튜리가 아니다");
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
        _story.Add((0, "sparring", $"{{swords}} 스파링 — {g.Name} vs {opp.Name}: {wName} 우세" + (grow != null ? $" · {grow} +0.5" : "")));
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

    /// <summary>거리 시비 타겟 후보(라이벌 모리튜리 목록).</summary>
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
        if (g == null) return Err("내 모리튜리가 아니다");
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

        var notes = new List<string> { $"{{fist}} 패싸움 — {string.Join("·", myside.Select(m => m.Name))} vs {string.Join("·", foes.Select(f => f.Name))}" };
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

        // 집계: 참여자 피로, 상대는 g에게 원한(관계 악화), 승패별 인기·부상(난투 결과 MinHpPct 반영)
        foreach (var m in myside.Where(x => x.IsPlayer)) m.Fatigue = Math.Min(100, m.Fatigue + 8);
        foreach (var f in foes) _ledger.DeepenGrudge(f.Id, g.Id, 12f);
        if (won)
        {
            foreach (var m in myside.Where(x => x.IsPlayer)) { m.Popularity += 15f; if (SeasonActive) m.PendingEmotions.Add(EmotionTable.Motivated); }
            notes.Add("{trophy} 완승! 뒷골목을 평정했다 — 인기 대폭 상승");
        }
        else
        {
            g.Popularity += 4f;
            // 크게 얻어맞은 내 선수(HP 20%↓ 생존/전멸)는 부상 위험
            foreach (var m in myside.Where(x => x.IsPlayer))
            {
                var oc = mres.Outcomes.FirstOrDefault(o => o.Name == m.Name);
                if (oc != null && (!oc.Survived || oc.MinHpPct <= 0.20f) && rng.Roll(0.40f))
                { m.InjuryMatches = Math.Max(m.InjuryMatches, 1); notes.Add($"{{impact}} {m.Name} 다구리에 당했다 — 부상(1경기)"); }
            }
            notes.Add("{impact} 수적 난전에 밀렸다 — 굴욕");
        }
        _story.Add((0, "brawl", notes[0] + $" → {(won ? "완승" : "패배")}"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return JsonSerializer.Serialize(new { ok = true, notes, venue, won, myWins = won ? 1 : 0, melee = true }, JsonOpts);
    }

    /// <summary>은퇴(세대·혈통): 프리시즌에 내 선수를 명예롭게 보낸다 → 명예의 전당(★).
    /// 세 진로(교관·스승·스카우터)는 각각 자격 기준을 넘어야 명예의 전당에 오른다.
    /// 단순 은퇴는 폐지 — 자격 미달이면 방출(ReleaseJson)이나 해방(ManumitJson)으로 떠나보낸다.</summary>
    public string RetireJson(string fighterId, string path = "")
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);   // #3 시즌 중에도 가능
        if (g == null) return Err("내 모리튜리가 아니다");
        if (path is not ("instructor" or "master" or "scout")) return Err("은퇴 진로를 선택하라 (교관·스승·스카우터). 그 외엔 방출·해방으로");
        // 자격 검증(진로 지정 시)
        if (path == "instructor" && g.Fame < InstructorFameMin) return Err($"교관 자격 미달 — 명성 {InstructorFameMin:F0}+ 필요 (현재 {g.Fame:F0})");
        if (path == "master" && g.Fame < MasterFameMin) return Err($"스승 자격 미달 — 명성 {MasterFameMin:F0}+ 필요 (현재 {g.Fame:F0})");
        if (path == "scout" && g.CKoW < ScoutKoMin && g.Fame < ScoutFameMin) return Err($"스카우터 자격 미달 — 통산 KO {ScoutKoMin}+ 또는 명성 {ScoutFameMin:F0}+ 필요");

        PurgeRemainingMatches(g.Id);
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        bool hall = path is "instructor" or "master" or "scout";
        if (hall) Unlock("kingmaker");   // 명장의 산실 — 진로 은퇴자 배출
        if (hall) _hall.Add(MakeHall(g, $"{g.CW}-{g.CL}-{g.CD}", Math.Max(1, _seasonsPlayed)));

        switch (path)
        {
            case "instructor":
            {
                // 교관: 생전 최고 스탯 축의 상한을 내 루두스 전체에 +보너스(누적). 스탯이 높을수록 큰 유산.
                int axis = StrongestAxis(g.Stats);
                float bonus = 6f + (AxisValue(g.Stats, axis) - 80f) * 0.12f;   // 80 기준 초과분 가산
                bonus = Math.Clamp(bonus, 4f, 16f);
                _axisCapBonus[axis] += bonus;
                _story.Add((0, "retire", $"{{book}} {g.Name} 은퇴 → 교관 — {AxisName(axis)} 상한 +{bonus:F0} (루두스 전체·누적)"));
                break;
            }
            case "master":
                // 스승: 특성·전술을 한 선수에게 1회 전수(추가). bestow로 소비 — 무엇을 물려줄지는 라니스타가 고른다.
                _masterName = g.Name;
                _masterTraitPool = g.TraitIds.ToArray();
                _masterTacticPool = g.TacticPool.ToArray();
                _masterTrait = g.TraitIds.Length > 0 ? g.TraitIds[0] : null;       // 구버전 호환 기본값
                _masterTactic = g.TacticPool.Length > 0 ? g.TacticPool[^1] : null;
                _mentorName = g.Name;   // 영입 유산도 겸함(기존 스승 효과 유지)
                _story.Add((0, "retire", $"{{scroll}} {g.Name} 은퇴 → 스승 — 특성 {g.TraitIds.Length}종·전술 {g.TacticPool.Length}종 중 골라 한 선수에게 1회 전수"));
                break;
            case "scout":
                _scoutLevel++;
                _story.Add((0, "retire", $"{{eye}} {g.Name} 은퇴 → 스카우터 (Lv{_scoutLevel}) — 영입 안목 향상·후보 정보 공개"));
                break;
        }
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>스승의 유산 전수(1회 소비): 한 선수에게 특성·전술 추가(교체 아님).
    /// traitId·tacticId = 스승의 보유 풀에서 라니스타가 고른 것(빈 값 = 그 항목 생략, 둘 다 생략은 불가).</summary>
    public string BestowJson(string fighterId, string? traitId = null, string? tacticId = null)
    {
        var traits = _masterTraitPool ?? (_masterTrait != null ? new[] { _masterTrait } : Array.Empty<string>());
        var tactics = _masterTacticPool ?? (_masterTactic != null ? new[] { _masterTactic } : Array.Empty<string>());
        if (traits.Length == 0 && tactics.Length == 0) return Err("전수할 스승의 유산이 없다");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        // 선택 검증 — 미지정(구버전 호환)이면 기존 기본값
        string? pickT = string.IsNullOrEmpty(traitId) ? null : traitId;
        string? pickC = string.IsNullOrEmpty(tacticId) ? null : (tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId);
        if (pickT == null && pickC == null) { pickT = _masterTrait; pickC = _masterTactic; }
        if (pickT != null && !traits.Contains(pickT)) return Err("스승이 보유하지 않은 특성");
        if (pickC != null && !tactics.Contains(pickC)) return Err("스승이 보유하지 않은 전술");
        var added = new List<string>();
        if (pickT != null && !g.TraitIds.Contains(pickT))
        { g.TraitIds = g.TraitIds.Append(pickT).ToArray(); added.Add("특성 " + TraitTable.Get(pickT).Name); }
        if (pickC != null && !g.TacticPool.Contains(pickC))
        { g.TacticPool = g.TacticPool.Append(pickC).ToArray(); added.Add("전술 " + pickC.Replace("TAC_","")); }
        _story.Add((0, "master", $"{{scroll}} 스승 {_masterName}의 유산 — {g.Name}에게 {(added.Count > 0 ? string.Join("·", added) : "이미 보유")} 전수"));
        _masterTrait = null; _masterTactic = null; _masterTraitPool = null; _masterTacticPool = null;   // 소비
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    private const float MentorFameMin = 60f, InstructorFameMin = 40f, MasterFameMin = 60f, ScoutFameMin = 30f;
    private const int ScoutKoMin = 12;
    private string? _mentorName;   // 루두스의 스승(은퇴 전설) — 영입 유산
    private string? _masterName, _masterTrait, _masterTactic;   // 스승 전수 대기(소비성 — 구버전 호환 기본값)
    private string[]? _masterTraitPool, _masterTacticPool;      // 전수 후보 풀(라니스타가 고른다)
    private int _scoutLevel;                                     // 스카우터 누적 레벨
    private readonly float[] _axisCapBonus = new float[6];       // 교관 상한 보너스(축별 누적)
    private static int StrongestAxis(FighterStats s)
    {
        float[] v = { s.Atk, s.Def, s.HpMax / 10f, s.Spd, s.Aspd, s.Rct };
        int best = 0; for (int i = 1; i < 6; i++) if (v[i] > v[best]) best = i; return best;
    }
    private static float AxisValue(FighterStats s, int a) => a switch { 0 => s.Atk, 1 => s.Def, 2 => s.HpMax / 10f, 3 => s.Spd, 4 => s.Aspd, _ => s.Rct };
    private static string AxisName(int a) => a switch { 0 => "공격", 1 => "방어", 2 => "체력", 3 => "이동", 4 => "공격 속도", _ => "반응" };

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
        if (def.Id == null) return Err("그런 특전이 없다");
        int lv = PerkLv(id);
        if (lv >= def.Max) return Err($"{def.Name} 최대 Lv");
        int cost = def.Costs[lv];
        if (_glory < cost) return Err($"영광 부족 ({cost} 필요)");
        _glory -= cost;
        _perks[id] = lv + 1;
        _story.Add((0, "perk", $"{{ludus}} 제국 특전 — {def.Name} Lv{lv + 1} (영광 −{cost})"));
        SaveWorld();
        return StateJson();
    }

    private string? _tbWinnerId;   // {scales} 우승 결정전 승자(1부 1위 동률 단판) — 시즌 한정, 영속

    // ── 골드 사용처(후반 인플레 해소): 흥행 개최·후원자 연회·프리미엄 영입·모리튜리 해방 ──
    private const float HostShowCost = 120f, BanquetCost = 100f, PremiumGachaCost = 300f;
    private int _banquetSeason;   // 마지막 연회 시즌(시즌당 1회, 영속)

    /// <summary>{fest} 흥행 개최: 골드를 태워 특별 흥행전을 직접 연다 — 리그 최고 명성 AI에게 도전장(기존 제안 파이프 재사용).
    /// 수익은 흥행(양측 인기·이벤트 배율)으로 돌아온다. 큰돈의 사용처이자 서사 제조기.</summary>
    public string HostShowJson()
    {
        if (!SeasonActive) return Err("시즌 중에만 흥행을 열 수 있다");
        if (_pendingProposalOpp != null) return Err("이미 성사 대기 중인 대전이 있다");
        if (!_cast.Any(g => g.IsPlayer)) return Err("출전시킬 모리튜리가 없다");
        if (_gold < HostShowCost) return Err($"잔고 부족 (흥행 개최 {HostShowCost:F0})");
        var opp = _cast.Where(g => !g.IsPlayer).OrderByDescending(g => g.Fame + g.Popularity).FirstOrDefault();
        if (opp == null) return Err("초청할 상대가 없다");
        _gold -= HostShowCost;
        _pendingProposalOpp = opp.Id; _proposalExec = false;
        _story.Add((_rounds + 1, "event", $"{{fest}} 흥행 개최 — 광장에 방이 붙었다: {LudusNameOf(opp.LudusId)}의 {opp.Name}을(를) 초청하는 특별 흥행전 ({{coin}}−{HostShowCost:F0}, 출전자를 정하라)"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>{wine} 후원자 연회: 골드로 연회를 열어 후원자의 마음을 산다 — 시즌당 1회, 후원 관계 +12.</summary>
    public string BanquetJson()
    {
        if (_banquetSeason == _seasonNo && _seasonNo > 0) return Err("이번 시즌 연회는 이미 열었다 — 과공은 비례다");
        if (_gold < BanquetCost) return Err($"잔고 부족 (연회 {BanquetCost:F0})");
        _gold -= BanquetCost; _banquetSeason = _seasonNo;
        Patron(12f);
        _story.Add((_rounds + 1, "patron", $"{{wine}} 연회 — 포도주가 흐르고 후원자의 웃음이 길어졌다 ({{coin}}−{BanquetCost:F0} · 후원 +12)"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>{dove} 모리튜리 해방(마누미시오): 큰돈을 들여 공로 모리튜리에게 자유(루디스)를 사준다 —
    /// 명예의 전당 등재 + 루두스 명성·영광 획득. 세계관 페이오프이자 최고의 골드 사용처.</summary>
    public string ManumitJson(string fighterId)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        float cost = MathF.Round(150f + g.Fame * 1.5f);
        if (_gold < cost) return Err($"잔고 부족 (해방 몸값 {cost:F0} = 150 + 명성×1.5)");
        _gold -= cost;
        PurgeRemainingMatches(g.Id);
        _cast.Remove(g);
        _ledger.RemoveFighter(g.Id);
        _hall.Add(MakeHall(g, $"{g.CW}-{g.CL}-{g.CD} {{dove}}해방", Math.Max(1, _seasonsPlayed)));
        AddRep(12f); AddGlory(4f);
        _story.Add((0, "retire", $"{{dove}} 해방 — {g.Name}, 목검(루디스)을 받고 자유민이 되다. 군중이 그 이름을 연호한다 ({{coin}}−{cost:F0} · 명성 +12 {{glory}}+4)"));
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    // ── [19] 프리시즌 준비 주간 — 4주 동안 매주 활동 1개 선택(비용·리스크·보상 상충). 세 갈래를 한 틀에:
    //     ① 준비 주간(프레임) ② 원정 순회(도시 친선전) ③ 심화 훈련 캠프(팀 프로그램). 결과는 시즌으로 이월. ──
    private int _preWeek;   // 이번 프리시즌 소진한 주(0~PreWeeksMax) — 영속. StartSeason에서 0.
    private const int PreWeeksMax = 4;
    private static readonly (string City, float Reward, float Risk)[] PreCities =
    {
        ("폼페이 원형극장", 45f, 0.12f), ("네아폴리스 항구 흥행", 60f, 0.18f), ("타렌툼 변방 투기장", 35f, 0.08f),
        ("시라쿠사이 대경기장", 80f, 0.25f), ("루카니아 산간 마을", 30f, 0.06f), ("브룬디시움 군항", 55f, 0.16f),
    };
    private (string City, float Reward, float Risk, Gladiator? Foe) PreExpedition(int week)
    {
        var c = PreCities[StableHash($"{_worldSeed}exp{_seasonNo}w{week}") % PreCities.Length];
        var foe = _cast.Where(g => !g.IsPlayer).OrderBy(_ => new SimRandom(_worldSeed ^ (ulong)(week * 131 + _seasonNo)).NextUInt64()).FirstOrDefault();
        return (c.City, c.Reward, c.Risk, foe);
    }
    private sealed record PreExpDoc(string City, string? Foe, int Reward, int RiskPct);
    private sealed record PreseasonDoc(int Week, int MaxWeek, PreExpDoc Expedition);
    private PreseasonDoc? BuildPreseasonDoc()
    {
        if (SeasonActive || _playerless) return null;
        var (city, reward, risk, foe) = PreExpedition(_preWeek);
        return new PreseasonDoc(_preWeek, PreWeeksMax, new PreExpDoc(city, foe?.Name, (int)reward, (int)MathF.Round(risk * 100f)));
    }
    private bool PreGuard(out string err)
    {
        err = "";
        if (SeasonActive) { err = "프리시즌에만 준비할 수 있다"; return false; }
        if (_preWeek >= PreWeeksMax) { err = "준비 기간이 끝났다 — 이제 개막하라"; return false; }
        if (!_cast.Any(g => g.IsPlayer)) { err = "데려갈 모리튜리가 없다"; return false; }
        return true;
    }

    /// <summary>심화 훈련 캠프(한 주) — 팀 프로그램 3택: 담금질(성장·피로)/실전 감각(성장·부상위험)/요양(회복).</summary>
    public string TrainingCampJson(string kind)
    {
        if (!PreGuard(out var err)) return Err(err);
        var rng = new SimRandom(_worldSeed ^ 0xCA37_0001UL + (ulong)(_seasonNo * 17 + _preWeek));
        var mine = _cast.Where(g => g.IsPlayer).ToList();
        string note;
        switch (kind)
        {
            case "forge":
                foreach (var g in mine) { g.TrainingPoints += 1; g.Fatigue = Math.Min(100, g.Fatigue + 10); }
                note = "{flame} 담금질 훈련 — 전원 훈련 포인트 +1 (피로 +10)"; break;
            case "spar":   // 실전 감각 — 성장 굴림 + 낮은 부상 위험(리스크)
            {
                var hurt = new List<string>();
                foreach (var g in mine)
                {
                    Grow(g, rng); g.Fatigue = Math.Min(100, g.Fatigue + 5);
                    if (g.InjuryMatches == 0 && rng.Roll(0.10f)) { g.InjuryMatches = 1; hurt.Add(g.Name); }
                }
                note = "{{swords}} 실전 감각 훈련 — 전원 성장 굴림" + (hurt.Count > 0 ? $" · 부상: {string.Join(",", hurt)}(1경기)" : " · 무사고"); break;
            }
            default:   // rest
                foreach (var g in mine) { g.Fatigue = 0; if (g.InjuryMatches > 0) g.InjuryMatches--; }
                note = "{heart} 요양 — 전원 피로 완전 회복 · 부상 호전"; break;
        }
        _preWeek++;
        _story.Add((0, "camp", $"[{_preWeek}주차] {note}"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>원정 친선전(한 주) — 지목 모리튜리를 다른 도시로 보내 비공식 일전. 승리=골드·인기·성장, 패배도 성장, 부상 위험.</summary>
    public string PreseasonCupJson(string fighterId)
    {
        if (!PreGuard(out var err)) return Err(err);
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("보낼 모리튜리를 고르라");
        if (g.InjuryMatches > 0) return Err("부상 중 — 원정은 무리다");
        var (city, reward, risk, foe) = PreExpedition(_preWeek);
        if (foe == null) return Err("상대가 없다");
        var rng = new SimRandom(_worldSeed ^ 0x5AC0_50FFUL + (ulong)(_seasonNo * 29 + _preWeek * 7));
        var res = RunExhibition(g, foe, rng.NextUInt64());
        Grow(g, rng); g.Fatigue = Math.Min(100, g.Fatigue + 8);
        string outcome;
        if (res.Winner == 0)
        {
            float prize = MathF.Round(reward); _gold += prize; g.Popularity += 5f; AddRep(2f);
            outcome = $"승리! 상금 {{coin}}+{prize:F0} · 인기 +5 · 명성 +2";
        }
        else outcome = res.Winner == 1 ? "패배 — 그러나 원정의 경험은 남는다" : "무승부 — 팽팽했다";
        // 부상 위험(도시 난이도에 비례)
        if (g.InjuryMatches == 0 && rng.Roll(risk)) { g.InjuryMatches = 1; outcome += " · 부상(1경기)"; }
        _preWeek++;
        _story.Add((0, "sparring", $"[{_preWeek}주차] {{scroll}} 원정 — {g.Name}, {city} 원정 친선전: {outcome}"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>원석 발굴(한 주) — 골드를 들여 숨은 원석을 찾는다. 확률로 무료 뽑기권(다음 영입 1회 무료) 또는 후보 정보.</summary>
    public string PreScoutJson()
    {
        if (!PreGuard(out var err)) return Err(err);
        const float cost = 50f;
        if (_gold < cost) return Err($"잔고 부족 (발굴 {cost:F0})");
        _gold -= cost; _preWeek++;
        var rng = new SimRandom(_worldSeed ^ 0x5C00_7A11UL + (ulong)(_seasonNo * 41 + _preWeek));
        string note;
        if (rng.Roll(0.45f)) { _freeGachas++; note = "{gem} 대성공 — 원석의 행방을 잡았다! 무료 영입권 +1"; }
        else if (rng.Roll(0.5f)) { AddRep(3f); note = "{search} 소득 — 시장에 안면을 텄다 (루두스 명성 +3)"; }
        else note = "…허탕 — 이번엔 쓸 만한 원석이 없었다";
        _story.Add((0, "recruit", $"[{_preWeek}주차] {{search}} 원석 발굴 — {note} ({{coin}}−{cost:F0})"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>후원 협상(한 주) — 후원자를 접대해 관계를 다진다. 후원 +10, 소액 비용.</summary>
    public string PreNegotiateJson()
    {
        if (!PreGuard(out var err)) return Err(err);
        const float cost = 40f;
        if (_gold < cost) return Err($"잔고 부족 (협상 {cost:F0})");
        _gold -= cost; _preWeek++; Patron(10f);
        _story.Add((0, "patron", $"[{_preWeek}주차] {{wine}} 후원 협상 — 포도주와 약속이 오갔다 (후원 +10 · {{coin}}−{cost:F0})"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>이번 시즌 상대전적 차(a 기준 승−패) — 동률 판정용 승자승.</summary>
    private int SeasonH2H(Gladiator a, Gladiator b)
    {
        int d = 0;
        foreach (var m in _matchLog)
        {
            bool ab = m.AId == a.Id && m.BId == b.Id, ba = m.AId == b.Id && m.BId == a.Id;
            if (!ab && !ba) continue;
            if (m.Winner == a.Name) d++; else if (m.Winner == b.Name) d--;
        }
        return d;
    }
    private int SeasonKo(Gladiator g) => _matchLog.Count(m => m.Winner == g.Name && m.Reason == "KO");

    /// <summary>순위표 — 동률 기준(명문화): 승점 → 승수 → {scales}결정전 승자 → 승자승(시즌 상대전적) → 시즌 KO승 → 명성.</summary>
    private List<Gladiator> Standings(int? division = null)
    {
        var list = _cast.Where(g => division == null || g.Division == division)
             .OrderByDescending(g => g.SeasonPoints).ThenByDescending(g => g.W)
             .ThenByDescending(g => g.Id == _tbWinnerId ? 1 : 0)
             .ThenByDescending(g => SeasonKo(g)).ThenByDescending(g => g.Fame).ToList();
        // 승자승 보정: 인접 2인이 승점·승수 동률이고 뒤쪽이 상대전적 우세면 교환(결정전 승자는 불가침)
        for (int i = 0; i + 1 < list.Count; i++)
        {
            var x = list[i]; var y = list[i + 1];
            if (x.SeasonPoints != y.SeasonPoints || x.W != y.W) continue;
            if (x.Id == _tbWinnerId || y.Id == _tbWinnerId) continue;
            if (SeasonH2H(y, x) > 0) { list[i] = y; list[i + 1] = x; }
        }
        return list;
    }

    // ── 진행 ──

    /// <summary>다음 경기 1판. 프리시즌이면 시즌 개막만(경기 안 침 — 라니스타이 1경기부터 전술을 고를 수 있게).
    /// tacticId = 내 선수의 이번 경기 전술(선택). prep = 경기 전 방침(C1: forge/rest/show — 시뮬 무영향, 메타만).</summary>
    public MatchSummary PlayNext(string? tacticId = null, string? prep = null)
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

        // 전술 결정: 내 선수 = 라니스타 선택(이번 요청 or 기존 유지) / AI = 상대 맞춤 휴리스틱 + 시드 노이즈
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);
        if (A.IsPlayer) { if (tacticId != null && A.TacticPool.Contains(tacticId)) A.TacticId = tacticId; }
        else A.TacticId = SelectTacticAi(A, B, tacRng);
        if (B.IsPlayer) { if (tacticId != null && !A.IsPlayer && B.TacticPool.Contains(tacticId)) B.TacticId = tacticId; }
        else B.TacticId = SelectTacticAi(B, A, tacRng);

        // 경기 전 방침(C1): 라니스타의 컨디셔닝 결정 — 시뮬 def 무접촉(잠정·정산 동일성 보존), 성장·피로·수입·흥행만
        _prepKind = _prepId = null;
        var mine0 = A.IsPlayer ? A : B.IsPlayer ? B : null;
        if (prep is "forge" or "rest" or "show" && mine0 != null)
        {
            _prepKind = prep; _prepId = mine0.Id;
            if (prep == "forge") mine0.Fatigue = Math.Min(100, mine0.Fatigue + 8);        // 담금질 — 몸을 혹사해 배움을 늘린다
            else if (prep == "rest") mine0.Fatigue = Math.Max(0, mine0.Fatigue - 10);     // 아낀다 — 회복 우선, 배움은 없다
            else { mine0.Popularity += 6f; mine0.Fatigue = Math.Min(100, mine0.Fatigue + 4); }   // 무대 — 흥행몰이(하이프·출전료에 반영)
        }

        var res = Play(A, B, s.Round, s.Kind, out float income, out string incomeNote, out var mine, s.Format);
        _prepKind = _prepId = null;
        if (s.IsEvent)
            _eventDocs.Add(new EventDoc(A.Name, B.Name, s.Score,
                res.Winner < 0 ? "무승부" : (res.Winner == 0 ? A.Name : B.Name), res.Reason == "KO"));

        // {scales} 우승 결정전: 1부 승점·승수 동률 1위끼리의 단판 — 모래가 챔피언을 정한다
        if (s.Kind == "tiebreak" && res.Winner >= 0)
        {
            var tbW = res.Winner == 0 ? A : B;
            _tbWinnerId = tbW.Id;
            _story.Add((s.Round, "season", $"{{scales}} 우승 결정전 — {tbW.Name}이(가) 동률의 저울을 갈랐다! 시즌 순위 1위 확정"));
        }

        // 컵 결승: 우승자 확정 + 상금·명성·업적
        if (s.Kind == "cup_final" && res.Winner >= 0)
        {
            var cupW = res.Winner == 0 ? A : B;
            _cupChampion = cupW.Name;
            cupW.Fame += 10f;
            _story.Add((s.Round, "cup", $"{{trophy}} 챔피언십 컵 우승 — {cupW.Name}!"));
            if (cupW.IsPlayer) { _gold += CupWinPrize; AddRep(RepCupTitle); AddGlory(GloryCup); Unlock("first_cup");
                                 _myCupTitles++;   // 엔드게임 업적 카운트
                                 if (_edict is { Type: "cup" }) MarkEdictDone(); }
            else AddRivalRep(cupW.LudusId, RepCupTitle);
        }
        else if (s.Kind == "cup_sf" && res.Winner >= 0)   // 4강 진출 상금(내 선수)
        {
            var w = res.Winner == 0 ? A : B;
            if (w.IsPlayer) _gold += CupSemiPrize;
        }
        else if (s.Kind.StartsWith("fest_"))   // {masks} 대항전: 승자 진출(무승부 = 상위 시드 A), 결승 = 왕관
        {
            var fw = res.Winner == 1 ? B : A;
            if (res.Winner < 0) _story.Add((s.Round, "festival", $"{{masks}} 무승부 — 시드 우위의 {fw.Name}이(가) 진출한다"));
            fw.Popularity += 3f;   // 축제의 함성 — 흥행 메타만(전투 무영향)
            int slot = _festSlots.FindIndex(x => x.Length == 0);
            if (slot >= 0) _festSlots[slot] = fw.Id;
            if (s.Kind == "fest_final")
            {
                _festChampion = fw.Name; _festStage = 4;
                fw.Fame += 8f; fw.Popularity += 7f;
                _story.Add((s.Round, "festival", $"{{masks}} 사투르날리아 대항전 우승 — {LudusNameOf(fw.LudusId)}의 {fw.Name}, 축제의 왕관을 쓴다!"));
                if (fw.IsPlayer) { _gold += FestWinPrize; AddRep(RepFestTitle); AddGlory(GloryFest); }
                else AddRivalRep(fw.LudusId, RepFestTitle);
            }
        }
        else if (s.Kind == "gauntlet" && res.Winner >= 0)   // {arena} 초청전: 승당 하사, 전승 시 대관
        {
            var w = res.Winner == 0 ? A : B;
            if (w.IsPlayer)
            {
                _gauntletWins++; _gold += 100f; AddGlory(5f);
                _story.Add((s.Round, "gauntlet", $"{{arena}} 초청전 {_gauntletWins}승 — {w.Name} ({{coin}}+100 {{glory}}+5)"));
                if (_gauntletWins >= 3)
                { AddGlory(15f); _story.Add((s.Round, "gauntlet", $"{{crown}} 초청전 전승! {w.Name}, 황제 앞에서 대관하다 ({{glory}}+15)")); }
            }
        }

        // 베팅 정산: 이 경기에 걸었으면 승패 판정 (결과 카드 연계 노트 포함)
        bool betWon = false; string? betNote = null;
        if (_betCursor == _cursor - 1)
        {
            _betCursor = -1;
            string on = BetLabel(_betSide, A, B);
            bool bko = res.Reason == "KO";
            bool won = _betSide switch {   // 승자 × 방식(KO/판정) 조합 판정
                0 => res.Winner == 0, 1 => res.Winner == 1,
                2 => res.Winner == 0 && bko, 3 => res.Winner == 0 && !bko,
                4 => res.Winner == 1 && bko, 5 => res.Winner == 1 && !bko, _ => false };
            _betStreak = won ? _betStreak + 1 : 0;   // {flame} 연속 적중 스트릭
            float payout = won ? MathF.Round(_betAmount * _betOdds) : 0f;   // 마진은 배당에 내장
            if (won)
            {
                _gold += payout; _seasonBetNet += payout;
                if (++_betHits >= 10) Unlock("gambler");   // 행운의 도박사
                _story.Add((s.Round, "bet", $"{{dice}} 적중! {on} — 배당금 +{payout:F0}"));
            }
            else _story.Add((s.Round, "bet", $"{{dice}} 빗나감 — {_betAmount:F0} 데나리우스가 모래에 묻혔다"));
            betWon = won;
            betNote = won ? $"{{dice}} 적중! {on}에 건 {_betAmount:F0} → 배당금 +{payout:F0} (×{_betOdds:F2})"
                          : $"{{dice}} 빗나감 — {on}에 건 {_betAmount:F0} 데나리우스가 모래에 묻혔다";
            _betLog.Add(new BetLogRec(_seasonNo, on, _betAmount, _betOdds, won, payout));
            while (_betLog.Count > 60) _betLog.RemoveAt(0);
        }

        EnsureSchedule();   // 다음 페이즈 편성(예: 4강 후 결승) — 종료 판정 전에
        bool last = _cursor >= _schedule.Count && _cupStage == 3;
        if (!last) MaybeSpawnEvent(A.IsPlayer ? A : B.IsPlayer ? B : null);   // 내 경기 후 서사 이벤트(2b)
        if (!last && (A.IsPlayer || B.IsPlayer)) TeaseNext(s.Round);   // {horn} 예고 — 내 경기 직후, 다음 경기의 기대감
        string? cato = CatoComment(A, B, res.Winner, res.Reason, A.IsPlayer || B.IsPlayer);   // [13] 저장 전(참조 카운터 영속)
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
            _lastUpset, winnerOdds, betWon, betNote, _lastExec, _lastFixNote, _lastFixBad,
            Cato: cato);   // [13] 카토의 한 줄 평
    }

    /// <summary>
    /// 다음 경기가 없으면 다음 페이즈를 편성: 정규 소진 → 이벤트 빅매치 → 챔피언십 컵(4강→결승).
    /// 각 단계는 라니스타이 전술을 고를 수 있게 한 페이즈씩 채운다. 시즌 종료 판정 = 컵까지 끝(_cupStage==3).
    /// </summary>
    private void EnsureSchedule()
    {
        if (!SeasonActive) return;
        MaybeScheduleFestival();   // {fest} 미드시즌 대항전 — 정규 전반 소진 시 커서 위치에 삽입(전반 → 축제 → 후반)
        if (_cursor < _schedule.Count) return;

        // 정규 소진 → {scales} 우승 결정전: 1부 1·2위가 승점·승수 완전 동률이면 단판으로 가린다(최대 2회 — 무승부 재대결 1회).
        // 이벤트·컵은 순위에 무영향이라 정규 직후가 유일한 판정 시점. 승자는 Standings 최우선.
        if (_tbWinnerId == null && _schedule.Count(x => x.Kind == "tiebreak") < 2)
        {
            var d1 = Standings(1);
            if (d1.Count >= 2 && d1[0].SeasonPoints == d1[1].SeasonPoints && d1[0].W == d1[1].W
                && d1[0].SeasonPoints > 0)
            {
                _schedule.Add(new SchedRec(_rounds + 1, d1[0].Id, d1[1].Id, true, 0f, "tiebreak"));
                _story.Add((_rounds + 1, "season", $"{{scales}} 승점 동률! {d1[0].Name} vs {d1[1].Name} — 우승 결정전이 편성됐다 (단판, 모래가 답한다)"));
                return;
            }
        }

        // 정규 소진 → 이벤트 빅매치 (일부는 특수 형식: {skull}처형전 — 시드 결정론)
        if (!_eventsAppended)
        {
            var fmtRng = new SimRandom(SeasonSeed ^ 0xF0_47_11UL);
            foreach (var (a, b, score) in TopEventCards(Math.Max(2, _cast.Count / 2)))
            {
                string fmt = fmtRng.Roll(0.30f) ? "execution" : "normal";   // {skull}처형전(30%) — 나머지는 일반전
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
            _story.Add((_rounds + 2, "cup", $"{{ludus}} 챔피언십 컵 개막 — {top[0].Name}·{top[1].Name}·{top[2].Name}·{top[3].Name}"));
            _schedule.Add(new SchedRec(_rounds + 2, _cupSeeds[0], _cupSeeds[3], false, 0f, "cup_sf"));  // 1v4
            _schedule.Add(new SchedRec(_rounds + 2, _cupSeeds[1], _cupSeeds[2], false, 0f, "cup_sf"));  // 2v3
            _cupStage = 1;
            return;
        }
        if (_cupStage == 1)   // 4강 둘 다 끝 → 결승 편성
        {
            // 시드 기반 탐지 — 라운드 번호 매칭은 편성 체계가 바뀌면 깨진다(4강 = 가장 최근의 시드 간 대결 2건)
            var sfWinners = _matchLog.Where(m => _cupSeeds.Contains(m.AId) && _cupSeeds.Contains(m.BId)).TakeLast(2)
                .Select(m => m.Winner == m.AName ? m.AId : m.BId).ToList();
            if (sfWinners.Count == 2)
                _schedule.Add(new SchedRec(_rounds + 3, sfWinners[0], sfWinners[1], false, 0f, "cup_final"));
            _cupStage = 2;
            return;
        }
        if (_cupStage == 2) _cupStage = 3;   // 결승 끝 → 컵 종료

        // 컵 종료 → {arena} 황제의 초청전(건틀릿): 총애 6+ 루두스의 간판이 리그 최강 3인과 연전 (총애 트랙의 정점)
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
                    _story.Add((_rounds + 4, "gauntlet", $"{{arena}} 황제의 초청전 — 총애받는 {champ.Name}, 최강 3인({string.Join("·", rivals.Select(x => x.Name))})과 연전!"));
                }
            }
        }
    }

    // ── {masks} 사투르날리아 대항전(미드시즌) — 루두스별 대표 1인 토너먼트. 순위 무관 축제전, 걸린 것은 루두스의 명예 ──
    private const float FestWinPrize = 80f, RepFestTitle = 15f, GloryFest = 6f;
    private int FestHalfRound => (_rounds + 1) / 2;   // 정규 전반의 마지막 라운드(축제 삽입 경계)

    /// <summary>정규 전반이 끝나는 경계에서 대항전 단계를 커서 위치에 삽입. 단계 승자들이 모이면 다음 단계.
    /// 이미 시즌 종반인 세이브(이벤트·컵 진입)는 이번 시즌 생략. 대표: AI=루두스 간판, 플레이어=지명(기본 간판).</summary>
    private void MaybeScheduleFestival()
    {
        if (_festStage >= 4) return;
        if (_eventsAppended || _cupStage > 0) { _festStage = 4; return; }   // 시즌 종반 승계(구 세이브) — 생략
        bool boundary = _cursor >= _schedule.Count
            || (_schedule[_cursor].Kind == "regular" && _schedule[_cursor].Round > FestHalfRound);
        if (!boundary || _schedule.Skip(_cursor).Any(s => s.Kind.StartsWith("fest_"))) return;

        List<string> alive;
        if (_festStage == 0)
        {
            alive = FestParticipants();
            if (alive.Count < 2) { _festStage = 4; return; }
            _story.Add((FestHalfRound, "festival",
                $"{{masks}} 사투르날리아 대항전 개막 — 루두스의 명예를 걸고: {string.Join("·", alive.Select(id => ById(id).Name))}"));
        }
        else
        {
            if (_festSlots.Count == 0 || _festSlots.Any(x => x.Length == 0)) { _festStage = 4; return; }   // 이상 상태 방어
            alive = _festSlots.Where(id => _cast.Any(g => g.Id == id)).ToList();   // 사망·이탈 정리
            if (alive.Count < 2) { _festStage = 4; return; }
        }
        ScheduleFestRound(alive);
        _festStage = Math.Min(3, _festStage + 1);
    }

    /// <summary>대표 선발 — 루두스당 1인(살아있는 최고 명성), 플레이어는 지명 존중. 시드 = 명성 내림차순, 최대 8.</summary>
    private List<string> FestParticipants()
    {
        var reps = new List<Gladiator>();
        var mine = _cast.Where(g => g.IsPlayer).ToList();
        if (mine.Count > 0)
            reps.Add(mine.FirstOrDefault(g => g.Id == _festRepId) ?? mine.OrderByDescending(g => g.Fame).First());
        foreach (var r in ActiveRivalLudi)
        {
            var top = _cast.Where(g => !g.IsPlayer && g.LudusId == r.Id).OrderByDescending(g => g.Fame).FirstOrDefault();
            if (top != null) reps.Add(top);
        }
        return reps.OrderByDescending(g => g.Fame).ThenBy(g => g.Id).Take(8).Select(g => g.Id).ToList();
    }

    /// <summary>한 단계 편성: 표준 브래킷(1vN·2vN−1…), 모자란 자리는 상위 시드 부전승. 커서 위치에 삽입(후반 앞).</summary>
    private void ScheduleFestRound(List<string> alive)
    {
        int size = alive.Count <= 2 ? 2 : alive.Count <= 4 ? 4 : 8;
        string kind = size == 8 ? "fest_qf" : size == 4 ? "fest_sf" : "fest_final";
        _festSlots = Enumerable.Repeat("", size / 2).ToList();
        int ins = _cursor;
        for (int i = 0; i < size / 2; i++)
        {
            string a = alive[i];
            string? b = size - 1 - i < alive.Count ? alive[size - 1 - i] : null;
            if (b == null) _festSlots[i] = a;   // 부전승 — 상위 시드가 조용히 다음 단계로
            else _schedule.Insert(ins++, new SchedRec(FestHalfRound, a, b, false, 0f, kind));
        }
        // 삽입으로 뒤 인덱스가 밀림 — 스케줄 인덱스를 참조하는 상태 보정
        int shifted = ins - _cursor;
        if (shifted > 0 && _betCursor >= _cursor) _betCursor += shifted;
        if (_oddsCursor >= _cursor) _oddsCursor = -1;   // 커서 경기 배당 캐시 무효화
    }

    /// <summary>{masks} 대항전 대표 지명 — 대항전 시작 전(프리시즌·정규 전반)에만 가능.</summary>
    public string FestivalRepJson(string fighterId)
    {
        if (_festStage != 0) return Err("대항전이 이미 시작됐다");
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        _festRepId = g.Id;
        SaveWorld(); if (_interactive) WriteSeasonJson();
        return JsonSerializer.Serialize(new { ok = true, rep = g.Name }, JsonOpts);
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
            if (ById(s.A).IsPlayer || ById(s.B).IsPlayer) break; // 내 경기 발견 — 멈춰서 라니스타에게
            var m = PlayNext(); played++;
            if (m.SeasonCompleted) { seasonDone = true; break; }
        }
        return JsonSerializer.Serialize(new { played, seasonDone }, JsonOpts);
    }

    // ── 라이브 매치(라니스타 실시간 개입) — 관전 먼저, 정산은 나중. 커서는 정산 시에만 전진(앱 종료 = 미개시로 복원, 세이브 안전) ──
    private sealed class LiveMatch
    {
        public required string MyId; public required string[] MyPool;
        public required string InitialTactic;   // 개막 전술 — 같은 전술 재선택 판정용
        public required List<TacticSwitch> Switches;
        public string? Prep;                    // 경기 전 방침(C1) — 정산 시 적용(시뮬 무영향)
        // 내전(#6): 두 번째 조종 대상(양측 다 내 모리튜리일 때만)
        public string? MyId2; public string[]? MyPool2; public string? InitialTactic2; public List<TacticSwitch>? Switches2;
    }
    private LiveMatch? _live;                                       // 진행 중 라이브 매치(메모리 전용 — 영속 안 함)
    private (string FighterId, TacticSwitch[] Switches)? _liveSwitches;   // 정산 시 Play가 def에 주입
    private (string FighterId, TacticSwitch[] Switches)? _liveSwitches2;  // 내전 2번째 조종 대상

    /// <summary>내 경기 라이브 시작: 커서 전진 없이 잠정 시뮬 → viewer.json. 정산(/api/settle) 전까지 세계 무변이.
    /// prep = 경기 전 방침(C1) — 시뮬 무영향이라 잠정·정산 동일성이 유지된다(정산 때 적용).</summary>
    public string LiveBeginJson(string? tacticId, string? tacticIdB = null, string? prep = null)
    {
        if (!SeasonActive || _cursor >= _schedule.Count) return Err("다음 경기가 없다");
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var mine = A.IsPlayer ? A : B.IsPlayer ? B : null;
        if (mine == null) return Err("내 경기가 아니다");
        bool mirror = A.IsPlayer && B.IsPlayer;   // 내전(#6): 양측 다 내 모리튜리 = 이중 조종

        // PlayNext와 동일한 전술 결정(같은 rng 시드 → 정산 때 재현됨). 클라이언트는 TAC_ 접두사 없이 보낸다 → 정규화
        string? Norm(string? t) => t is { Length: > 0 } ? (t.StartsWith("TAC_") ? t : "TAC_" + t) : null;
        tacticId = Norm(tacticId); tacticIdB = Norm(tacticIdB);   // tacticId = mine(첫 조종), tacticIdB = 내전 상대측
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);
        // 각 선수의 전술: 내 조종이면 지정 전술(mine=tacticId·내전 상대=tacticIdB), AI면 SelectTacticAi
        void SetTac(Gladiator f, Gladiator o)
        {
            if (!f.IsPlayer) { f.TacticId = SelectTacticAi(f, o, tacRng); return; }
            string? want = f.Id == mine.Id ? tacticId : tacticIdB;
            if (want != null && f.TacticPool.Contains(want)) f.TacticId = want;
        }
        SetTac(A, B); SetTac(B, A);

        _live = new LiveMatch { MyId = mine.Id, MyPool = mine.TacticPool, InitialTactic = mine.TacticId, Switches = new(),
                                Prep = prep is "forge" or "rest" or "show" ? prep : null };
        if (mirror)
        {
            var other = mine == A ? B : A;
            _live.MyId2 = other.Id; _live.MyPool2 = other.TacticPool; _live.InitialTactic2 = other.TacticId; _live.Switches2 = new();
        }
        LiveResim();
        return JsonSerializer.Serialize(new { ok = true, a = A.Name, b = B.Name, round = s.Round,
            kind = s.Kind, remaining = 2, mirror }, JsonOpts);
    }

    /// <summary>라이브 재시뮬(같은 시드 + 현재 전환 예약) → viewer.json 재작성. Play와 동일한 def 조립 = 정산과 일치.</summary>
    private void LiveResim()
    {
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var (defA, defB) = BuildDefs(A, B);   // 정산(Play)과 동일 조립
        if (_live!.Switches.Count > 0)
        {
            var sw = _live.Switches.OrderBy(x => x.Time).ToArray();
            if (A.Id == _live.MyId) defA = defA with { TacticSwitches = sw };
            else defB = defB with { TacticSwitches = sw };
        }
        if (_live.Switches2 is { Count: > 0 } sw2list)   // 내전 2번째 조종 대상의 전환
        {
            var sw2 = sw2list.OrderBy(x => x.Time).ToArray();
            if (A.Id == _live.MyId2) defA = defA with { TacticSwitches = sw2 };
            else defB = defB with { TacticSwitches = sw2 };
        }
        ulong seed = SeasonSeed + (ulong)(_matchIdx + 1);   // Play의 ++_matchIdx와 동일
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(defA, defB, seed, events, frames);
        ViewerExport.WriteDoc(defA, defB, seed, res, frames, events, "viewer.json",
            EndowOf(A.Id, defA), EndowOf(B.Id, defB),
            PreMatchQuote(A, B), PreMatchQuote(B, A));   // 경기 직전 대사(#5) — 경기장 인트로 줌인용
    }

    /// <summary>관전 중 전술 변경(2회 한정): 그 시각부터 새 전술로 재시뮬 — 이후의 운명이 갈린다.</summary>
    public string LiveSwitchJson(float time, string tacticId, int side = 0)
    {
        if (_live == null) return Err("라이브 경기가 없다");
        // side 1 = 내전 2번째 조종 대상, 그 외 = 첫 조종 대상
        var pool = side == 1 ? _live.MyPool2 : _live.MyPool;
        var switches = side == 1 ? _live.Switches2 : _live.Switches;
        string init = side == 1 ? (_live.InitialTactic2 ?? "") : _live.InitialTactic;
        if (pool == null || switches == null) return Err("조종 대상이 없다");
        string full = tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId;
        if (!pool.Contains(full)) return Err("이 자가 아는 전술이 아니다");
        // 그 시각에 이미 적용 중인 전술을 다시 고르면 = 변화 없음 → 기회 미차감(#12)
        string activeNow = switches.Where(x => x.Time <= time).OrderBy(x => x.Time).LastOrDefault()?.TacticId ?? init;
        if (full == activeNow)
            return JsonSerializer.Serialize(new { ok = true, remaining = 2 - switches.Count, nochange = true }, JsonOpts);
        if (switches.Count >= 2) return Err("전술은 경기당 두 번까지만 바꿀 수 있다");
        switches.Add(new TacticSwitch(MathF.Max(0.1f, time), full));
        LiveResim();
        return JsonSerializer.Serialize(new { ok = true, remaining = 2 - switches.Count }, JsonOpts);
    }

    /// <summary>라이브 정산: 예약된 전환을 주입해 정식 경기 처리(수입·명성·관계·운명·저장). 관전한 것과 같은 시드 = 같은 결과.</summary>
    public string LiveSettleJson()
    {
        if (_live == null) return Err("정산할 라이브 경기가 없다");
        if (_live.Switches.Count > 0)
            _liveSwitches = (_live.MyId, _live.Switches.OrderBy(x => x.Time).ToArray());
        if (_live.MyId2 != null && _live.Switches2 is { Count: > 0 } s2)
            _liveSwitches2 = (_live.MyId2, s2.OrderBy(x => x.Time).ToArray());
        string? prep = _live.Prep;
        _live = null;
        return JsonSerializer.Serialize(PlayNext(null, prep), JsonOpts);
    }

    /// <summary>시즌 자동완주(편의): 내 경기 포함 남은 전 경기를 현재 전술로 진행. 이벤트 발생 시 멈춰서 라니스타에게 결정 위임.</summary>
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
        if (idx < 0 || idx >= _greatest.Count) return Err("기억할 만한 경기가 없다");
        var e = _greatest[idx].Entry;
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(e.DefA, e.DefB, e.Seed, events, frames);
        ViewerExport.WriteDoc(e.DefA, e.DefB, e.Seed, res, frames, events, "viewer.json",
            EndowOf(e.AId, e.DefA), EndowOf(e.BId, e.DefB),
            ReplayQuote(e.DefA, e.Seed), ReplayQuote(e.DefB, e.Seed + 1), ReplayEndFocus(res));
        return JsonSerializer.Serialize(new { ok = true, a = e.AName, b = e.BName, round = e.Round, isEvent = e.IsEvent }, JsonOpts);
    }

    /// <summary>경기 재관전: 로그의 스냅샷+시드로 결정론 재시뮬 → viewer.json. idx<0 = 최근 경기.</summary>
    /// <summary>리플레이 인트로 대사(#5) — 라이브 PreMatchQuote와 달리 관계·감정 정보가 없으니 성격 기반 결정론 대사.</summary>
    private static string ReplayQuote(FighterDef d, ulong seed)
    {
        var pool = PersonaQuotes(d.PersonalityId);
        return pool[(int)(new SimRandom(seed ^ (ulong)d.Name.GetHashCode()).NextUInt64() % (ulong)pool.Length)];
    }
    /// <summary>KO 결착이면 종료 시 쓰러진 패자(패자 인덱스)를 줌인 — 리플레이 극적 연출. 무승부/판정은 −1.</summary>
    private static int ReplayEndFocus(MatchResult res) => res.Reason == "KO" && res.Winner >= 0 ? 1 - res.Winner : -1;

    public string WatchJson(int idx)
    {
        var e = idx < 0 ? _matchLog.LastOrDefault() : _matchLog.FirstOrDefault(x => x.Idx == idx);
        if (e == null) return Err("남은 경기 기록이 없다");
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(e.DefA, e.DefB, e.Seed, events, frames);
        ViewerExport.WriteDoc(e.DefA, e.DefB, e.Seed, res, frames, events, "viewer.json",
            EndowOf(e.AId, e.DefA), EndowOf(e.BId, e.DefB),
            ReplayQuote(e.DefA, e.Seed), ReplayQuote(e.DefB, e.Seed + 1), ReplayEndFocus(res));
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
        if (pos < 0 || pos >= _archive.Count) return Err("보관된 경기가 없다");
        var e = _archive[pos].Entry;
        var events = new List<SimEvent>(); var frames = new List<ReplayFrame>();
        var res = new MatchSim().Run(e.DefA, e.DefB, e.Seed, events, frames);
        ViewerExport.WriteDoc(e.DefA, e.DefB, e.Seed, res, frames, events, "viewer.json",
            EndowOf(e.AId, e.DefA), EndowOf(e.BId, e.DefB),
            ReplayQuote(e.DefA, e.Seed), ReplayQuote(e.DefB, e.Seed + 1), ReplayEndFocus(res));
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
        if (g == null) return Err("내 모리튜리가 아니다");
        string tid = tacticId.StartsWith("TAC_") ? tacticId : "TAC_" + tacticId;
        if (!g.TacticPool.Contains(tid)) return Err("익히지 않은 전술이다");
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
        bool exec = format == "execution";  // {skull} 처형전 — 패자는 죽을 수 있다. 보상도 크다
        _lastInjuries.Clear();
        _lastFixNote = null; _lastFixBad = false;
        _lastHype = MathF.Round(((A.Popularity + B.Popularity) * (exec ? 2f : isEvent ? 1.5f : 1f) + (A.Fame + B.Fame) * 0.1f)
                    * UnrestHypeMult);   // 경기 관심도(#5) — [13] 불안한 시대일수록 군중은 목마르다(최대 +15%)
        var (defA, defB) = BuildDefs(A, B);
        if (_liveSwitches is { } li)   // 라니스타 실시간 개입(라이브 정산): 관전 중 예약한 전술 전환을 결정 def에 주입
        {
            if (A.Id == li.FighterId) defA = defA with { TacticSwitches = li.Switches };
            else if (B.Id == li.FighterId) defB = defB with { TacticSwitches = li.Switches };
            _liveSwitches = null;
        }
        if (_liveSwitches2 is { } li2)   // 내전 2번째 조종 대상의 전환
        {
            if (A.Id == li2.FighterId) defA = defA with { TacticSwitches = li2.Switches };
            else if (B.Id == li2.FighterId) defB = defB with { TacticSwitches = li2.Switches };
            _liveSwitches2 = null;
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
                _story.Add((round, "revenge", $"R{round} {{swords}} 복수! {win.Name}이(가) 숙적 {lose.Name}에게 설욕 (그간 {prior.Wins}승 {prior.Losses}패)"));
            else if (upset)
                _story.Add((round, "upset", $"R{round} ★ 이변! {win.Name}이(가) 상위 {lose.Name}을(를) 격파"));
            if (comeback)
                _story.Add((round, "comeback", $"R{round} {{flame}} 대역전! {win.Name} 사선(HP{winStats.MinHpPct * 100:F0}%)에서 {lose.Name} 제압"));

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
                      * (1f + 0.08f * self.MPay)   // 협상 마스터리 = 출전료 협상력. 처형전 ×3(목숨값)
                      * UnrestIncomeMult;          // [13] 반란 지수 — 시국 불안 = 세금·검문(최대 −10%)
            bool mainEvent = _lastHype >= MainEventHype;   // {star} 인기(#3) 페이오프: 흥행 대박 = 메인 이벤트 출전료 가산
            if (mainEvent) own *= 1.2f;
            bool staged = _prepKind == "show" && self.Id == _prepId;   // 방침: 무대를 띄운다 — 출전료 가산
            if (staged) own *= 1.15f;
            var notes = new List<string> { $"출전료 +{own:F0}" + (mainEvent ? " {{star}}메인이벤트" : "") + (staged ? " (무대 연출)" : "") };
            if (self.Popularity >= SponsorPopReq)   // {handshake} 인기(#3) 페이오프: 스타는 스폰서를 부른다 — 인기 비례 후원금
            {
                float spon = MathF.Round(self.Popularity * SponsorScale * IncomeMult);
                own += spon; notes.Add($"스폰서 후원 +{spon:F0}");
            }
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

        // 신예 데뷔(#1·#4) — 이 경기가 첫 경기인 모리튜리를 신문에 올린다(기록 반영 전 판정)
        bool aDebut = A.CW + A.CL + A.CD == 0, bDebut = B.CW + B.CL + B.CD == 0;
        // 순위/커리어 + 관계 + 감정 (경기 인덱스 파생 스트림 = 미드시즌 재개 결정론)
        Record(A, B, res, standing: !isEvent);
        if (aDebut) _story.Add((round, "debut", $"{{sprout}} 데뷔 — {A.Name}({LudusNameOf(A.LudusId)}), 처음으로 모래를 밟다 ({WpnKo(A.WeaponId)} · {PerKo(A.PersonalityId)})"));
        if (bDebut) _story.Add((round, "debut", $"{{sprout}} 데뷔 — {B.Name}({LudusNameOf(B.LudusId)}), 처음으로 모래를 밟다 ({WpnKo(B.WeaponId)} · {PerKo(B.PersonalityId)})"));
        // 황제의 특명 진행: 지목 상대 격파(beat)는 여기서, 연승/N승은 CheckEdict에서
        if (_edict is { Type: "beat" } && !_edictDone && win != null && win.IsPlayer && lose?.Id == _edict.TargetId)
            MarkEdictDone();
        CheckEdict();
        _ledger.RecordMatch(A.Id, B.Id, res.Winner, ko, res.StatsA.MinHpPct, res.StatsB.MinHpPct);
        // 원한 = 관계(감정 아님): KO패한 복수심 성격은 그 상대에게 원한을 품는다(원수로 향하는 추가 affinity). 굴욕이 클수록 깊게.
        if (ko && win != null && lose != null && EmotionGen.IsVengeful(lose.PersonalityId))
        {
            _ledger.DeepenGrudge(lose.Id, win.Id, 30f);
            lose.GrudgeCount++;
            _story.Add((round, "grudge", $"{{swords}} {lose.Name}, {win.Name}에게 원한을 품었다 — 이 치욕은 잊지 않는다"));
        }

        // 승부조작 정산: 가담 예약된 선수가 이 경기에 나섰다면 — 실제로 던졌는가로 성패가 갈린다(선입금 없음)
        if (_fixFighterId != null && (A.Id == _fixFighterId || B.Id == _fixFighterId))
        {
            var fx = A.Id == _fixFighterId ? A : B;
            var fxStats = A.Id == _fixFighterId ? res.StatsA : res.StatsB;
            bool fxLost = res.Winner >= 0 && win != fx;   // 결정적 패배만 이행(무승부는 던진 게 아니다)
            var frng = new SimRandom(SeasonSeed ^ 0xF15E_D000UL + (ulong)_matchIdx * 7UL);
            if (fxLost)
            {
                _gold += _fixReward;
                // 던진 티가 날수록 발각↑: 처절하게 진(KO·빈사) 패배는 진짜 같아 덜 의심받고, 맥없는 판정패는 수상하다
                bool convincing = ko || fxStats.MinHpPct <= 0.15f;
                if (frng.Roll(convincing ? 0.15f : 0.40f))
                {
                    _ludusRep = MathF.Max(0f, _ludusRep - 40f); Patron(-25f); _favor = Math.Max(0, _favor - 1);
                    _lastFixNote = $"{{dice}} 승부조작 발각! {fx.Name}의 석연찮은 패배가 들통났다 — 골드 +{_fixReward:F0}이나 명성 −40·후원 −25·총애 −1"; _lastFixBad = true;
                }
                else { _lastFixNote = $"{{dice}} 검은 거래 완수 — {fx.Name}이(가) 조용히 던졌다. 골드 +{_fixReward:F0} (아무도 눈치채지 못했다)"; _lastFixBad = false; }
            }
            else   // 이기거나 비겼다 — 약속을 어겼다
            {
                _ludusRep = MathF.Max(0f, _ludusRep - 30f); Patron(-20f); DebtTxn("검은 인장의 협박 채무", _fixReward);
                _lastFixNote = $"{{dice}} 약속을 어겼다 — {fx.Name}이(가) 지지 않았다. 뒷돈의 주인이 이를 간다: 명성 −30·후원 −20·협박 채무 +{_fixReward:F0}"; _lastFixBad = true;
            }
            _story.Add((round, "fix", _lastFixNote));
            _fixFighterId = null; _fixReward = 0f;
        }
        ProcessFatigue(A, res.StatsA, res, 0, round);   // 피로 누적(메타) + 부상 판정(드묾, 부상만 스탯 영향)
        ProcessFatigue(B, res.StatsB, res, 1, round);
        var emoRng = new SimRandom(SeasonSeed ^ 0x5EA5_04EDUL + (ulong)_matchIdx * 17UL);
        string? eA = EmotionGen.Roll(emoRng, res.Winner, 0, ko, res.StatsA.MinHpPct, A.Pers);
        string? eB = EmotionGen.Roll(emoRng, res.Winner, 1, ko, res.StatsB.MinHpPct, B.Pers);
        if (eA != null) { A.PendingEmotions.Add(eA); A.EmoHistory[eA] = A.EmoHistory.GetValueOrDefault(eA) + 1; _emoGen++; }
        if (eB != null) { B.PendingEmotions.Add(eB); B.EmoHistory[eB] = B.EmoHistory.GetValueOrDefault(eB) + 1; _emoGen++; }

        // 성장: 경기 자동 소량 + 3경기당 훈련 포인트. 방침(C1): 담금질=성장 2회, 아낀다=성장 없음
        var growRng = new SimRandom(SeasonSeed ^ 0x6120_6120UL + (ulong)_matchIdx * 13UL);
        string? growA = _prepKind == "rest" && A.Id == _prepId ? null : Grow(A, growRng);
        string? growB = _prepKind == "rest" && B.Id == _prepId ? null : Grow(B, growRng);
        if (_prepKind == "forge")
        {
            if (A.Id == _prepId && Grow(A, growRng) is { } fa) growA = growA != null ? $"{growA}·{fa}" : fa;
            else if (B.Id == _prepId && Grow(B, growRng) is { } fb) growB = growB != null ? $"{growB}·{fb}" : fb;
        }
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
            _story.Add((round, "greatest", $"{{play}} 명경기 — {A.Name} vs {B.Name} (드라마 {_lastDrama:F1}) 보관함 등재"));
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
        _lastFates.Clear(); _lastExec = null;
        var fRng = new SimRandom(SeasonSeed ^ 0xFA7E_FA7EUL + (ulong)_matchIdx * 61UL);
        void Fate(int r, string k, string note) { _lastFates.Add(note); _story.Add((r, k, note)); }
        if (win != null && lose != null)
        {
            bool loserBrutal = ko || loseStats.MinHpPct <= 0.15f;
            void Death()   // 사망 처리 공통: 명전 등재·대진 정리·공석 승계
            {
                win.Executions++;   // 모리튜리 기록(#2): 통산 처형 — 이 승자가 상대를 저승으로 보냈다
                _cast.Remove(lose); _ledger.RemoveFighter(lose.Id);
                for (int i = _schedule.Count - 1; i >= _cursor; i--)   // 남은 대진에서 제거
                    if (_schedule[i].A == lose.Id || _schedule[i].B == lose.Id) _schedule.RemoveAt(i);
                _hall.Add(MakeHall(lose, $"{lose.CW}-{lose.CL}-{lose.CD} {{coffin}}전사", _seasonNo));
                Fate(round, "death", $"{{coffin}} {lose.Name}({lose.Age}세) — 모래 위에서 숨을 거두다. 모리튜리로 죽다");
                if (!lose.IsPlayer)
                {
                    var rk = SpawnRookieCore(fRng, lose.LudusId, lose.Division, 1);
                    _story.Add((round, "recruit", $"{{sprout}} {LudusNameOf(lose.LudusId)}, 공석에 신인 {rk.Name} 영입 (다음 시즌 출전)"));
                }
            }
            bool killed = false;
            if (exec && loserBrutal)
            {
                // {skull} 엄지 판정: 기본 25%에서 군중의 사랑·명경기의 감동이 자비를, 오만이 냉대를 부른다 (총량은 25% 근방 유지)
                float deathP = 0.25f;
                var factors = new List<string>();
                if (lose.Popularity >= 40f) { deathP -= 0.10f; factors.Add("군중의 연인"); }
                else if (lose.Popularity >= 20f) { deathP -= 0.05f; factors.Add("관중의 호감"); }
                if (_lastDrama >= 4f) { deathP -= 0.08f; factors.Add("명경기의 감동"); }
                else if (_lastDrama >= 2f) { deathP -= 0.04f; factors.Add("잘 싸운 자"); }
                if (loseStats.Taunted) { deathP += 0.06f; factors.Add("오만의 대가"); }
                deathP = Math.Clamp(deathP, 0.05f, 0.45f);
                killed = fRng.Roll(deathP);
                _lastExec = new ExecVerdict(lose.Name, (int)MathF.Round(deathP * 100f), !killed,
                    factors.Count > 0 ? string.Join(" · ", factors) : "군중은 무심하다");
                if (killed) Death();
                else Fate(round, "mercy", $"{{thumb}} 황제의 자비 — {lose.Name}, 엄지가 하늘을 향했다. 오늘은 산다");
            }
            else if (kind == "regular" && loserBrutal && lose.SeasonBrutals >= 2 && fRng.Roll(0.02f))
            {
                // {coffin} 정규 경기 사망 — 격전 누적자만, 드묾. 컵 대진은 보호
                Death(); killed = true;
            }
            // {skull} 영구 부상(#6) — 부위별 코어 스탯 영구 감소. 격전 한정·드묾, 확률 의무실이 완화(일시부상과 별개, 자연치유 안 됨).
            bool permInjured = false;
            if (!killed && loserBrutal)
            {
                float pInj = 0.05f * (lose.SeasonBrutals >= 2 ? 1.3f : 1f);
                if (lose.IsPlayer) pInj *= 1f - 0.25f * (_medicalLv - 1);   // 의무실 Lv → 영구부상률 감소
                pInj *= 1f - 0.10f * lose.MRecover;                          // 회복력 마스터리
                if (fRng.Roll(pInj)) permInjured = PermInjure(lose, round, fRng);
            }
            // 전사 시엔 사망만 남긴다(#9) — 죽은 자에게 성격 변화 등 다른 이벤트가 겹치지 않게
            if (!killed && !permInjured && loserBrutal && lose.SeasonBrutals >= 2
                // {masks} 트라우마 성격 변화 — 마음의 상처 '이력'이 확률을 키운다(W10a): 무상처 2% ~ 상처 깊음 8%
                // (트라우마 감정 + 원한 관계 누적 = 사선을 넘은 패배의 누적 흉터)
                && fRng.Roll(0.02f + 0.02f * Math.Min(3,
                    lose.EmoHistory.GetValueOrDefault(EmotionTable.Trauma) + lose.GrudgeCount)))
            {
                string? shift = lose.PersonalityId switch
                {
                    "PER_RECKLESS" => "PER_WARY", "PER_BOLD" => "PER_CALM", "PER_ARROGANT" => "PER_WARY",
                    "PER_SHOWMAN" => "PER_CALM", "PER_CRUEL" => "PER_WARY", "PER_OPPORTUNIST" => "PER_WARY",
                    "PER_CALM" => "PER_WARY", "PER_WARY" => "PER_COWARD", _ => null,
                };
                if (shift != null)
                {
                    int scars = lose.EmoHistory.GetValueOrDefault(EmotionTable.Trauma);
                    string from = lose.PersonalityId;
                    lose.PersonalityId = shift;
                    Fate(round, "persona", $"{{masks}} {lose.Name} — 사선을 넘은 패배가 사람을 바꿨다{(scars >= 2 ? $" (쌓인 상처 {scars}번)" : "")} ({PerKo(from)} → {PerKo(shift)})");
                }
            }
            // {masks} 자만의 길(W10a): 승리와 자만이 쌓인 자는 오만해진다 — 감정 이력 → 성격 드리프트
            if (win != null && _cast.Contains(win) && win.PersonalityId != "PER_ARROGANT"
                && win.EmoHistory.GetValueOrDefault(EmotionTable.Hubris) + win.EmoHistory.GetValueOrDefault(EmotionTable.Confident) >= 4
                && fRng.Roll(0.08f))
            {
                string from = win.PersonalityId;
                win.PersonalityId = "PER_ARROGANT";
                Fate(round, "persona", $"{{masks}} {win.Name} — 연이은 영광이 그를 바꿨다: 오만해졌다 ({PerKo(from)} → 오만)");
            }
            // {star} 각성 — 대역전·이변의 순간, 한계가 열린다 (승자·30세 이하)
            if (_cast.Contains(win) && (comeback || upset) && win.Age <= 30 && fRng.Roll(0.04f))
            {
                win.PotentialBudget += 20f;
                win.Stats = WithAxis(win.Stats, (int)(fRng.NextFloat01() * 6), 2f);
                win.Stats = WithAxis(win.Stats, (int)(fRng.NextFloat01() * 6), 2f);
                Fate(round, "awakening", $"{{star}} {win.Name} — 각성! 그 승리가 한계를 열었다 (상한 {win.PotentialBudget:F0})");
                string? bloom = win.PersonalityId switch { "PER_COWARD" => "PER_BOLD", "PER_WARY" => "PER_BOLD", _ => null };
                if (bloom != null && fRng.Roll(0.30f))
                { string from = win.PersonalityId; win.PersonalityId = bloom; Fate(round, "persona", $"{{masks}} {win.Name} — 성격 개화: 대담해졌다 ({PerKo(from)} → {PerKo(bloom)})"); }
            }
        }
        // {scales} 강제 트레이드오프 — 몸의 적응(아주 드묾, 승패 무관)
        foreach (var g in new[] { A, B })
            if (_cast.Contains(g) && fRng.Roll(0.008f))
            {
                int a = (int)(fRng.NextFloat01() * 6), b = (a + 1 + (int)(fRng.NextFloat01() * 5)) % 6;
                g.Stats = WithAxis(g.Stats, a, -3f); g.Stats = WithAxis(g.Stats, b, 3f);
                Fate(round, "tradeoff", $"{{scales}} {g.Name} — 몸의 적응: {AxisNames[a]} −3 → {AxisNames[b]} +3");
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
                _story.Add((round, "injury", $"{{medic}} 부상! {g.Name} — 향후 {dur}경기 실효 스탯 저하"));
            }
        }
    }

    /// <summary>영구 부상(#6) 부위 카탈로그 — 부위별 코어 스탯 영구 감소. Pts는 음수(감소량). Axis2=−1이면 단일 축.</summary>
    private static readonly (string Id, string Name, string Icon, int Axis, float Pts, int Axis2, float Pts2)[] PermInjuryKinds =
    {
        ("arm",  "오른팔 부상",  "{fist}", 0, -7f, 4, -4f),   // 팔 → ATK + ASPD
        ("ribs", "갈비뼈 골절",  "{medic}", 2, -8f, -1, 0f),    // 갈비뼈 → HP(내구)
        ("eye",  "한쪽 눈 실명", "{eye}", 5, -9f, -1, 0f),    // 눈 → RCT(반응)
        ("leg",  "다리 부상",    "{medic}", 3, -8f, -1, 0f),    // 다리 → SPD(이속)
    };
    /// <summary>영구 부상의 스탯 변경점 표기 — 예: "ATK −7 · ASPD −4".</summary>
    private static string PermInjuryStatText(int axis, float pts, int axis2, float pts2)
    {
        string One(int a, float p) => $"{AxisNames[a].ToUpperInvariant()} {(p < 0 ? "−" : "+")}{Math.Abs(p):0}";
        return axis2 >= 0 ? $"{One(axis, pts)} · {One(axis2, pts2)}" : One(axis, pts);
    }
    private sealed record PermInjuryInfo(string Id, string Name, string Stat);
    /// <summary>선수의 영구 부상 목록 — 부위명 + 스탯 저하 명시(상세 창·수술 부위 선택용).</summary>
    private static PermInjuryInfo[] PermInjuryInfos(Gladiator g) => g.PermInjuries
        .Select(id => PermInjuryKinds.FirstOrDefault(k => k.Id == id) is { Id: { } } k
            ? new PermInjuryInfo(id, k.Name, PermInjuryStatText(k.Axis, k.Pts, k.Axis2, k.Pts2))
            : new PermInjuryInfo(id, id, ""))
        .ToArray();

    /// <summary>영구 부상 부여 — 부위 무작위(중복 부위 제외), 코어 스탯 + 상한 동시 감소(재훈련 불가=진짜 영구). 이미 만신창이면 false.</summary>
    private bool PermInjure(Gladiator g, int round, SimRandom rng)
    {
        var pool = PermInjuryKinds.Where(k => !g.PermInjuries.Contains(k.Id)).ToArray();
        if (pool.Length == 0) return false;                          // 4부위 다 다침 — 더는 없음
        var k = pool[Math.Min(pool.Length - 1, (int)(rng.NextFloat01() * pool.Length))];
        float removed = -k.Pts + (k.Axis2 >= 0 ? -k.Pts2 : 0f);      // 제거된 총 포인트(양수)
        g.Stats = WithAxis(g.Stats, k.Axis, k.Pts);
        if (k.Axis2 >= 0) g.Stats = WithAxis(g.Stats, k.Axis2, k.Pts2);
        g.PotentialBudget = MathF.Max(MinPotentialBudget, g.PotentialBudget - removed);   // 상한도 깎아 영구화
        g.PermInjuries.Add(k.Id);
        string note = $"{k.Icon} 영구 부상! {g.Name} — {k.Name} (다시는 예전 같지 않다)";
        _lastFates.Add(note); _story.Add((round, "perm_injury", note));
        return true;
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
        if (g.IsPlayer) { g.TrainingPoints += pts; return pts; }      // 라니스타이 분배
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

    /// <summary>경기 def 조립(잠정 시뮬·정산 공용 — 동일성 필수).</summary>
    private (FighterDef defA, FighterDef defB) BuildDefs(Gladiator A, Gladiator B)
    {
        var relA = _ledger.Get(A.Id, B.Id).Classify(A.PersonalityId);
        var relB = _ledger.Get(B.Id, A.Id).Classify(B.PersonalityId);
        var defA = ToDef(A, relA, Intensity(A.Id, B.Id));
        var defB = ToDef(B, relB, Intensity(B.Id, A.Id));
        return (defA, defB);
    }

    private FighterDef ToDef(Gladiator g, RelationType? rel, float intensity)
    {
        // 부상 중에만 실효 스탯 소폭 하락(반응·속도 위주 — 코어 매트릭스 ATK/DEF/HP 불변, 회복성). 평상 피로는 무영향.
        var stats = g.InjuryMatches > 0
            ? g.Stats with { Rct = g.Stats.Rct * 0.90f, Aspd = g.Stats.Aspd * 0.92f, Spd = g.Stats.Spd * 0.94f }
            : g.Stats;
        // 스킬(T12) = 장착형 특성 — 특성과 같은 파이프로 def에 합류(잠정·정산·재관전 스냅샷 전부 일관)
        var tr = g.SkillIds.Length > 0 ? g.TraitIds.Concat(g.SkillIds).ToArray() : g.TraitIds;
        return new(g.Name, stats, g.WeaponId, g.TacticId, g.PersonalityId,
            tr.Length > 0 ? tr : null,
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
                var (dA, dB) = BuildDefs(_cast[i], _cast[j]);
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
                var (dA, dB) = BuildDefs(p.A, p.B);
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

    // 커서 경기 결과 분포(승자 × 방식) — 승자만이 아니라 "어떻게 이길지"까지 배당에 반영. 시뮬 15판 캐시.
    private readonly record struct BetOutcomes(float A, float B, float AKo, float ADec, float BKo, float BDec);
    private int _oddsCursor = -1; private float _oddsProbA; private BetOutcomes _oddsOut;
    private float _seasonBetNet;                              // 시즌 베팅 수지(결산 표시)
    private int _gauntletStage, _gauntletWins;                // 황제의 초청전: 0=미편성 1=편성됨 · 승수
    private int _betStreak;                                   // 연속 적중(스트릭 보너스 — 3연속부터 배당 +10%)

    /// <summary>커서 경기의 결과 분포(시뮬 15판, 캐시) — 승자×방식(KO/판정) 6종 확률. 다른 시드 스트림이라 결과 유출 없음.</summary>
    private BetOutcomes CursorOutcomes()
    {
        if (_oddsCursor == _cursor) return _oddsOut;
        var s = _schedule[_cursor];
        var A = ById(s.A); var B = ById(s.B);
        var tacRng = new SimRandom(SeasonSeed ^ 0x7AC7_1C5EUL + (ulong)_matchIdx * 31UL);   // PlayNext와 동일 소비 순서
        string tA = A.IsPlayer ? A.TacticId : SelectTacticAi(A, B, tacRng);
        string tB = B.IsPlayer ? B.TacticId : SelectTacticAi(B, A, tacRng);
        var (dA, dB) = BuildDefs(A, B);
        dA = dA with { TacticsId = tA }; dB = dB with { TacticsId = tB };
        const int K = 25;   // 조합(승자×방식) 해상도를 위해 표본 확대
        ulong seed = SeasonSeed ^ 0xBE77_0DD5UL + (ulong)_matchIdx * 977UL;
        int aKo = 0, aDec = 0, bKo = 0, bDec = 0;
        for (int t = 1; t <= K; t++)
        {
            var r = new MatchSim().Run(dA, dB, seed + (ulong)t * 104729UL);
            if (r.Winner == 0) { if (r.Reason == "KO") aKo++; else aDec++; }
            else if (r.Winner == 1) { if (r.Reason == "KO") bKo++; else bDec++; }
        }
        int winsA = aKo + aDec, winsB = bKo + bDec, decided = winsA + winsB;
        // 승 확률(전체 K 대비, 라플라스). 방식 = 승리 조건부(P(승)×P(방식|승)) → 언제나 승 확률보다 작다
        // = 방식 배당이 항상 승 배당보다 크다(같은 배당 방지). 상한 없음 → 희귀 방식은 고배당.
        float pA = (winsA + 0.5f) / (K + 1f), pB = (winsB + 0.5f) / (K + 1f);
        float aKoC = (aKo + 0.5f) / (winsA + 1f), aDecC = (aDec + 0.5f) / (winsA + 1f);
        float bKoC = (bKo + 0.5f) / (winsB + 1f), bDecC = (bDec + 0.5f) / (winsB + 1f);
        _oddsProbA = Math.Clamp((winsA + 1f) / (decided + 2f), 0.05f, 0.95f);   // VS 표시용 승률(decided 기준)
        _oddsOut = new BetOutcomes(pA, pB, pA * aKoC, pA * aDecC, pB * bKoC, pB * bDecC);
        _oddsCursor = _cursor;
        return _oddsOut;
    }
    private float CursorProbA() { CursorOutcomes(); return _oddsProbA; }

    /// <summary>베팅 종류별 배당: 0=A승 1=B승 2=A KO승 3=A 판정승 4=B KO승 5=B 판정승. 조합 확률에서 산정.</summary>
    private float BetOddsFor(int side)
    {
        var o = CursorOutcomes();
        float p = side switch { 0 => o.A, 1 => o.B, 2 => o.AKo, 3 => o.ADec, 4 => o.BKo, 5 => o.BDec, _ => 0.5f };
        return BetOdds(p);
    }

    private static void Record(Gladiator a, Gladiator b, MatchResult r, bool standing)
    {
        if (r.Winner == 0) { a.CW++; b.CL++; if (r.Reason == "KO") a.CKoW++; if (standing) { a.W++; b.L++; a.Streak = a.Streak >= 0 ? a.Streak + 1 : 1; b.Streak = b.Streak <= 0 ? b.Streak - 1 : -1; } }
        else if (r.Winner == 1) { b.CW++; a.CL++; if (r.Reason == "KO") b.CKoW++; if (standing) { b.W++; a.L++; b.Streak = b.Streak >= 0 ? b.Streak + 1 : 1; a.Streak = a.Streak <= 0 ? a.Streak - 1 : -1; } }
        else { a.CD++; b.CD++; if (standing) { a.D++; b.D++; a.Streak = 0; b.Streak = 0; } }
        // 모리튜리 기록(#2): 최다 연승·통산 경기시간·피해량 누적 (은퇴 후에도 GladRec/HallRec로 보존)
        a.BestStreak = Math.Max(a.BestStreak, a.Streak); b.BestStreak = Math.Max(b.BestStreak, b.Streak);
        a.TotalMatchTime += r.DurationSec; b.TotalMatchTime += r.DurationSec;
        a.TotalDamage += r.StatsA.DamageDealt; b.TotalDamage += r.StatsB.DamageDealt;
        a.TotalDamageTaken += r.StatsB.DamageDealt; b.TotalDamageTaken += r.StatsA.DamageDealt;   // 받은 피해 = 상대가 가한 피해(1:1)
        a.TotalBlocks += r.StatsA.Blocks; b.TotalBlocks += r.StatsB.Blocks;
        a.TotalDodges += r.StatsA.Dodges; b.TotalDodges += r.StatsB.Dodges;
    }

    // ── 라니스타 액션 API ──

    /// <summary>뽑기: 재화(또는 무료권) 소모 → 후보 3명(마스킹). 기존 후보는 소멸(포기).</summary>
    /// <summary>영입 뽑기. premium({coin}300) = 노예 시장의 귀한 물건: 천부 굴림 +2(상급 확률↑) + 전 후보 천부 등급 공개.</summary>
    public string GachaJson(bool premium = false)
    {
        if (_playerless) return Err("CLI 모드");
        if (_cast.Count(g => g.IsPlayer) >= RosterCap) return Err($"로스터 가득참 (상한 {RosterCap} — 숙소 증축 필요)");
        if (premium)
        {
            if (_gold < PremiumGachaCost) return Err($"잔고 부족 (프리미엄 영입 {PremiumGachaCost:F0})");
            _gold -= PremiumGachaCost;
        }
        else if (_freeGachas > 0) _freeGachas--;
        else if (_gold >= EffGachaCost) _gold -= EffGachaCost;   // 원로원 인맥 특전 = 뽑기 할인
        else return Err($"잔고 부족 (뽑기 {EffGachaCost:F0})");

        _candidates.Clear(); _lastReveal.Clear();
        var rng = new SimRandom(_worldSeed ^ 0x6ACA_6ACAUL + (ulong)(++_gachaCount) * 2654435761UL);
        var usedNames = _cast.Select(g => g.Name).Concat(_candidates.Select(c => c.Name)).ToHashSet();
        var wpns = WeaponTable.All.Select(w => w.Id).ToArray();
        var pers = PersonalityTable.All.Select(p => p.Id).ToArray();
        int scouting = 1 + LudusTier() + (_mentorName != null ? 1 : 0) + _scoutLevel   // 등급 + 스승 안목 + 스카우터 유산 = 원석 품질
                     + (premium ? 2 : 0);                                              // 프리미엄 = 시장 안쪽의 물건
        int nCand = UnrestStageIdx >= 2 ? 2 : 3;   // [13] 폭동+ 국면 = 노예 시장 위축(후보 2명)
        for (int i = 0; i < nCand; i++)
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
        if (premium)   // 프리미엄 = 상인이 혈통 문서를 내민다: 전 후보 천부 등급 공개
        {
            for (int ci = 0; ci < _candidates.Count; ci++)
                _candHints[ci] = new List<string> { "천부 " + ViewerExport.TalentName(_candidates[ci].Talent) };
            _story.Add((0, "recruit", $"{{gem}} 프리미엄 영입 — 상인이 혈통 문서와 함께 귀한 물건을 내놓았다 ({{coin}}{PremiumGachaCost:F0})"));
        }
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
        return $"모리튜리{used.Count + 1}";
    }

    /// <summary>영입: 후보 택1 → 전체 공개 + 로스터 편입 (시즌 중이면 다음 시즌부터 출전).</summary>
    public string RecruitJson(int idx)
    {
        if (idx < 0 || idx >= _candidates.Count) return Err("고를 후보가 없다");
        if (_cast.Count(g => g.IsPlayer) >= RosterCap) return Err("자리가 없다 — 숙소가 찼다");
        var g = _candidates[idx];
        // 미선택 후보 공개 + 일부는 라이벌 루두스로 편입(#8) — 지나친 원석이 적이 되어 돌아온다
        _lastReveal.Clear();
        var others = _candidates.Where((_, i) => i != idx).ToList();
        var rRng = new SimRandom(_worldSeed ^ 0x0A11_5EED + (ulong)_gachaCount * 17UL);
        foreach (var o in others)
        {
            string? joinedRival = null;
            // 육성 개성(W10b): 육성 루두스가 있으면 놓친 원석을 더 자주(55%)·우선적으로 주워간다
            var rivalsAll = ActiveRivalLudi.ToList();
            var youthLudi = rivalsAll.Where(r => r.Persona == "youth").ToList();
            float joinP = youthLudi.Count > 0 ? 0.55f : 0.40f;
            if (!_playerless && _cast.Count(x => !x.IsPlayer) < 15 && rRng.Roll(joinP))   // AI 12인 기준 +3 여유
            {
                var rivals = youthLudi.Count > 0 && rRng.Roll(0.6f) ? youthLudi : rivalsAll;
                var rl = rivals.Count > 0 ? rivals[(int)(rRng.NextUInt64() % (ulong)rivals.Count)] : default;
                o.IsPlayer = false; o.LudusId = rl.Id ?? "RIV"; o.Division = 2;
                _cast.Add(o); joinedRival = rl.Name ?? "라이벌 검투소";
                _story.Add((0, "recruit", $"{{person}} 놓친 원석 — {o.Name}이(가) {joinedRival}에 합류했다"));
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
            var peers = _cast.Where(x => x.Division == g.Division && x.Id != g.Id).ToList();
            int n = _schedule.Skip(_cursor).Where(s => s.Kind == "regular").Select(s => s.Round).Distinct().Count();
            int span = _schedule.Count - _cursor;
            // 남은 일정에 고르게 흩뿌린다 — 끝에 붙이면 신입의 데뷔가 시즌 막바지로 밀린다.
            // 라운드 번호로 자리를 찾지 않는다: 구버전이 꼬리에 몰아둔 합류전이 같은 번호를 달고 있어 또 끝으로 간다.
            for (int k = 0; k < n && peers.Count > 0; k++)
            {
                var p = peers[(int)(jRng.NextUInt64() % (ulong)peers.Count)];
                int at = Math.Min(_schedule.Count, _cursor + 1 + (int)((k + 0.5) * span / n) + k);   // +k = 앞서 삽입된 만큼 밀림
                int round = _schedule[Math.Min(at, _schedule.Count - 1)].Round;                      // 이웃과 같은 라운드로 표기
                _schedule.Insert(at, new SchedRec(round, g.Id, p.Id, false, 0f));
                if (_betCursor >= at) _betCursor++;             // 삽입으로 밀린 베팅 대상 보정
                if (_oddsCursor >= at) _oddsCursor = -1;        // 배당 캐시 무효화
                joined++;
            }
        }
        if (_mentorName != null)      // 스승의 지도(혈통 유산) — 신인의 그릇이 넓어진다
        {
            g.PotentialBudget += 10f;
            _story.Add((0, "mentor", $"{{scroll}} 스승 {_mentorName}의 지도 — {g.Name} 잠재력 +10 (상한 {g.PotentialBudget:F0})"));
        }
        if (g.Talent == TalentGrade.Caesar) Unlock("caesar");
        _story.Add((0, "recruit", $"{{scroll}} 영입! {g.Name} ({ViewerExport.TalentName(g.Talent)}·{g.Age}세) 루두스 합류" +
                                   (SeasonActive ? (joined > 0 ? $" — 중도 투입: 합류전 {joined}경기 편성" : " — 다음 시즌부터 출전") : "")));
        MaybeSpawnStoryEvent(afterMatch: false);   // [13] 서막: 첫 영입 → 첫 방문자(S5)
        SaveWorld();
        if (_interactive) WriteSeasonJson();
        return StateJson();
    }

    /// <summary>훈련: 포인트 1을 축에 분배 (axis: Atk/Def/Hp/Spd/Aspd/Rct).</summary>
    public string TrainJson(string fighterId, string axis)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        if (g.TrainingPoints <= 0) return Err("훈련 포인트가 없다");
        int a = Array.IndexOf(AxisNames, axis);
        if (a < 0) return Err("그런 단련 축이 없다");
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
        if (g == null) return Err("내 모리튜리가 아니다");
        int cost = BreakthroughCost(g);
        if (_glory < cost) return Err($"영광 부족 (돌파 {cost} 필요)");
        _glory -= cost;
        g.PotentialBudget += 25f;
        _story.Add((0, "breakthrough", $"{{impact}} 잠재력 돌파! {g.Name} — 상한 {g.PotentialBudget:F0} (영광 −{cost})"));
        SaveWorld();
        return StateJson();
    }

    /// <summary>마스터리 수련: 훈련 포인트를 비스탯 성장에 투자(상한 찬 선수의 성장 여지).
    /// track: grit(투혼=피로저항)/recover(회복력=부상저항)/show(흥행=인기)/pay(협상=출전료). 비용=현재Lv+1, 최대 5.</summary>
    public string MasteryJson(string fighterId, string track)
    {
        var g = _cast.FirstOrDefault(x => x.Id == fighterId && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        int lv = track switch { "grit" => g.MGrit, "recover" => g.MRecover, "show" => g.MShow, "pay" => g.MPay, _ => -1 };
        if (lv < 0) return Err("그런 수련은 없다");
        if (lv >= 5) return Err("더 갈고닦을 것이 없다 (5)");
        int cost = lv + 1;
        if (g.TrainingPoints < cost) return Err($"훈련 포인트 부족 ({cost} 필요)");
        g.TrainingPoints -= cost;
        switch (track) { case "grit": g.MGrit++; break; case "recover": g.MRecover++; break;
                         case "show": g.MShow++; break; default: g.MPay++; break; }
        SaveWorld();
        return StateJson();
    }

    /// <summary>개명(라니스타 명명권): kind=ludus → 내 루두스 / kind=fighter+id → 내 모리튜리.
    /// 모리튜리 개명 시 과거 기록(챔피언·명전·컵)의 이름도 승계(업적이 이름을 따라간다).</summary>
    public string RenameJson(string kind, string id, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 14) return Err("이름은 1~14자");
        if (kind == "ludus") { _ludusName = name; SaveWorld(); return StateJson(); }

        var g = _cast.FirstOrDefault(x => x.Id == id && x.IsPlayer);
        if (g == null) return Err("내 모리튜리가 아니다");
        if (_cast.Any(x => x != g && x.Name == name)) return Err("이미 그 이름을 쓰는 자가 있다");
        string old = g.Name;
        g.Name = name;
        for (int i = 0; i < _champions.Count; i++) if (_champions[i].Name == old) _champions[i] = _champions[i] with { Name = name };
        for (int i = 0; i < _hall.Count; i++) if (_hall[i].Name == old) _hall[i] = _hall[i] with { Name = name };
        if (_cupChampion == old) _cupChampion = name;
        _story.Add((0, "rename", $"{{tag}} 개명 — {old} → {name}"));
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
        if (costs.Length == 0) return Err("그런 시설이 없다");
        int step = facility == "quarters" ? lv : lv - 1;          // 다음 단계 비용 인덱스
        if (step >= costs.Length || lv >= max) return Err("더 올릴 수 없다 — 이미 끝이다");
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
            if (w != null) Console.WriteLine("  {warn} world.json 손상 — 백업(world.json.bak)에서 복구.");
        }
        if (w is null) return false;
        if (w.SchemaVer != SchemaVer)
        { Console.WriteLine($"  {{warn}} world.json 스키마 v{w.SchemaVer} ≠ v{SchemaVer} (라니스타 모드 개편) — 새 세계로 시작."); return false; }

        static WorldV2? TryRead(string path)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<WorldV2>(File.ReadAllText(path), JsonOpts); }
            catch { return null; }
        }
        if (w.ConstantsVer != ConstantsVer)
            Console.WriteLine($"  {{warn}} 상수버전 {w.ConstantsVer} ≠ {ConstantsVer} — 과거 경기는 다른 밸런스.");

        _worldSeed = w.WorldSeed; _gold = w.Gold;
        _gachaCount = w.GachaCount; _freeGachas = w.FreeGachas;
        _trainingLv = w.TrainingLv; _medicalLv = w.MedicalLv; _quartersLv = w.QuartersLv;
        _seasonsPlayed = w.SeasonsPlayed;
        SeasonActive = w.SeasonActive; _seasonNo = w.SeasonNo; _matchIdx = w.MatchIdx;
        _cursor = w.Cursor; _eventsAppended = w.EventsAppended;
        _schedule.Clear(); if (w.Schedule != null) _schedule.AddRange(w.Schedule);
        // 정규 라운드 수 복원 — 스케줄이 진실의 원천(라운드로빈은 부 인원에 따라 달라진다)
        _rounds = Math.Max(1, _schedule.Where(s => s.Kind == "regular").Select(s => s.Round).DefaultIfEmpty(_rounds).Max());
        _story.Clear(); if (w.Story != null) _story.AddRange(w.Story.Select(s => (s.Round, s.Kind, s.Text)));
        _eventDocs.Clear(); if (w.Events != null) _eventDocs.AddRange(w.Events);
        _cast.Clear(); _cast.AddRange(w.Gladiators.Select(FromRec));
        _candidates.Clear(); if (w.Candidates != null) _candidates.AddRange(w.Candidates.Select(FromRec));
        _matchLog.Clear(); if (w.MatchLog != null) _matchLog.AddRange(w.MatchLog);
        _archive.Clear(); if (w.Archive != null) _archive.AddRange(w.Archive);
        _masterName = w.MasterName; _masterTrait = w.MasterTrait; _masterTactic = w.MasterTactic; _scoutLevel = w.ScoutLevel;
        _masterTraitPool = w.MasterTraitPool; _masterTacticPool = w.MasterTacticPool;   // 구세이브 = null(단일 기본값 경로)
        if (w.AxisCapBonus != null) for (int i = 0; i < 6 && i < w.AxisCapBonus.Length; i++) _axisCapBonus[i] = w.AxisCapBonus[i];
        _betHits = w.BetHits; _patronage = w.Patronage; _betStreak = w.BetStreak;
        _redemption = w.Redemption; _myCupTitles = w.MyCupTitles;
        _fixFighterId = w.FixFighterId; _fixReward = w.FixReward;
        _betLog.Clear(); if (w.BetLog != null) _betLog.AddRange(w.BetLog);
        _streetSeq = w.StreetSeq; _surgerySeq = w.SurgerySeq;
        // [13] 캠페인·반란 지수·전설 — 구세이브(StoryStage 없음)는 캠페인 완료 취급(전부 해금), 전설은 소급 시드
        _storyStage = w.StoryStage ?? "chronicle";
        _storyBeats.Clear(); if (w.StoryBeats != null) foreach (var b in w.StoryBeats) _storyBeats.Add(b);
        _storyCtx = w.StoryCtx; _fixChoice = w.FixChoice;
        _keepsakes.Clear();
        if (w.Keepsakes != null) _keepsakes.AddRange(w.Keepsakes);
        else if (w.GhostClues != null) foreach (var c in w.GhostClues) AddClue(c);   // 구세이브 유품함 단서 → 보관함 문서 마이그레이션
        _debtLog.Clear(); if (w.DebtLog != null) _debtLog.AddRange(w.DebtLog);
        _tbWinnerId = w.TiebreakWinner; _banquetSeason = w.BanquetSeason; _preWeek = w.PreWeek;
        _pressArchive.Clear(); if (w.PressArchive != null) _pressArchive.AddRange(w.PressArchive);
        _unrest = w.Unrest; _legendRefs = w.LegendRefs; _favorAtE1 = w.FavorAtE1;
        _legends.Clear();
        if (w.Legends != null) _legends.AddRange(w.Legends);
        else if (!_playerless) SeedLegends();
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
        _festStage = w.FestStage; _festSlots = w.FestSlots ?? new(); _festRepId = w.FestRepId; _festChampion = w.FestChampion;
        _pendingEventId = w.PendingEventId; _pendingEventFighter = w.PendingEventFighter;
        _rivalRep.Clear();
        if (w.RivalReps != null) foreach (var lr in w.RivalReps) _rivalRep[lr.Id] = lr.Rep;
        foreach (var lid in _cast.Where(g => !g.IsPlayer).Select(g => g.LudusId).Distinct())
            _rivalRep.TryAdd(lid, 0f);   // 구세이브 호환 — 캐스트 소속에서 라이벌 루두스 복원
        _ledger.Load(w.Relations);
        return true;
    }

    /// <summary>자동저장 on/off. off로 두면 진행이 디스크에 안 쌓이고, 재시작 시 마지막 수동 저장 시점으로 로드.</summary>
    public string SetAutosaveJson(bool on)
    {
        _autosave = on;
        if (on) SaveWorld();   // 켤 때 현재 상태를 곧바로 반영
        return """{"ok":true}""";
    }

    /// <summary>수동 저장 — 자동저장 off여도 강제로 현재 상태를 슬롯 파일에 기록.</summary>
    public string ManualSaveJson()
    {
        bool prev = _autosave;
        _autosave = true;
        SaveWorld();
        _autosave = prev;
        return """{"ok":true}""";
    }

    private void SaveWorld()
    {
        if (!_autosave) return;   // 자동저장 off — 메모리 상태만 유지(수동 저장/재시작 로드 기준)
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
            _axisCapBonus.Any(x => x != 0f) ? _axisCapBonus.ToArray() : null, _betHits, _patronage, _betStreak,
            _redemption, _myCupTitles, _fixFighterId, _fixReward,
            _betLog.Count > 0 ? _betLog.ToList() : null, _streetSeq, _surgerySeq,
            _storyStage, _storyBeats.Count > 0 ? _storyBeats.ToList() : null, _storyCtx,
            _fixChoice, null,   // GhostClues: 더 이상 기록 안 함(Keepsakes로 대체)
            _unrest, _legends.Count > 0 ? _legends.ToList() : null, _legendRefs, _favorAtE1,
            _keepsakes.Count > 0 ? _keepsakes.ToList() : null, _debtLog.Count > 0 ? _debtLog.ToList() : null,
            _tbWinnerId, _masterTraitPool, _masterTacticPool, _banquetSeason, 0, 0,
            _pressArchive.Count > 0 ? _pressArchive.ToList() : null, _preWeek,
            _festStage, _festSlots.Count > 0 ? _festSlots.ToList() : null, _festRepId, _festChampion), JsonOpts));
    }

    private static GladRec ToRec(Gladiator g) => new(g.Id, g.Name, g.WeaponId, g.PersonalityId,
        g.TacticPool, g.TacticId,
        g.Stats.Atk, g.Stats.Def, g.Stats.HpMax, g.Stats.Spd, g.Stats.Aspd, g.Stats.Rct,
        (int)g.Talent, (int)g.Potential, g.TalentBudget, g.PotentialBudget,
        g.TraitIds, g.IsPlayer, g.Age, g.AgingStartAge, g.TrainingPoints, g.MatchCounter,
        g.CW, g.CL, g.CD, g.CKoW, g.Fame, g.Popularity,
        g.W, g.L, g.D, g.Streak, g.PendingEmotions.ToArray(), g.Fatigue, g.InjuryMatches, g.LudusId, g.Division, g.SeasonBrutals,
        g.MGrit, g.MRecover, g.MShow, g.MPay,
        g.EmoHistory.Count > 0 ? new Dictionary<string, int>(g.EmoHistory) : null,
        g.SkillIds.Length > 0 ? g.SkillIds : null, g.GrudgeCount,
        g.BestStreak, g.Executions, g.TotalMatchTime, g.TotalDamage,
        g.TotalDamageTaken, g.TotalBlocks, g.TotalDodges,
        g.PermInjuries.Count > 0 ? g.PermInjuries.ToArray() : null);

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
        if (r.EmoHistory != null) foreach (var kv in r.EmoHistory) g.EmoHistory[kv.Key] = kv.Value;
        if (r.Skills != null) g.SkillIds = r.Skills.Where(SkillTable.Exists).ToArray();
        g.GrudgeCount = r.GrudgeCount;
        g.BestStreak = r.BestStreak; g.Executions = r.Executions;
        g.TotalMatchTime = r.TotalMatchTime; g.TotalDamage = r.TotalDamage;
        g.TotalDamageTaken = r.TotalDamageTaken; g.TotalBlocks = r.TotalBlocks; g.TotalDodges = r.TotalDodges;
        if (r.PermInjuries != null) g.PermInjuries.AddRange(r.PermInjuries);
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
            g.Fatigue, g.InjuryMatches > 0, g.Division, g.CKoW, LudusNameOf(g.LudusId))).ToList();
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
            string an, bn; string? winner = null; int idx = -1; bool mine; string? title = null;
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
                if (ga != null && gb != null) title = BoutTitle(ga, gb, s);   // 타이틀전 라벨(#5) — 예정 경기만
            }
            bool hot = !played && ((_cast.FirstOrDefault(g => g.Id == s.A)?.Streak ?? 0) >= 3
                                || (_cast.FirstOrDefault(g => g.Id == s.B)?.Streak ?? 0) >= 3);   // 연승 걸린 경기(DDD)
            cal.Add(new CalDoc(idx, month, day % 30 + 1, an, bn, s.Kind, s.Format, winner, mine, SeasonActive && i == _cursor, hype, hot, title));
        }
        // 현재 로마력 날짜(달력 오늘 강조) — RomanDate()와 동일한 스케줄 위치 비례
        string? curMonth = null; int curDay = 0;
        if (SeasonActive)
        {
            int d0 = (int)(Math.Clamp((float)_cursor / Math.Max(1, _schedule.Count), 0f, 1f) * 239f);
            curMonth = RomanMonths[Math.Min(RomanMonths.Length - 1, d0 / 30)];
            curDay = d0 % 30 + 1;
        }
        return new SeasonDoc(SchemaVer, Math.Max(1, _seasonNo), _rounds, _matchIdx, total, !SeasonActive,
            next != null ? ById(next.A).Name : null, next != null ? ById(next.B).Name : null, next?.IsEvent ?? true,
            standings[0].Name, fighters, rels, _eventDocs.ToList(),
            _story.Select(s => new StoryDoc(s.Round, s.Kind, s.Text)).ToList(),
            _matchLog.Select(e => new MatchLogDoc(e.Idx, e.Round, e.IsEvent, e.AName, e.BName, e.Winner, e.Reason, e.IsPlayerMatch)).ToList(),
            _champions.Count > 0 ? _champions.ToList() : null,
            _hall.Count > 0 ? _hall.OrderByDescending(h => h.Fame).ToList() : null,
            cal, 680 + Math.Max(1, _seasonNo), curMonth, curDay);
    }

    private void WriteSeasonJson() => File.WriteAllText("season.json", JsonSerializer.Serialize(BuildSeasonDoc(), JsonOpts));

    private sealed record NewsArt(string Header, string Body);   // 기사: 굵은 머릿글(기존 가시성 텍스트) + 본문 산문
    private sealed record PressIssue(int Season, int Month, string MonthName, int Auc,
        string Headline, string HeadBody, List<NewsArt> Articles, string Flavor, string Ad);   // 월보 한 호(영속)
    private readonly List<PressIssue> _pressArchive = new();   // 지난 시즌 월보 영속(시즌 넘어가도 안 사라짐)

    /// <summary>머릿글(사실)에 붙일 신문 어체 본문 — 종류별 산문 풀(시드 변주). 읽을거리이자 세계의 어조.</summary>
    private static string ArticleBody(string kind, SimRandom rng)
    {
        string[] p = kind switch
        {
            "death" => new[] { "모래가 또 한 사람을 삼켰다. 관중은 잠시 숨을 죽였다가, 이내 다음 피를 재촉했다.", "그의 이름은 머잖아 잊히겠으나, 오늘 밤 선술집에서만은 오래 회자되리라." },
            "promote" => new[] { "1부의 문이 열렸다. 더 큰 무대, 더 굶주린 군중이 그를 기다린다.", "승격은 축배가 아니라 각오다 — 위로 오를수록 칼끝은 날카로워진다." },
            "relegate" => new[] { "2부의 먼지 속으로 내려간다. 재기를 노리는 자에게 강등은 끝이 아니라 서약이다.", "어제의 함성이 오늘의 침묵이 되었다. 군중은 원래 잔인한 법이다." },
            "cup" => new[] { "챔피언십 컵이 새 주인을 맞았다. 월계관은 무겁고, 지키기란 더 무겁다.", "결승의 모래는 유난히 붉었다. 콜로세움은 오래 이 이름을 새길 것이다." },
            "season" => new[] { "한 시즌이 저물었다. 승자는 대리석에, 패자는 기억 속에 이름을 남긴다.", "모래가 식어 간다. 겨우내 라니스타들은 다음 봄의 칼을 벼린다." },
            "upset" => new[] { "배당판이 뒤집혔다. 도박꾼들의 곡소리와 환호가 한데 뒤엉켰다.", "모래는 명성을 읽지 못한다 — 오늘 그것이 다시 증명되었다." },
            "comeback" => new[] { "사선을 넘어 돌아왔다. 관중은 자리에서 일어섰고, 함성은 경기장 벽을 넘었다.", "패배의 문턱에서 승리를 낚아챈 밤 — 이것이 콜로세움이다." },
            "revenge" => new[] { "묵은 빚이 피로 청산됐다. 원한은 모래 위에서 가장 정직하게 갚아진다.", "두 사람은 다시 만날 것이다. 원한이란 좀처럼 한 번으로 끝나지 않는다." },
            "persona" => new[] { "그날 이후 그는 달라졌다고들 한다. 모래는 검뿐 아니라 사람도 벼린다.", "상처는 몸에만 남지 않는다 — 마음에 새겨진 것이 더 오래간다." },
            "debut" => new[] { "새 얼굴이 처음으로 모래를 밟았다. 신예의 첫 함성은 늘 서툴고, 그래서 애틋하다.", "오늘의 신예가 내일의 전설이 될지는 오직 모래만이 안다." },
            "recruit" => new[] { "노예 시장이 분주하다. 원석 하나가 챔피언이 되는 데엔 안목과 운이 함께 필요하다." },
            "sparring" or "camp" => new[] { "프리시즌의 땀이 여름의 피를 준비한다. 기록엔 남지 않아도 몸은 기억한다." },
            "unrest" => new[] { "거리의 공기가 달라지고 있다. 흉흉한 시절일수록 콜로세움의 함성은 더 사나워진다." },
            "legend" => new[] { "한 이름이 전설의 반열에 올랐다. 세대가 지나도 모래는 그를 척도로 삼을 것이다." },
            "injury" or "perm_injury" => new[] { "의원(醫院) 앞에 또 한 사람이 실려 갔다. 영광의 값은 언제나 몸으로 치른다." },
            "patron" => new[] { "후원자의 그림자가 길다. 모래 위의 승부만큼이나 관람석의 정치도 치열하다." },
            "retire" => new[] { "한 시대가 검을 내려놓았다. 관중은 박수를, 젊은 검투사는 빈자리를 물려받는다." },
            "match" => new[] { "군중은 만족했다. 오늘의 승자는 내일 또 시험대에 오를 것이다.", "심판의 손이 하늘을 가리켰다. 모래는 다시 평평해졌다.", "한 합 한 합이 도박이었다. 이긴 자는 웃었고, 진 자는 배웠다." },
            _ => new[] { "모래는 오늘도 정직했다 — 그 위의 인간들이 문제일 뿐." },
        };
        return p[(int)(rng.NextUInt64() % (ulong)p.Length)];
    }

    // 경기장 바깥 소식(세계관 공기) — 반란 지수 국면별 풀, 시드 결정론
    private static readonly string[][] StreetNews =
    {
        new[] { "포룸의 곡물 값이 안정세다. 시민들은 다음 흥행을 이야기한다.",
                "항구에 누미디아산 맹수가 도착했다 — 맹수전 흥행주들이 값을 다툰다.",
                "총독 관저에서 사흘 밤 연회가 열렸다. 포도주가 강처럼 흘렀다 한다.",
                "신전 앞 점술사들이 성업 중이다 — 도박꾼들이 배당보다 신탁을 믿는 철이다." },
        new[] { "남쪽 가도에서 탈주 노예 무리가 목격됐다는 소문이 시장을 돈다.",
                "곡물 값이 들썩인다. 빵집 앞 줄이 길어지면 경기장 함성도 사나워진다.",
                "밤길에 횃불 순찰이 늘었다. 여인숙 주인들은 문단속을 이른다." },
        new[] { "폭동의 불길이 이웃 도시를 스쳤다 — 성문 검문이 강화됐다.",
                "노예 값이 치솟았다. 시장 상인들은 '파는 쪽도 목숨값'이라 푸념한다.",
                "군중은 흉흉할수록 피에 목마르다 — 관중석은 오히려 만원이다." },
        new[] { "총독부가 흥행세를 올려 걷는다. 라니스타들의 곡소리가 포룸까지 들린다.",
                "병사들이 거리를 순찰한다. 검투 흥행만이 유일하게 허가된 함성이다." },
    };
    private static readonly string[] RomanAds =
    {
        "【광고】 메빌리우스의 올리브유 — 챔피언들이 바르는 바로 그 기름!",
        "【광고】 카푸아 대장간 — 부러지지 않는 검, 부러지면 두 자루로 보상.",
        "【광고】 셉티무스 의원(醫院) — 검상·자상·수치심 빼고 다 꿰맵니다.",
        "【광고】 투스쿨룸 포도주 — 승리의 밤에도, 패배의 밤에도.",
        "【광고】 리비아의 세탁소 — 핏물 전문. 문의는 목욕탕 뒷골목.",
    };

    private static int NewsPri(string k) => k switch   // 1면 헤드라인 우선순위 — 극적일수록 크게
    {
        "death" => 10, "perm_injury" => 9, "promote" => 9, "relegate" => 9, "cup" => 8, "season" => 8,
        "upset" => 7, "comeback" => 6, "revenge" => 5, "greatest" => 4, "persona" => 3, "debut" => 3,
        "unrest" => 3, "legend" => 3, "retire" => 3, "injury" => 2, "patron" => 1, "grudge" => 1, _ => 0,
    };
    private static string NewsClean(string t)   // "R3 ★ …" 라운드 접두 제거 → 신문 문장
    {
        int sp = t.IndexOf(' ');
        if (sp > 1 && t[0] == 'R' && int.TryParse(t.AsSpan(1, sp - 1), out _)) return t[(sp + 1)..];
        return t;
    }

    /// <summary>이번 시즌 월보 호(號)들을 서사 로그·경기 로그에서 편집. 각 기사 = 머릿글(사실) + 산문 본문.
    /// 시즌이 넘어가면 FinalizeSeason이 이 결과를 _pressArchive에 박제해 영속 보관한다.</summary>
    private List<PressIssue> BuildSeasonIssues(int season)
    {
        int matchesTotal = Math.Max(1, _schedule.Count);
        int maxRound = Math.Max(1, _schedule.Count > 0 ? _schedule.Max(s => s.Round) : _rounds);
        int MonthOf(int round) => Math.Min(RomanMonths.Length - 1, (round - 1) * RomanMonths.Length / Math.Max(1, maxRound));
        int MonthOfIdx(int idx) => Math.Min(RomanMonths.Length - 1, idx * RomanMonths.Length / matchesTotal);
        var stories = _story.Where(s => s.Round > 0 && s.Kind is not ("bet" or "fix" or "tease")).ToList();   // 예고는 일회성 — 회고지엔 안 싣는다
        var monthsSet = stories.Select(s => MonthOf(s.Round))
            .Concat(_matchLog.Select((m, i) => MonthOfIdx(i))).Distinct().OrderBy(x => x).ToList();
        int auc = 680 + Math.Max(1, season);
        return monthsSet.Select(mo =>
        {
            var rng = new SimRandom(SeasonSeed ^ 0x2E75_1E77UL + (ulong)(season * 13 + mo) * 97UL);
            var ordered = stories.Where(s => MonthOf(s.Round) == mo).OrderByDescending(s => NewsPri(s.Kind)).ToList();
            var results = _matchLog.Select((m, i) => (m, i)).Where(x => MonthOfIdx(x.i) == mo && x.m.Winner != "무승부")
                .Select(x => new NewsArt($"{{swords}} {x.m.Winner}, {(x.m.Winner == x.m.AName ? x.m.BName : x.m.AName)}을(를) 꺾다"
                    + (x.m.Reason == "KO" ? " (KO)" : " (판정)"), ArticleBody("match", rng))).ToList();
            string headline, headBody;
            var arts = new List<NewsArt>();
            if (ordered.Count > 0)
            {
                headline = NewsClean(ordered[0].Text); headBody = ArticleBody(ordered[0].Kind, rng);
                arts.AddRange(ordered.Skip(1).Take(5).Select(s => new NewsArt(NewsClean(s.Text), ArticleBody(s.Kind, rng))));
            }
            else if (results.Count > 0) { headline = results[^1].Header.Replace("{swords} ", ""); headBody = results[^1].Body; results.RemoveAt(results.Count - 1); }
            else { headline = "조용한 한 달 — 모래만 뜨거웠다"; headBody = "큰 사건 없는 한 달이었다. 라니스타들은 다음 흥행을 셈했고, 검투사들은 상처를 다스렸다."; }
            arts.AddRange(results.AsEnumerable().Reverse().Take(4));   // 경기 결과 읽을거리
            var pool = StreetNews[Math.Min(StreetNews.Length - 1, UnrestStageIdx)];
            return new PressIssue(season, mo + 1, RomanMonths[mo], auc, headline, headBody, arts.Take(6).ToList(),
                pool[(int)(rng.NextUInt64() % (ulong)pool.Length)], RomanAds[(int)(rng.NextUInt64() % (ulong)RomanAds.Length)]);
        }).ToList();
    }

    /// <summary>콜로세움 월보(#1 개편) — 로마력 월간 발행, 시즌 넘어가도 아카이브로 영속. 실제 신문 어체·머릿글+본문.
    /// 이번 시즌 진행분 + 지난 시즌 아카이브를 함께 낸다(최신 시즌 먼저).</summary>
    public string NewsJson()
    {
        // 진행 중인 시즌만 현행분으로 편집 — 시즌이 끝나면(프리시즌) 이미 아카이브에 박제돼 있어 겹치면 중복 발행이 된다.
        var cur = SeasonActive ? BuildSeasonIssues(Math.Max(1, _seasonNo)) : new List<PressIssue>();
        var all = cur.Concat(_pressArchive)                                   // 현행 + 영속 아카이브
            .OrderByDescending(i => i.Season).ThenByDescending(i => i.Month).Take(40).ToList();
        return JsonSerializer.Serialize(new { ok = true, season = Math.Max(1, _seasonNo), auc = 680 + Math.Max(1, _seasonNo), issues = all }, JsonOpts);
    }

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
            g.MGrit, g.MRecover, g.MShow, g.MPay, g.CKoW,
            g.SkillIds.Length > 0 ? g.SkillIds : null,
            g.Talent >= TalentGrade.Champion ? 2 : 1,
            g.PermInjuries.Count > 0 ? PermInjuryInfos(g) : null)).ToList();

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
                BoutTitle(A, B, s),
                MyWinPct: pctInt, MyOdds: MathF.Round(10000f / pctInt) / 100f, OppOdds: MathF.Round(10000f / (100 - pctInt)) / 100f,
                CrowdFavorsMe: mine != null && mine.Popularity >= opp.Popularity,
                Hype: MathF.Round((A.Popularity + B.Popularity) * (s.Format == "execution" ? 2f : s.IsEvent ? 1.5f : 1f) + (A.Fame + B.Fame) * 0.1f),
                OddsA: MathF.Round(BetOddsFor(0) * 100f) / 100f, OddsB: MathF.Round(BetOddsFor(1) * 100f) / 100f,
                // 예상 수익(#15): Play의 출전료 공식과 동일 — 인기 hype·이벤트·처형전·협상 마스터리 반영
                FeeEstimate: mine == null ? 0f : MathF.Round(
                    (FeeBase + (mine.Popularity + opp.Popularity) * FeePopScale)
                    * (s.Format == "execution" ? 3f : s.IsEvent ? 2f : 1f) * IncomeMult * (1f + 0.08f * mine.MPay)),
                WinBonusEstimate: mine == null ? 0f : MathF.Round(WinBonus * IncomeMult),
                // 승자×방식 조합 배당(누가 어떻게 이길지) — AI 경기 도박용
                OddsAKo: MathF.Round(BetOddsFor(2) * 100f) / 100f, OddsADec: MathF.Round(BetOddsFor(3) * 100f) / 100f,
                OddsBKo: MathF.Round(BetOddsFor(4) * 100f) / 100f, OddsBDec: MathF.Round(BetOddsFor(5) * 100f) / 100f,
                AId: A.Id, BId: B.Id,
                MyQuote: mine != null ? PreMatchQuote(mine, opp) : null,
                OppQuote: mine != null ? PreMatchQuote(opp, mine) : null,
                IsMirror: A.IsPlayer && B.IsPlayer,
                OppId: A.IsPlayer && B.IsPlayer ? opp.Id : null,
                OppPool: A.IsPlayer && B.IsPlayer ? opp.TacticPool.Select(t => t.Replace("TAC_", "")).ToArray() : null,
                OppTactic: A.IsPlayer && B.IsPlayer ? opp.TacticId.Replace("TAC_", "") : null);
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
                ? new BetDoc(BetLabel(_betSide, ById(_schedule[_cursor].A), ById(_schedule[_cursor].B)), _betAmount, _betOdds) : null,
            _favor,
            HasMyMatchAhead: SeasonActive && Enumerable.Range(_cursor, Math.Max(0, _schedule.Count - _cursor))
                .Any(i => ById(_schedule[i].A).IsPlayer || ById(_schedule[i].B).IsPlayer),
            RecruitReveal: _lastReveal.Count > 0 ? _lastReveal.ToList() : null,
            MasterPending: (_masterTrait != null || _masterTactic != null
                            || _masterTraitPool is { Length: > 0 } || _masterTacticPool is { Length: > 0 }) ? _masterName : null,
            MasterTraits: (_masterTraitPool ?? (_masterTrait != null ? new[] { _masterTrait } : Array.Empty<string>()))
                .Where(TraitTable.Exists).Select(t => new MasterGiftDoc(t, TraitTable.Get(t).Name)).ToList() is { Count: > 0 } mt ? mt : null,
            MasterTactics: (_masterTacticPool ?? (_masterTactic != null ? new[] { _masterTactic } : Array.Empty<string>()))
                .Select(t => t.Replace("TAC_", "")).ToList() is { Count: > 0 } mc ? mc : null,
            ScoutLevel: _scoutLevel,
            Legacy: BuildLegacyNote(),
            Patronage: MathF.Round(_patronage),
            Gamble: new GambleDoc(MathF.Round(_seasonBetNet), _betLog.Count(b => b.Won), _betLog.Count,
                _betLog.AsEnumerable().Reverse().Take(40).ToList(), _betStreak),
            Redemption: _redemption,
            FixTarget: _fixFighterId != null ? _cast.FirstOrDefault(g => g.Id == _fixFighterId)?.Name : null,
            Campaign: BuildCampaignDoc(), Unrest: BuildUnrestDoc(),
            Legends: _legends.Count > 0 ? _legends.ToList() : null,
            DebtInfo: BuildDebtDoc(), Keepsakes: BuildKeepsakes(), Preseason: BuildPreseasonDoc(),
            Festival: BuildFestivalDoc()), JsonOpts);
    }

    /// <summary>{masks} 대항전 문서 — 내 대표(지명 또는 간판 기본값)·진행 브래킷·우승자.</summary>
    private FestivalDoc? BuildFestivalDoc()
    {
        if (_playerless) return null;
        string Nm(string id) => _cast.FirstOrDefault(g => g.Id == id)?.Name ?? id;
        var mine = _cast.Where(g => g.IsPlayer).ToList();
        string? myRep = mine.Count == 0 ? null
            : (mine.FirstOrDefault(g => g.Id == _festRepId) ?? mine.OrderByDescending(g => g.Fame).First()).Name;
        List<CupMatchDoc>? bracket = null;
        if (_festStage >= 1)
            bracket = _schedule.Where(s => s.Kind.StartsWith("fest_")).Select(s =>
            {
                // 같은 쌍이 정규(같은 라운드 번호)에서도 만났을 수 있어 최신 로그 우선(대항전은 나중에 치러진다)
                var log = _matchLog.LastOrDefault(m => m.AId == s.A && m.BId == s.B && m.Round == s.Round);
                return new CupMatchDoc(s.Kind == "fest_final" ? "결승" : s.Kind == "fest_sf" ? "4강" : "8강",
                    Nm(s.A), Nm(s.B), log != null && log.Winner != "무승부" ? log.Winner : null);
            }).ToList();
        return new FestivalDoc(_festStage, myRep, _festStage == 0 && mine.Count > 0, _festChampion, bracket);
    }
    private string? BuildLegacyNote()
    {
        var parts = new List<string>();
        for (int a = 0; a < 6; a++) if (_axisCapBonus[a] > 0f) parts.Add($"{AxisName(a)} 상한 +{_axisCapBonus[a]:F0}");
        if (_scoutLevel > 0) parts.Add($"스카우터 Lv{_scoutLevel}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    public string PlayNextJson(string? body)
    {
        string? tacticId = null, prep = null;
        if (!string.IsNullOrWhiteSpace(body))
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("tacticId", out var t) && t.GetString() is { Length: > 0 } tid)
                    tacticId = tid.StartsWith("TAC_") ? tid : "TAC_" + tid;
                if (doc.RootElement.TryGetProperty("prep", out var p)) prep = p.GetString();
            }
            catch { }
        return JsonSerializer.Serialize(PlayNext(tacticId, prep), JsonOpts);
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
            Console.WriteLine("\n  시즌 대시보드 서버 기동 (Ctrl+C로 종료)");
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
                $"  {ViewerExport.TalentName(g.Talent)}·{g.Age}세·[{string.Join(",", g.TraitIds.Select(t => TraitTable.Get(t).Name))}]{(k == 0 ? " {{crown}}" : "")}");
        }
        Console.WriteLine($"\n  {{trophy}} 리그 챔피언: {season[0].Name}" + (_cupChampion != null ? $"  ·  {{trophy}} 컵 우승: {_cupChampion}" : "") + "\n");

        Console.WriteLine("  [{fest} 이벤트 매치]");
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

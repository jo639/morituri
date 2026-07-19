using Morituri.Sim.Core;

namespace Morituri.Sim.Data;

/// <summary>
/// T12 패시브 스킬 MVP (문서[6]§3.1 "엔진은 하나, 경제는 둘" · [7]§5).
/// 스킬 = <b>장착형 특성 행</b> — TraitDef를 그대로 재사용해 Sim 훅 추가가 없다(원칙 A/C).
/// 특성(T09)과의 차이는 경제뿐: 특성=타고남·탈부착 불가 / 스킬=성격·천부 게이트 안에서 습득·교체(Game 층 슬롯).
/// 생성 추첨 풀(TraitGen)에는 절대 들어가지 않는다 → 미장착 세계의 매트릭스는 구조적으로 불변.
/// 수치 원칙: 타고난 특성보다 항상 약하게(스킬은 보완재, [6]§3.2 "낮은 계급에도 유용한 것").
/// </summary>
/// <summary>액티브 발동 조건([7]§4 트리의 조건 게이트) — 상태 기반, AI가 스스로 판단.</summary>
public enum SkillTrigger
{
    SelfHpBelow,          // 자기 HP 비율 ≤ 임계 (광전사의 도끼)
    EvenFight,            // 호각 — HP 격차 ≤ 임계%p & 교전 지속 (결투의 격)
    ConsecHitsTaken,      // 연속 피격 ≥ 임계 (불퇴의 자세)
    OppGuarding,          // 상대 가드 중 & 사거리내 (분쇄 일격)
    OppGuardGaugeBelow,   // 사거리내 & 상대 가드게이지 비율 < 임계 (연격)
    GapBand,              // 간격이 [GapMinM, GapMaxM] 안 (견제 찌르기·쇄도 베기·휘감기·공간 지배)
    OppHeavyWindupOrPress,// 상대 강공 선딜 인지 or 근접 압박 (철벽 반격)
    OppHeavyWindupOrRecovery, // 상대 강공 선딜 or 후딜 (그림자 보)
    OppVulnerable,        // 상대 경직/가드붕괴/스태거 — 확정 히트 창 (난무)
    InRange,              // 사거리내 (대지 강타)
    OppExecutable,        // 상대 HP < 임계 or 다운/스태거 (심판의 일격 — 거부권 대상)
    OppWindupAny,         // 상대 공격 선딜 인지 — 반응형 최우선 (방패 막기)
    OppGuardingOrStunned, // 상대 가드 중/경직 (방패 밀치기)
}

/// <summary>효과 형태 — 전용 모션 없이 기존 프리미티브로 구현(즉발 타격·위치 이동·시한 플래그). 연출은 애니메이션 트랙에서.</summary>
public enum ActiveKind { Buff, Strike, Stance, Charge }

/// <summary>
/// [7] 무기 액티브 명세 — <b>AI가 조건·확률로 발동</b>한다(관전형 — 감독이 누르는 게 아님, [7] 전제).
/// 발동 = [7]§1 트리(쿨→상태→코스트→거부권→조건→타당성→확률 롤) 통과.
/// 코스트: ST(공격 버스트)/HP%(배수진)/GG(방어)/CD만(수비·유틸) — [7]§0.
/// ⚠ 공간 수치(간격·이동거리)는 [7]의 ×1.5 스케일 이전 값을 현행 스케일로 환산해 담는다.
/// </summary>
public sealed record ActiveSpec(
    string ReasonTag,                       // [7] 가시화 원칙 — Decision("SKILL_"+tag)로 발동 방출
    SkillTrigger Trigger, float Threshold, float Prob,
    float Duration, float CooldownSec,
    ActiveKind Kind = ActiveKind.Buff,
    float StCost = 0f, float SelfHpPctCost = 0f, float GgCost = 0f,
    float GapMinM = 0f, float GapMaxM = 0f,     // GapBand 트리거용(현행 스케일)
    // ── Buff 효과 ──
    float CounterWindowAdd = 0f,            // 결투의 격: 카운터창 +0.3 (Override 파이프, 캡 +0.6 [7]§2)
    float DmgTakenMult = 1f,                // 광전사의 도끼: 받피 +25% (설계 의도된 리스크)
    float AtkPerMissingHpPct = 0f, float AtkCap = 0f,   // 광전사: 공격력 +0.8%/(부족 HP%p), 최대 +40%
    bool PoiseImmune = false,               // 불퇴의 자세: 포이즈 무한 = 스태거/넉백 면역(가드붕괴·다운은 아님)
    bool SunderNextHeavy = false,           // 분쇄 일격: 다음 강공 가드 무조건파괴 1회(미사용 시 만료 소멸)
    float AttackSpeedMult = 1f,             // 연격: 공속 +35% (모션 시간 ÷) — 광폭화와 가산 캡은 모션 트랙에서
    float EarlyEndGapM = 0f,                // 연격: 상대가 이 거리보다 멀어지면 조기 종료
    bool KiteExempt = false,                // 공간 지배: 카이팅 ST 소모 면제
    float AutoPokeMult = 0f, float AutoPokeIntervalSec = 0f,  // 공간 지배: 사거리 진입자 자동 견제(약공 ×0.6 / 0.8s)
    // ── Strike 효과(즉발 — 모션 없는 1차 구현) ──
    bool StrikeHeavy = false, float StrikeDmgMult = 1f, int StrikeHits = 1,
    float KnockbackM = 0f,                  // 견제 찌르기: 넉백(하이퍼아머·불퇴면 무효, 피해는 적용)
    float PullM = 0f, float RootSec = 0f,   // 휘감기: 끌어당김/이동봉쇄 택1(거리 따라)
    bool DashIn = false,                    // 쇄도 베기·방패 밀치기: 상대에게 돌진 후 타격
    float StaggerOnHitSec = 0f, float GuardPierce = 0f,  // 대지 강타: 명중 시 스태거·가드관통 50%
    bool BashBreak = false, float DownSec = 0f,          // 방패 밀치기: 가드붕괴+다운(면역이면 붕괴만)
    bool TeleportBehind = false, float NextLightCritSec = 0f,  // 그림자 보: 배후 이동+다음 약공 확정크리
    // ── Stance 효과(피격 반응) ──
    bool FullBlock = false, float CounterBoostMult = 1f, float CounterBoostSec = 0f, // 방패 막기: 완전차단+직후 반격 보너스
    bool AutoCounter = false,               // 철벽 반격: 자세 중 최초 피격 1회에 즉시 반격
    // ── [7]§4.5 후보 도입분(라니스타 배정) ──
    bool CounterOnGuard = false,            // 반격 태세: 자세 중 '가드 성공'에 즉시 반격(피격 반응인 AutoCounter와 트리 구분)
    float RangeAddM = 0f,                   // 사거리 증가: 지속 동안 리치 +m (만료 시 되돌림)
    float SlowMult = 1f, float SlowSec = 0f,// 둔화: 상대 이동속도 배율·지속(§2 CC — 다운/붕괴/스태거보다 약한 최하위)
    float CarryM = 0f, float WallSlamDmgMult = 0f,  // 캐리+벽꽝: 밀며 동반 이동 · 경계 충돌 시 추가타 배수
    // ── Charge 효과(심판의 일격) ──
    float ChargeSec = 0f, float ExecuteDmgMult = 0f, float ExecuteKillPct = 0f,
    bool VetoExecution = false);            // 거부권 대상([7]§8 — 고결은 처형류 발동 거부

/// <summary>성격 패시브 proc 조건([7]§5) — 대부분 상황 반응형(상시형은 Always).</summary>
public enum PassiveTrigger
{
    None,
    Always,             // 상시(정정당당·방비) — TraitDef 정적분 + AggressionAdd 영구 Override
    OnHitStun,          // 피격 경직 진입 (침착)
    Periodic,           // 내부 CD 주기 자동 (전장 분석)
    ConsecHitsTaken,    // 연속 피격 ≥ 임계 (투지)
    SelfHpBelow,        // 자기 HP ≤ 임계 (최후의 발악)
    SelfHpAboveWinning, // 자기 HP ≥ 임계 & 우세 (여유)
    HpDeficit,          // HP 열세 ≥ 임계%p (기사도의 보답)
    TimeLowAndLosing,   // 잔여 시간 ≤ 임계 & 열세 (역전의 영웅)
    AfterTaunt,         // 도발 성공 직후 (황제의 위압)
    OppHeavyWindup,     // 상대 강공 선딜 인지 (생존 본능)
    OppRecovery,        // 상대 후딜/등 노출 (기회의 일격)
    OppVulnerable,      // 상대 가드붕괴·지침·스태거 (약점 포착)
    OppHpBelow,         // 상대 HP ≤ 임계 (어부지리)
    OnDealHit,          // 가격 순간 (피의 갈증 — 출혈/저HP 상대 조건은 필드로)
    OppHpStep,          // 상대 HP가 임계 단위로 떨어질 때마다 (공포 군림)
    AfterHeavySwing,    // 강공·도박 행동 후 (배짱)
    OnCritOrHeavyOrTaunt, // 크리·강공 마무리·도발 (관중몰이)
    CrowdStackFull,     // 군중 스택 최대 (쇼타임)
    OppSkillActivated,  // 상대 액티브 발동 직후 (함정 간파)
}

/// <summary>
/// [7]§5 성격 패시브 명세 — <b>조건 충족 시 자동 proc</b>(플레이어 조작 없음).
/// 효과는 시한 배율·1회 플래그·스택. 상시형(Always)은 TraitDef 정적분과 병행.
/// </summary>
public sealed record PassiveSpec(
    string ReasonTag,
    PassiveTrigger Trigger, float Threshold = 0f, float Prob = 1f,
    float ProcCdSec = 0f, float Duration = 0f,
    float StCost = 0f,
    // 자기 버프
    float DmgDealtMult = 1f, float DmgTakenMult = 1f, float MoveMult = 1f,
    float AtkSpeedMult = 1f, float RecoveryMult = 1f, float IdleRegenMult = 1f,
    float CounterWindowAdd = 0f, float PerceptMult = 1f, float CritAdd = 0f,
    float AggressionAdd = 0f,
    // 스택형(투지·관중몰이)
    int StackMax = 0, float PerStackDmg = 0f,
    // 특수 거동
    bool ClearDebuffs = false,      // 침착: 분노·도발 override 해제
    bool ForceCrit = false,         // 기회의 일격: 확정 크리
    float CritDmgMult = 1f,
    float DodgeIFrameAdd = 0f, float DodgeRefundPct = 0f,   // 생존 본능
    float LifestealPct = 0f, float LifestealOppHpBelow = 0f, // 피의 갈증
    float FearAggPerStack = 0f, int FearStackMax = 0,        // 공포 군림(상대에게 누적)
    bool DashStrike = false,        // 어부지리: 즉발 처형 대시
    bool VetoExecution = false);    // 정정당당: 처형류 거부권([7]§8)

public sealed record SkillDef(
    TraitDef Def,               // 효과 본체(패시브 정적분) 또는 식별자(액티브 — 배율 전부 1)
    string GatePersonality,     // 패시브 = 성격 결합([7]§5). 액티브는 무기 게이트(GateWeapon)라 빈 문자열
    int RankTier,               // Ⅰ=1(전 천부) / Ⅱ=2(집정관 이상 — [6]§1.5 접근권)
    string Desc,
    ActiveSpec? Active = null,  // null=패시브
    string? GateWeapon = null,  // 액티브 = 무기 결합([7]§4 "무기별 액티브 2개")
    PassiveSpec? Passive = null); // [7]§5 proc형 패시브 명세

public static class SkillTable
{
    /// <summary>Ⅱ급 접근에 필요한 최소 천부(집정관). Game 층이 TalentGrade와 비교.</summary>
    public const int Tier2MinTalent = 3;   // TalentGrade.Consul

    public static readonly SkillDef[] All =
    {
        // ── 성격 패시브([7]§5, 10성격 × 2) — 조건 충족 시 자동 proc. 각 성격의 두 번째가 Ⅱ급(집정관+).
        // 냉철
        new(new TraitDef("SKL_COMPOSE", "침착(스킬)"), "PER_CALM", 1,
            "흔들리지 않는다 — 피격 경직 시 35%로 분노·도발 상태를 즉시 떨쳐낸다 (proc CD 6s · 主RCT)",
            Passive: new PassiveSpec("COMPOSE", PassiveTrigger.OnHitStun, Prob: 0.35f, ProcCdSec: 6f, ClearDebuffs: true)),
        new(new TraitDef("SKL_READ", "전장 분석(스킬)"), "PER_CALM", 2,
            "전장 전체가 느리게 보인다 — 8초마다 3초간 인지지연 −50%·카운터 창 +0.2 (集정관+ · 主RCT)",
            Passive: new PassiveSpec("READ", PassiveTrigger.Periodic, ProcCdSec: 8f, Duration: 3f,
                PerceptMult: 0.5f, CounterWindowAdd: 0.2f)),
        // 충동
        new(new TraitDef("SKL_FERVOR", "투지(스킬)"), "PER_RECKLESS", 1,
            "맞을수록 뜨거워진다 — 연속 2회 피격 시 4초간 공격력 +15%(최대 2중첩 +30%) (proc CD 5s · 主ATK)",
            Passive: new PassiveSpec("FERVOR", PassiveTrigger.ConsecHitsTaken, Threshold: 2f, ProcCdSec: 5f,
                Duration: 4f, StackMax: 2, PerStackDmg: 0.15f)),
        new(new TraitDef("SKL_LASTSTAND", "최후의 발악(스킬)"), "PER_RECKLESS", 2,
            "죽기 직전이 가장 빠르다 — HP 25% 이하에서 이속 +40%·공속 +25%, 받는 피해 +25% (集정관+ · 主SPD)",
            Passive: new PassiveSpec("LASTSTAND", PassiveTrigger.SelfHpBelow, Threshold: 0.25f,
                MoveMult: 1.40f, AtkSpeedMult: 1.25f, DmgTakenMult: 1.25f)),
        // 오만
        new(new TraitDef("SKL_LEISURE", "여유(스킬)"), "PER_ARROGANT", 1,
            "서두를 이유가 없다 — HP 60% 이상이고 우세할 때 정지 회복 +50% (主—)",
            Passive: new PassiveSpec("LEISURE", PassiveTrigger.SelfHpAboveWinning, Threshold: 0.60f, IdleRegenMult: 1.5f)),
        new(new TraitDef("SKL_IMPERIAL", "황제의 위압(스킬)"), "PER_ARROGANT", 2,
            "조롱이 곧 권위다 — 도발 성공 직후 5초간 크리율 +15%, 상대 분노 2배 (集정관+ · 主ATK)",
            Passive: new PassiveSpec("IMPERIAL", PassiveTrigger.AfterTaunt, Duration: 5f, CritAdd: 0.15f)),
        // 고결
        new(new TraitDef("SKL_FAIRFIGHT", "정정당당(스킬)", GuardDamageMult: 0.75f, PoiseMaxMult: 1.20f), "PER_HONORABLE", 1,
            "정면으로만 이긴다 — 가드 효율 +25%·포이즈 +20% / 다운·빈사 상대 처형 거부 (主DEF)",
            Passive: new PassiveSpec("FAIRFIGHT", PassiveTrigger.Always, VetoExecution: true)),
        new(new TraitDef("SKL_CHIVALRY", "기사도의 보답(스킬)"), "PER_HONORABLE", 2,
            "불리할수록 곧게 선다 — HP가 15%p 이상 뒤질 때 전 능력 +12% (集정관+)",
            Passive: new PassiveSpec("CHIVALRY", PassiveTrigger.HpDeficit, Threshold: 0.15f,
                DmgDealtMult: 1.12f, MoveMult: 1.12f, AtkSpeedMult: 1.12f)),
        // 겁쟁이
        new(new TraitDef("SKL_SURVIVE", "생존 본능(스킬)"), "PER_COWARD", 1,
            "죽음의 냄새를 먼저 맡는다 — 상대 강공 선딜 인지 시 회피 무적 0.45초·성공 시 스태미나 50% 환급 (proc CD 4s·ST15 · 主SPD)",
            // [7] 초안 ST15 선불 — 창만 열고 회피가 안 나오면 순손해(skillprobe Δ −22.5%p)라 선불 폐지.
            // 비용은 회피 자체가 이미 치르고, 이 패시브는 '성공 시 환급'만 준다(문서의 이득 부분 유지).
            Passive: new PassiveSpec("SURVIVE", PassiveTrigger.OppHeavyWindup, ProcCdSec: 4f, Duration: 1.2f,
                DodgeIFrameAdd: 0.15f, DodgeRefundPct: 0.5f)),
        new(new TraitDef("SKL_BACKSTAB", "기회의 일격(스킬)"), "PER_COWARD", 2,
            "등을 노리는 데 부끄러움은 없다 — 상대 후딜에 기습 확정 크리 ×1.6 (CD 14s · 集정관+ · 主ATK)",
            // [7] 초안 ×2 — skillprobe Δ +25%p(과함). 확정 크리 자체가 이미 강해 배수만 낮춤.
            Passive: new PassiveSpec("BACKSTAB", PassiveTrigger.OppRecovery, ProcCdSec: 22f, Duration: 1.5f,
                ForceCrit: true, CritDmgMult: 1.4f)),
        // 쇼맨
        new(new TraitDef("SKL_CROWD", "관중몰이(스킬)"), "PER_SHOWMAN", 1,
            "환호가 힘이 된다 — 크리·강공 마무리·도발마다 군중 1스택(최대 5), 스택당 능력 +3% (proc CD 2s)",
            Passive: new PassiveSpec("CROWD", PassiveTrigger.OnCritOrHeavyOrTaunt, ProcCdSec: 2f,
                StackMax: 5, PerStackDmg: 0.03f)),
        new(new TraitDef("SKL_SHOWTIME", "쇼타임(스킬)"), "PER_SHOWMAN", 2,
            "무대는 지금부터다 — 군중 5스택을 태워 8초간 전 능력 +20% (CD 30s · 集정관+ · 主ATK)",
            Passive: new PassiveSpec("SHOWTIME", PassiveTrigger.CrowdStackFull, ProcCdSec: 30f, Duration: 8f,
                DmgDealtMult: 1.20f, MoveMult: 1.20f, AtkSpeedMult: 1.20f)),
        // 기회주의자
        new(new TraitDef("SKL_EXPLOIT", "약점 포착(스킬)"), "PER_OPPORTUNIST", 1,
            "무너진 곳만 친다 — 가드붕괴·지침·스태거 상대에게 주는 피해 +15% (主ATK)",
            // [7] 초안은 +30% — 상시조건이 자주 참이라 skillprobe Δ +42.5%p(위험). 절반으로 낮춰 정상권.
            Passive: new PassiveSpec("EXPLOIT", PassiveTrigger.OppVulnerable, DmgDealtMult: 1.08f)),
        new(new TraitDef("SKL_VULTURE", "어부지리(스킬)"), "PER_OPPORTUNIST", 2,
            "빈사의 살점을 채간다 — 상대 HP 20% 이하에서 처형 대시 강공 (ST25 / CD18s · 集정관+ · 主SPD)",
            Passive: new PassiveSpec("VULTURE", PassiveTrigger.OppHpBelow, Threshold: 0.20f, Prob: 0.7f,
                ProcCdSec: 18f, StCost: 25f, DashStrike: true)),
        // 잔혹
        new(new TraitDef("SKL_BLOODLUST", "피의 갈증(스킬)"), "PER_CRUEL", 1,
            "피를 마신다 — 출혈 중이거나 HP 30% 이하인 상대를 가격하면 피해의 20% 흡혈 (proc CD 3s · 主ATK)",
            Passive: new PassiveSpec("BLOODLUST", PassiveTrigger.OnDealHit, ProcCdSec: 3f,
                LifestealPct: 0.20f, LifestealOppHpBelow: 0.30f)),
        new(new TraitDef("SKL_TERROR", "공포 군림(스킬)"), "PER_CRUEL", 2,
            "존재만으로 얼어붙는다 — 상대 HP가 25%씩 깎일 때마다 공포 누적(공격성 −0.15, 최대 3단) (集정관+)",
            Passive: new PassiveSpec("TERROR", PassiveTrigger.OppHpStep, Threshold: 0.25f,
                FearAggPerStack: -0.15f, FearStackMax: 3)),
        // 대담
        new(new TraitDef("SKL_NERVE", "배짱(스킬)"), "PER_BOLD", 1,
            "휘두른 뒤가 짧다 — 강공 뒤 후딜 −25% (proc CD 5s)",
            Passive: new PassiveSpec("NERVE", PassiveTrigger.AfterHeavySwing, ProcCdSec: 5f, Duration: 2f,
                RecoveryMult: 0.75f)),
        new(new TraitDef("SKL_COMEBACK", "역전의 영웅(스킬)"), "PER_BOLD", 2,
            "끝이 보일 때 타오른다 — 잔여 시간 25% 이하에서 뒤지고 있으면 전 능력 +18% (集정관+)",
            Passive: new PassiveSpec("COMEBACK", PassiveTrigger.TimeLowAndLosing, Threshold: 0.25f,
                DmgDealtMult: 1.18f, MoveMult: 1.18f, AtkSpeedMult: 1.18f)),
        // 신중
        new(new TraitDef("SKL_GUARDED", "방비(스킬)", DodgeCostMult: 0.60f, GuardDamageMult: 0.85f), "PER_WARY", 1,
            "지지 않는 것이 먼저다 — 가드·회피 소모 −40%·가드 효율 +15% / 공격성 −0.15 (主DEF)",
            Passive: new PassiveSpec("GUARDED", PassiveTrigger.Always, AggressionAdd: -0.15f)),
        new(new TraitDef("SKL_FORESEE", "함정 간파(스킬)"), "PER_WARY", 2,
            "오의를 읽는다 — 상대가 액티브를 쓴 직후 1초간 카운터 창 +0.4·피해 +25% (CD 10s · 集정관+ · 主RCT)",
            Passive: new PassiveSpec("FORESEE", PassiveTrigger.OppSkillActivated, ProcCdSec: 10f, Duration: 1f,
                CounterWindowAdd: 0.4f, DmgDealtMult: 1.25f)),

        // ── 무기 액티브([7]§4 카탈로그, 8무기 × 2) — 전용 모션 없이 기존 프리미티브로 전량 구현.
        //    코스트/CD/확률/트리 = 문서 수치. 공간 수치는 ×1.5 현행 스케일 환산. 전용 연출은 애니메이션 트랙에서.
        // 검
        new(new TraitDef("SKL_COMBO", "연격(스킬)"), "", 1,
            "베기가 베기를 부른다 — 3.5초간 공격 속도 +35%, 상대가 멀어지면 조기 종료 (ST20 / 9s · 主ATK)",
            new ActiveSpec("COMBO", SkillTrigger.OppGuardGaugeBelow, 0.70f, 0.5f, 3.5f, 9f,
                StCost: 20f, AttackSpeedMult: 1.35f, EarlyEndGapM: 3.0f), GateWeapon: "WPN_SWORD"),
        new(new TraitDef("SKL_GUARDSTANCE", "반격 태세(스킬)"), "", 2,
            "막아낸 그 순간이 기회다 — 3초 자세, 그동안 가드에 성공하면 즉시 반격 (CD만 / 20s · 主RCT)",
            new ActiveSpec("GUARDSTANCE", SkillTrigger.OppHeavyWindupOrPress, 2.25f, 0.7f, 3f, 20f, ActiveKind.Stance,
                CounterOnGuard: true), GateWeapon: "WPN_SWORD"),
        // 창 — 카이터 복원 핵심([7])
        new(new TraitDef("SKL_REACHPUSH", "긴 창(스킬)"), "", 1,
            "창대를 고쳐 쥔다 — 4초간 리치 +0.4 + 즉발 밀어내기 (ST20 / 12s · 主SPD)",
            new ActiveSpec("REACHPUSH", SkillTrigger.GapBand, 0f, 0.6f, 4f, 12f,
                StCost: 20f, GapMinM: 1.6f, GapMaxM: 4.2f,
                RangeAddM: 0.4f, KnockbackM: 1.2f), GateWeapon: "WPN_SPEAR"),
        new(new TraitDef("SKL_ZONELOCK", "공간 지배(스킬)"), "", 2,
            "이 원 안은 내 것이다 — 6초간 사거리 진입자 자동 견제(약공 ×0.6 / 0.8s) + 카이팅 ST 면제 (CD만 / 26s · 主SPD)",
            new ActiveSpec("ZONELOCK", SkillTrigger.GapBand, 0f, 0.6f, 6f, 26f,
                GapMinM: 0f, GapMaxM: 5.0f, KiteExempt: true,
                AutoPokeMult: 0.6f, AutoPokeIntervalSec: 0.8f), GateWeapon: "WPN_SPEAR"),
        // 도끼
        new(new TraitDef("SKL_SUNDER", "분쇄 일격(스킬)"), "", 1,
            "가드째 부순다 — 다음 강공이 가드를 무조건 파괴 + 출혈 (ST22 / 11s · 主ATK, 5초 내 미사용 시 소멸)",
            new ActiveSpec("SUNDER", SkillTrigger.OppGuarding, 0f, 0.6f, 5f, 11f,
                StCost: 22f, SunderNextHeavy: true), GateWeapon: "WPN_AXE"),
        new(new TraitDef("SKL_BERSERK", "광전사의 도끼(스킬)"), "", 2,
            "제 피를 값으로 치른다 — HP 5% 자해, 8초간 공격력 +0.8%/(부족 HP%p) 최대 +40%·받는 피해 +25% (26s · 主ATK)",
            new ActiveSpec("BERSERK", SkillTrigger.SelfHpBelow, 0.50f, 0.7f, 8f, 26f,
                SelfHpPctCost: 0.05f, DmgTakenMult: 1.25f, AtkPerMissingHpPct: 0.008f, AtkCap: 0.40f), GateWeapon: "WPN_AXE"),
        // 대검
        new(new TraitDef("SKL_CHARGE", "쇄도 베기(스킬)"), "", 1,
            "거리를 지우는 돌진 — 상대에게 짓쳐들어 강공 일격 (ST22 / 10s · 主ATK)",
            new ActiveSpec("CHARGE", SkillTrigger.GapBand, 0f, 0.55f, 0f, 10f, ActiveKind.Strike,
                StCost: 22f, GapMinM: 3.0f, GapMaxM: 6.0f, DashIn: true, StrikeHeavy: true), GateWeapon: "WPN_GREATSWORD"),
        new(new TraitDef("SKL_UNBROKEN", "불퇴의 자세(스킬)"), "", 2,
            "물러서지 않는다 — 5초간 포이즈 무한(스태거·넉백 면역, 가드붕괴·다운은 아님) (CD만 / 24s · 主DEF)",
            new ActiveSpec("UNBROKEN", SkillTrigger.ConsecHitsTaken, 2f, 0.6f, 5f, 24f,
                PoiseImmune: true), GateWeapon: "WPN_GREATSWORD"),
        // 쌍검
        new(new TraitDef("SKL_FLURRY", "난무(스킬)"), "", 1,
            "허점에 칼비가 쏟아진다 — 경직·스태거 상대에 5연타(타당 약공 ×0.5) (ST28 / 10s · 主ATK)",
            new ActiveSpec("FLURRY", SkillTrigger.OppVulnerable, 0f, 0.55f, 0f, 10f, ActiveKind.Strike,
                StCost: 28f, StrikeDmgMult: 0.5f, StrikeHits: 5), GateWeapon: "WPN_DUALBLADES"),
        new(new TraitDef("SKL_MIRAGE", "그림자 보(스킬)"), "", 2,
            "그림자만 남기고 사라진다 — 상대 배후로 이동 + 다음 약공 확정 크리 (ST20 / 20s · 主SPD)",
            new ActiveSpec("MIRAGE", SkillTrigger.OppHeavyWindupOrRecovery, 0f, 0.6f, 0f, 20f, ActiveKind.Strike,
                StCost: 20f, TeleportBehind: true, NextLightCritSec: 3f), GateWeapon: "WPN_DUALBLADES"),
        // 망치
        new(new TraitDef("SKL_SMASH", "대지 강타(스킬)"), "", 1,
            "땅째 부수는 일격 — 강공 ×1.3 + 가드관통 50% + 명중 시 스태거 (ST22 / 11s · 主ATK)",
            new ActiveSpec("SMASH", SkillTrigger.InRange, 0f, 0.5f, 0f, 11f, ActiveKind.Strike,
                StCost: 22f, StrikeHeavy: true, StrikeDmgMult: 1.3f, GuardPierce: 0.5f, StaggerOnHitSec: 0.8f), GateWeapon: "WPN_HAMMER"),
        new(new TraitDef("SKL_EXECUTE", "심판의 일격(스킬)"), "", 2,
            "빈사의 상대에게 심판이 내린다 — 1.2초 무방비 차지 후 강공 ×2.5, HP 15% 미만이면 즉사 (CD만 / 28s · 主ATK, 고결은 거부)",
            new ActiveSpec("EXECUTE", SkillTrigger.OppExecutable, 0.35f, 0.8f, 1.2f, 28f, ActiveKind.Charge,
                ChargeSec: 1.2f, ExecuteDmgMult: 2.5f, ExecuteKillPct: 0.15f, VetoExecution: true), GateWeapon: "WPN_HAMMER"),
        // 채찍 — 카이터 복원 핵심([7])
        new(new TraitDef("SKL_LASH", "채찍 후리기(스킬)"), "", 1,
            "가죽이 다리를 훑는다 — 피해 + 3초간 이동 속도 −25% (ST18 / 10s · 主SPD)",
            new ActiveSpec("LASH", SkillTrigger.GapBand, 0f, 0.6f, 0f, 10f, ActiveKind.Strike,
                StCost: 18f, GapMinM: 2.0f, GapMaxM: 4.5f, SlowMult: 0.75f, SlowSec: 3f), GateWeapon: "WPN_WHIP"),
        new(new TraitDef("SKL_ENTANGLE", "휘감기(스킬)"), "", 2,
            "가죽이 발목을 삼킨다 — 피해 + 멀면 끌어당김·가까우면 1초 이동봉쇄 (ST20 / 12s · 主SPD)",
            new ActiveSpec("ENTANGLE", SkillTrigger.GapBand, 0f, 0.6f, 0f, 12f, ActiveKind.Strike,
                StCost: 20f, GapMinM: 3.0f, GapMaxM: 4.5f, PullM: 1.2f, RootSec: 1.0f), GateWeapon: "WPN_WHIP"),
        // 방패
        new(new TraitDef("SKL_SHIELDBASH", "방패 밀치기(스킬)"), "", 1,
            "방패가 무기가 되는 순간 — 돌진 방패치기: 가드붕괴 + 다운(면역이면 붕괴만) (ST25 / 20s · 主DEF)",
            new ActiveSpec("SHIELDBASH", SkillTrigger.OppGuardingOrStunned, 0f, 0.6f, 0f, 20f, ActiveKind.Strike,
                StCost: 25f, DashIn: true, BashBreak: true, DownSec: 1.5f), GateWeapon: "WPN_SHIELD"),
        new(new TraitDef("SKL_CARRY", "몰아붙이기(스킬)"), "", 2,
            "방패로 떠밀어 벽까지 몰고 간다 — 상대를 2.2m 밀어내고, 경계에 처박으면 강타 ×1.6 + 스태거 (ST30 / 24s · 主DEF)",
            new ActiveSpec("CARRY", SkillTrigger.OppGuardingOrStunned, 0f, 0.6f, 0f, 24f, ActiveKind.Strike,
                StCost: 30f, DashIn: true, CarryM: 2.2f, WallSlamDmgMult: 1.6f,
                StaggerOnHitSec: 0.8f), GateWeapon: "WPN_SHIELD"),
    };

    private static readonly Dictionary<string, SkillDef> _byId = All.ToDictionary(s => s.Def.Id);
    public static SkillDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}

/// <summary>
/// 선천 스킬 부여 — 스킬은 <b>수련으로 익히는 것이 아니라 타고나는 것</b>(라니스타 결정).
/// 슬롯 상한은 없다. 자격 있는 스킬마다 독립적으로 확률을 굴리므로 개수는 0~4로 흩어진다.
///
/// 자격(=기존 게이트 유지): 액티브는 무기 일치, 패시브는 성격 일치, Ⅱ급은 집정관 이상.
/// 그래서 노예는 최대 2개(Ⅰ급 액티브·패시브), 집정관 이상은 최대 4개가 자연히 나온다.
/// 확률은 천부 등급에 비례 — 좋은 그릇일수록 재능을 타고날 여지가 크되, 집정관이 0개일 수도 있다.
/// 결정론: 주입된 SimRandom으로만 굴린다.
/// </summary>
public static class SkillGen
{
    // 천부 등급별 부여 확률 (노예→카이사르 순, TalentGrade 인덱스)
    private static readonly float[] Tier1Prob = { 0.25f, 0.31f, 0.37f, 0.43f, 0.49f, 0.55f };
    private static readonly float[] Tier2Prob = { 0f, 0f, 0f, 0.10f, 0.20f, 0.30f };

    /// <summary>bastard = '사생아' 특성 — 천부 등급을 넘는 스킬을 지닌다([7]§6 계급 천장 예외).</summary>
    public static string[] Roll(SimRandom rng, string weaponId, string personalityId,
                               TalentGrade talent, bool bastard = false)
    {
        int ti = (int)talent;
        var picked = new List<string>(4);
        foreach (var sk in SkillTable.All)
        {
            // 자격 심사 — 액티브(무기 결합) / 패시브(성격 결합)
            bool eligible = sk.GateWeapon != null ? sk.GateWeapon == weaponId
                                                  : sk.GatePersonality == personalityId;
            if (!eligible) continue;
            // Ⅱ급 계급 천장 — 사생아는 이 천장을 무시한다
            bool tier2 = sk.RankTier >= 2;
            if (tier2 && !bastard && ti < SkillTable.Tier2MinTalent) continue;
            // 사생아가 천장을 넘어 받을 때는 집정관 몫의 확률을 쓴다(등급이 그보다 낮아도)
            float p = tier2 ? Tier2Prob[Math.Max(ti, bastard ? SkillTable.Tier2MinTalent : ti)]
                            : Tier1Prob[ti];
            if (rng.NextFloat01() < p) picked.Add(sk.Def.Id);
        }
        return picked.ToArray();
    }
}

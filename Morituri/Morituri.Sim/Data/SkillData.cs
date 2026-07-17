namespace Morituri.Sim.Data;

/// <summary>
/// T12 패시브 스킬 MVP (문서[6]§3.1 "엔진은 하나, 경제는 둘" · [7]§5).
/// 스킬 = <b>장착형 특성 행</b> — TraitDef를 그대로 재사용해 Sim 훅 추가가 없다(원칙 A/C).
/// 특성(T09)과의 차이는 경제뿐: 특성=타고남·탈부착 불가 / 스킬=성격·천부 게이트 안에서 습득·교체(Game 층 슬롯).
/// 생성 추첨 풀(TraitGen)에는 절대 들어가지 않는다 → 미장착 세계의 매트릭스는 구조적으로 불변.
/// 수치 원칙: 타고난 특성보다 항상 약하게(스킬은 보완재, [6]§3.2 "낮은 계급에도 유용한 것").
/// </summary>
/// <summary>액티브 스킬 발동 조건 — 전부 결정적(난수 없음 = 리플레이·매트릭스 안전).</summary>
public enum ActiveTrigger
{
    HpBelow,        // 자기 HP 비율이 임계 이하로 떨어지는 순간
    StaminaBelow,   // 자기 스태미나 비율이 임계 이하
    AfterDown,      // 다운에서 일어나는 순간(GetUp)
}

/// <summary>
/// 액티브 스킬 명세 — <b>모리튜리가 스스로 발동</b>한다(감독 개입 불가, 조건 충족 시 자동).
/// 효과는 시한 배율(이속·가피·받피·스태미나 회복)만 — 기존 소비 지점에 곱해질 뿐 새 수식 없음.
/// </summary>
public sealed record ActiveSpec(
    ActiveTrigger Trigger, float Threshold, float Duration, float Cooldown,
    float MoveMult = 1f, float DmgDealtMult = 1f, float DmgTakenMult = 1f,
    float StamRegenMult = 1f, float StamRestore = 0f);

public sealed record SkillDef(
    TraitDef Def,              // 효과 본체(TraitDef 재사용 — MatchSim이 특성과 동일 파이프로 소비)
    string GatePersonality,    // 패시브 = 성격 결합([7]§5). 습득 시점의 성격이 게이트
    int RankTier,              // Ⅰ=1(전 천부) / Ⅱ=2(집정관 이상 — [6]§1.5 접근권)
    string Desc,
    ActiveSpec? Active = null); // null=패시브. 액티브는 TraitDef 배율이 전부 1(상시 효과 없음)

public static class SkillTable
{
    /// <summary>Ⅱ급 접근에 필요한 최소 천부(집정관). Game 층이 TalentGrade와 비교.</summary>
    public const int Tier2MinTalent = 3;   // TalentGrade.Consul

    public static readonly SkillDef[] All =
    {
        // ── Ⅰ급 (전 천부) — 성격당 1종, 타고난 특성의 절반 이하 강도 ──
        new(new TraitDef("SKL_READ",    "간파(스킬)",    PerceptDelayAdd: -0.04f),                       "PER_CALM",        1, "상대의 예비동작을 읽는다 — 인지지연 −0.04s"),
        new(new TraitDef("SKL_RUSH",    "저돌(스킬)",    MoveSpeedMult: 1.06f),                          "PER_RECKLESS",    1, "생각보다 몸이 먼저 — 이동속도 +6%"),
        new(new TraitDef("SKL_LEISURE", "여유(스킬)",    StamRegenMult: 1.15f),                          "PER_ARROGANT",    1, "서두르지 않는 자의 호흡 — 스태미나 회복 +15%"),
        new(new TraitDef("SKL_AEGIS",   "수호자(스킬)",  GuardDamageMult: 0.80f),                        "PER_HONORABLE",   1, "정면으로 받아낸다 — 가드 시 받는 피해 −20%"),
        new(new TraitDef("SKL_SURVIVE", "생존술(스킬)",  DamageTakenMult: 0.96f, DodgeCostMult: 0.85f),  "PER_COWARD",      1, "맞지 않는 것이 이기는 것 — 받피 −4%·회피 소모 −15%"),
        new(new TraitDef("SKL_VIGOR",   "살육의 활력(스킬)", StaminaMaxMult: 1.10f),                     "PER_CRUEL",       1, "피 냄새가 힘이 된다 — 스태미나 최대 +10%"),
        new(new TraitDef("SKL_NERVE",   "배짱(스킬)",    PoiseMaxMult: 1.12f),                           "PER_BOLD",        1, "물러서지 않는 심장 — 포이즈 +12%"),
        new(new TraitDef("SKL_ECONOMY", "절제(스킬)",    DodgeCostMult: 0.70f),                          "PER_WARY",        1, "낭비 없는 몸놀림 — 회피 스태미나 소모 −30%"),
        new(new TraitDef("SKL_FLAIR",   "무대 장악(스킬)", MoveSpeedMult: 1.04f, RangeAdd: 0.08f),       "PER_SHOWMAN",     1, "관중이 보는 각도를 안다 — 이속 +4%·간격 +0.08"),
        new(new TraitDef("SKL_ANGLE",   "노림수(스킬)",  PerceptDelayAdd: -0.03f, MoveSpeedMult: 1.03f), "PER_OPPORTUNIST", 1, "허점이 열리는 순간을 기다린다 — 인지 −0.03s·이속 +3%"),
        // ── Ⅱ급 (집정관 이상) — 더 강하지만 접근권이 좁다 ──
        new(new TraitDef("SKL_READ2",   "전장 분석(스킬)", PerceptDelayAdd: -0.08f),                     "PER_CALM",        2, "전장 전체가 느리게 보인다 — 인지지연 −0.08s"),
        new(new TraitDef("SKL_BULWARK", "불괴(스킬)",    GuardDamageMult: 0.65f, MoveSpeedMult: 0.96f),  "PER_HONORABLE",   2, "무너지지 않는 방벽 — 가드 받피 −35%·이속 −4%"),

        // ── 액티브 (모리튜리 자동 발동 — 감독 개입 불가, 조건은 전부 결정적) ──
        new(new TraitDef("SKL_SECONDWIND", "재기의 호흡(스킬)"), "PER_BOLD",     1, "궁지에서 숨을 고른다 — HP 35% 이하 시 스태미나 +30·8초간 회복 +50% (경기당 1회)",
            new ActiveSpec(ActiveTrigger.HpBelow, 0.35f, 8f, 999f, StamRegenMult: 1.5f, StamRestore: 30f)),
        new(new TraitDef("SKL_FRENZY",     "핏빛 광란(스킬)"),   "PER_CRUEL",    1, "제 피 냄새에 눈이 뒤집힌다 — HP 50% 이하 시 6초간 가하는 피해 +10%·받는 피해 +5% (경기당 1회)",
            new ActiveSpec(ActiveTrigger.HpBelow, 0.50f, 6f, 999f, DmgDealtMult: 1.10f, DmgTakenMult: 1.05f)),
        new(new TraitDef("SKL_BURST",      "질풍(스킬)"),        "PER_RECKLESS", 1, "쓰러진 몸이 먼저 튀어나간다 — 다운에서 일어날 때 6초간 이속 +10% (재사용 20초)",
            new ActiveSpec(ActiveTrigger.AfterDown, 0f, 6f, 20f, MoveMult: 1.10f)),
        new(new TraitDef("SKL_LASTSTAND",  "최후의 보루(스킬)"), "PER_HONORABLE", 2, "무너지기 직전이 가장 단단하다 — HP 30% 이하 시 8초간 받는 피해 −15% (경기당 1회)",
            new ActiveSpec(ActiveTrigger.HpBelow, 0.30f, 8f, 999f, DmgTakenMult: 0.85f)),
    };

    private static readonly Dictionary<string, SkillDef> _byId = All.ToDictionary(s => s.Def.Id);
    public static SkillDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}

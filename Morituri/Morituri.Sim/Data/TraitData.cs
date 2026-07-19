using Morituri.Sim.Core;

namespace Morituri.Sim.Data;

/// <summary>
/// T09 특성 (문서[7]§6, [11]§5.5). 타고난 정체성 — 생성 시 자동 부여, 탈부착 불가, 슬롯 무제한.
/// 대부분 파생스탯/전투 배율(데이터)로 표현. 일부는 고유 행동(Id로 분기) — 좀비·숨고르기·초상비·선취점.
/// 배타: 같은 ExclAxis에서 반대 ExclPolarity인 특성쌍은 생성 시 동시 부여 금지(상반 특성).
/// </summary>
public sealed record TraitDef(
    string Id, string Name,
    string ExclAxis = "", int ExclPolarity = 0,
    float HpMaxMult = 1f, float StaminaMaxMult = 1f, float StamRegenMult = 1f,
    float MoveSpeedMult = 1f, float PoiseMaxMult = 1f, float PerceptDelayAdd = 0f,
    float DamageTakenMult = 1f, float DodgeCostMult = 1f, float RangeAdd = 0f, float SizeScale = 1f,
    float GuardDamageMult = 1f, float RangeMult = 1f,   // RangeMult: 사거리 비례 배율(거인)   // 가드 성공 시 받는 피해 추가 배율(봉쇄자 <1). 일반 받피(DamageTakenMult)와 별개로 가드에만 적용
    // ── [7]§6.2 미구현분 구현([7] 표 순서) ──
    float AttackSpeedMult = 1f,        // 광란: 공속 배율(모션 시간 ÷) — 스킬 연격과 같은 파이프
    float DamageDealtMult = 1f,        // 광란의 대가. [7]은 '명중 −10%'지만 이 엔진엔 명중 판정이 없어
                                       // 기대 피해가 같은 형태(피해 −10%)로 옮겼다(코드 주석 근거 명기 규약)
    float SkillCooldownMult = 1f,      // 빠른손: 모든 액티브 CD 배율
    float CounterWindowAdd = 0f,       // 반격가: 카운터 창 +(캡 +0.6 [7]§2 공유)
    float CounterDamageMult = 1f,      // 반격가: 카운터 피해 배율
    float NonCounterDamageMult = 1f,   // 반격가의 대가: 카운터가 아닌 피해 배율
    float EmotionResistMult = 1f,      // 강심장: 공포·분노·도발 트리거 확률 배율
    bool FearImmune = false);          // 겁없는자: 공포 완전 면역(강심장과 영역 구분 — [7]§6.2)

public static class TraitTable
{
    // ── 고유 행동 특성 Id (코드가 보유 여부로 분기) ──
    public const string Zombie      = "TRT_ZOMBIE";       // HP≤30%: 디버프 면역 (이속 감소는 데이터)
    public const string CatchBreath = "TRT_CATCHBREATH";  // 스태미나≥80%: 모든 공격 치명타
    public const string Lightfoot   = "TRT_LIGHTFOOT";    // 대시 ST소모↓(데이터) + 대시 후 1초 이속↑
    public const string FirstBlood  = "TRT_FIRSTBLOOD";   // 첫 클린 히트 ×1.25 + 흡수 쉴드
    // ── 신규 전투 특성(#16) — 조건부 데미지 배율(MatchSim Id 분기). 매트릭스는 특성 없는 baseline이라 무영향 ──
    public const string Executioner = "TRT_EXECUTIONER";  // 상대 HP≤30%: 데미지 ×1.5 (마무리)
    public const string Assassin    = "TRT_ASSASSIN";     // 클린히트 4%: 필살 ×2.4
    public const string Berserk     = "TRT_BERSERK";      // 자신 HP≤35%: 데미지 ×1.3 (궁지의 폭발)
    // ── 신규 메타 특성(#16) — 전투 무영향, Game.cs가 성장/노화에 적용 ──
    public const string Genius      = "TRT_GENIUS";       // 잠재력 상한 ×1.15 · 성장속도↑ (Meta)
    public const string SlowAge     = "TRT_SLOWAGE";      // 노화 감소 (Meta)
    // ── [7]§6.2 미구현분(라니스타 지시로 구현). 소집·분신은 제외 ──
    public const string Fearless    = "TRT_FEARLESS";     // 공포 완전 면역(데이터 FearImmune)
    public const string Veteran     = "TRT_VETERAN";      // 노련함 — 노화 능력↑ + 10년마다 특성 1개 추가 (Meta)
    public const string Bastard     = "TRT_BASTARD";      // 사생아 — 천부 등급을 넘는 Ⅱ급 스킬 보유 ([6]§1.5 천장 예외, Meta)

    // ── 카탈로그 ──
    public static readonly TraitDef[] All =
    {
        // 받피 축(ExclAxis "dmgTaken"): 유리몸(+1) ⊗ 질긴가죽·둔감(−1)
        new("TRT_BRITTLE",    "유리몸",      "dmgTaken", +1, DamageTakenMult: 1.20f),
        new("TRT_TOUGHHIDE",  "질긴가죽",    "dmgTaken", -1, DamageTakenMult: 0.88f, MoveSpeedMult: 0.92f),
        new("TRT_DULL",       "둔감",        "dmgTaken", -1, DamageTakenMult: 0.85f, PerceptDelayAdd: 0.06f),
        // 스태미나 축(ExclAxis "stamina"): 마르지않는샘(+1) ⊗ 허약체질(−1)
        new("TRT_SPRING",     "마르지않는샘", "stamina",  +1, StaminaMaxMult: 1.25f, StamRegenMult: 1.20f),
        new("TRT_FRAIL",      "허약체질",     "stamina",  -1, StaminaMaxMult: 0.75f, StamRegenMult: 0.70f),
        // 단독(배타 없음)
        new("TRT_FLEET",      "민첩",        MoveSpeedMult: 1.12f, HpMaxMult: 0.90f),
        new("TRT_STAND",      "불굴",        PoiseMaxMult: 1.30f),
        new("TRT_STONEWALL",  "봉쇄자",      GuardDamageMult: 0.50f),              // 방어 시 받는 피해 ×0.5(가드 한정)
        new("TRT_GLASSJAW",   "유리턱",      PoiseMaxMult: 0.60f),                 // 기절 잘 걸림
        new("TRT_GIANT",      "거인",        HpMaxMult: 1.25f, RangeMult: 1.25f, SizeScale: 1.30f),  // 비례 reach ×1.25(의미있는 선타 우위, 압살은 아님)
        new(Zombie,           "좀비",        MoveSpeedMult: 0.85f),                 // + 고유: HP≤30% 디버프 면역
        new(CatchBreath,      "숨고르기"),                                          // 고유: ST≥80% 확정 크리
        new(Lightfoot,        "초상비",      DodgeCostMult: 0.50f),                 // + 고유: 대시 후 이속↑
        new(FirstBlood,       "선취점"),                                            // 고유: 첫 클린히트 ×1.25 + 흡수쉴드
        // 신규 전투(조건부 데미지 — MatchSim Id 분기, 데이터 배율 없음)
        new(Executioner,      "처형자"),                                            // 고유: 상대 HP≤30% 데미지 ×1.5
        new(Assassin,         "일격필살"),                                          // 고유: 클린히트 4% 필살 ×2.4
        new(Berserk,          "광폭화"),                                            // 고유: 자신 HP≤35% 데미지 ×1.3
        // 신규 메타(전투 무영향 — Game.cs 적용)
        new(Genius,           "천재"),                                              // Meta: 잠재 상한 ×1.15·성장↑
        new(SlowAge,          "저속노화"),                                          // Meta: 노화 감소
        // ── [7]§6.2 미구현분 ──
        // 감정 축(ExclAxis "emotion"): 겁없는자·강심장(−1) ⊗ (향후 감정 취약 특성 +1)
        new(Fearless,         "겁없는자",   "emotion", -1, FearImmune: true),
        new("TRT_STOIC",      "강심장",     "emotion", -1, EmotionResistMult: 0.40f),   // 공포·분노·도발 −60%
        new("TRT_FRENZY",     "광란",       AttackSpeedMult: 1.20f, DamageDealtMult: 0.90f),
        new("TRT_SWIFT",      "빠른손",     SkillCooldownMult: 0.85f),                  // 모든 액티브 CD −15%
        new("TRT_RIPOSTEUR",  "반격가",     CounterWindowAdd: 0.30f, CounterDamageMult: 1.30f,
                                            NonCounterDamageMult: 0.92f),
        new(Veteran,          "노련함"),                                            // Meta: 노화 완화 + 10년마다 특성 +1
        new(Bastard,          "사생아"),                                            // Meta: Ⅱ급 스킬 천장 예외
    };

    // 스킬(T12)도 같은 조회 파이프에 등록 — MatchSim은 특성과 스킬을 구분하지 않는다([6]§3.1 엔진 하나).
    // 생성 추첨(TraitGen)은 All만 쓰므로 스킬이 타고나는 일은 없다.
    private static readonly Dictionary<string, TraitDef> _byId =
        All.Concat(SkillTable.All.Select(s => s.Def)).ToDictionary(t => t.Id);
    public static TraitDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}

/// <summary>
/// 생성 시 특성 부여 (문서[11]§5.5): 1개(75%) / 2개(20%) / 3개(5%). 상반 배타.
/// 결정론 — 주입된 SimRandom으로만. (20세 +1 부여는 나이 시스템 도입 시 — 보류.)
/// </summary>
public static class TraitGen
{
    /// <summary>talent = 천부 등급. 조건부 특성을 추첨 단계에서 걸러낸다(무의미한 부여 방지).</summary>
    public static string[] Roll(SimRandom rng, TalentGrade talent = TalentGrade.Slave)
    {
        float r = rng.NextFloat01();
        int count = r < 0.05f ? 3 : r < 0.25f ? 2 : 1;   // 5% 3개 / 20% 2개 / 75% 1개

        var picked = new List<TraitDef>(count);
        // 사생아는 '천부 천장을 넘는다'가 전부라 이미 Ⅱ급을 담을 수 있는 집정관+에겐 아무 의미가 없다 → 후보에서 제외.
        // (빠른손은 스킬 추첨 뒤에야 의미를 알 수 있어 여기서 못 거른다 — Reconcile이 처리한다.)
        var pool = (int)talent >= SkillTable.Tier2MinTalent
                 ? TraitTable.All.Where(t => t.Id != TraitTable.Bastard).ToArray()
                 : TraitTable.All;
        int guard = 0;
        while (picked.Count < count && guard++ < 64)
        {
            var cand = pool[Math.Min(pool.Length - 1, (int)(rng.NextFloat01() * pool.Length))];
            if (picked.Contains(cand)) continue;
            // 상반 배타: 같은 축 반대 극성과 공존 불가
            if (cand.ExclAxis.Length > 0 &&
                picked.Any(p => p.ExclAxis == cand.ExclAxis && p.ExclPolarity == -cand.ExclPolarity))
                continue;
            picked.Add(cand);
        }
        return picked.Select(t => t.Id).ToArray();
    }
}

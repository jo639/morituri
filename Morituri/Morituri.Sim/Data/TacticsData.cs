namespace Morituri.Sim.Data;

// ───────────────────────── T02_WeaponMotions ─────────────────────────

public enum MotionKind { Light, Heavy }

/// <summary>
/// T02 모션 프레임 데이터 (M1 오픈 이슈 #3 해결).
/// WindupBaseSec는 ASPD 보정 전 기준값 — 실제 선딜 = CombatMath.MotionTime(WindupBaseSec, ...).
/// 무기 정체성 원칙: 선딜은 한방 무기일수록 길고, 후딜(T01.RecoverySec × RecoveryMult)이 카운터 창을 결정한다.
/// RecoveryMult (M3-A 추가): 약공 0.8 = 가드에 안전 / 강공 1.6 = 처벌 가능.
/// 동일 무기 매치업에서 "기다리는 전술"의 보상이 물리적으로 존재하려면 강공 후딜이
/// 인지지연+선딜(≈0.47s)보다 길어야 한다 — 검 0.45×1.6=0.72 ✓ (상성 매트릭스 디버깅으로 발견).
/// </summary>
public readonly record struct MotionDef(string Id, MotionKind Kind, float WindupBaseSec, float ActiveSec, float RecoveryMult);

public static class MotionTable
{
    private static readonly Dictionary<string, (MotionDef Light, MotionDef Heavy)> _map = new()
    {
        ["WPN_SWORD"]       = (new("MOT_SWORD_L", MotionKind.Light, 0.30f, 0.10f, 0.8f), new("MOT_SWORD_H", MotionKind.Heavy, 0.55f, 0.12f, 1.6f)),
        ["WPN_SPEAR"]       = (new("MOT_SPEAR_L", MotionKind.Light, 0.35f, 0.10f, 0.8f), new("MOT_SPEAR_H", MotionKind.Heavy, 0.60f, 0.12f, 1.6f)),
        // 중량 무기 강공 후딜 ×0.9 (<상대 Stagger 0.9s): 일격이 적중하면 상대가 굳은 동안 내가 먼저
        // 회복 → 추가타. "한 방 꽂으면 연계"라는 중량 무기 보상(문서[4] 11장)이 프레임상 성립한다.
        ["WPN_AXE"]         = (new("MOT_AXE_L",   MotionKind.Light, 0.45f, 0.12f, 0.8f), new("MOT_AXE_H",   MotionKind.Heavy, 0.50f, 0.15f, 0.9f)),
        ["WPN_GREATSWORD"]  = (new("MOT_GS_L",    MotionKind.Light, 0.45f, 0.14f, 0.8f), new("MOT_GS_H",    MotionKind.Heavy, 0.52f, 0.18f, 0.9f)),
        ["WPN_DUALBLADES"]  = (new("MOT_DB_L",    MotionKind.Light, 0.20f, 0.08f, 0.8f), new("MOT_DB_H",    MotionKind.Heavy, 0.40f, 0.10f, 1.6f)),
        ["WPN_HAMMER"]      = (new("MOT_HAM_L",   MotionKind.Light, 0.50f, 0.12f, 0.8f), new("MOT_HAM_H",   MotionKind.Heavy, 0.55f, 0.15f, 0.9f)),
        ["WPN_WHIP"]        = (new("MOT_WHIP_L",  MotionKind.Light, 0.30f, 0.10f, 0.8f), new("MOT_WHIP_H",  MotionKind.Heavy, 0.50f, 0.12f, 1.6f)),
        ["WPN_SWORDSHIELD"] = (new("MOT_SS_L",    MotionKind.Light, 0.32f, 0.10f, 0.8f), new("MOT_SS_H",    MotionKind.Heavy, 0.58f, 0.12f, 1.6f)),
    };

    public static MotionDef Get(string weaponId, MotionKind kind)
        => kind == MotionKind.Light ? _map[weaponId].Light : _map[weaponId].Heavy;
}

// ───────────────────────── 전술 파라미터 시스템 ─────────────────────────

/// <summary>Override가 만질 수 있는 전술 파라미터 (문서[3] 4.1 + 성격 효과용 확장 3종).</summary>
public enum TParam
{
    PreferredDistance, DistanceTolerance, Aggression, CommitThreshold, CounterWindow,
    GuardBias, FeintRate, RiskTolerance, StaminaReserve,
    // 성격 효과용 확장 (기본값 0 / 1)
    RepeatBias,     // 집착적: 같은 공격 반복 가중
    HeavyBias,      // 잔혹함/대담함: 강공 가중
    StamRegenMult,  // 낙천적: 스태미나 회복 배율 (기본 1.0)
    NoAttack,       // 명예중시 HoldOff: 1이면 공격 금지
}

/// <summary>파라미터 변경: Set(덮어쓰기) 또는 Delta(가감).</summary>
public readonly record struct ParamMod(TParam Param, bool IsSet, float Value)
{
    public static ParamMod Set(TParam p, float v) => new(p, true, v);
    public static ParamMod Add(TParam p, float v) => new(p, false, v);
}

/// <summary>
/// 전략층 출력 = 전술층 입력. 프로파일 + 성격 상시보정 + 활성 Override를 합성한 작업 사본.
/// </summary>
public struct Directive
{
    public float PreferredDistance, DistanceTolerance, Aggression, CommitThreshold, CounterWindow,
                 GuardBias, FeintRate, RiskTolerance, StaminaReserve,
                 RepeatBias, HeavyBias, StamRegenMult, NoAttack;

    public static Directive From(in TacticsProfile p) => new()
    {
        PreferredDistance = p.PreferredDistance,
        DistanceTolerance = p.DistanceTolerance,
        Aggression = p.Aggression,
        CommitThreshold = p.CommitThreshold,
        CounterWindow = p.CounterWindow,
        GuardBias = p.GuardBias,
        FeintRate = p.FeintRate,
        RiskTolerance = p.RiskTolerance,
        StaminaReserve = p.StaminaReserve,
        RepeatBias = 0f, HeavyBias = 0f, StamRegenMult = 1f, NoAttack = 0f,
    };

    public void Apply(in ParamMod m)
    {
        ref float f = ref PreferredDistance;
        switch (m.Param)
        {
            case TParam.PreferredDistance: f = ref PreferredDistance; break;
            case TParam.DistanceTolerance: f = ref DistanceTolerance; break;
            case TParam.Aggression:        f = ref Aggression; break;
            case TParam.CommitThreshold:   f = ref CommitThreshold; break;
            case TParam.CounterWindow:     f = ref CounterWindow; break;
            case TParam.GuardBias:         f = ref GuardBias; break;
            case TParam.FeintRate:         f = ref FeintRate; break;
            case TParam.RiskTolerance:     f = ref RiskTolerance; break;
            case TParam.StaminaReserve:    f = ref StaminaReserve; break;
            case TParam.RepeatBias:        f = ref RepeatBias; break;
            case TParam.HeavyBias:         f = ref HeavyBias; break;
            case TParam.StamRegenMult:     f = ref StamRegenMult; break;
            case TParam.NoAttack:          f = ref NoAttack; break;
        }
        if (m.IsSet) f = m.Value; else f += m.Value;
    }
}

// ───────────────────────── T03_Tactics ─────────────────────────

/// <summary>전술 프로파일 (문서[3] 4.2). UniqueRule = 전술 고유 조건 (프로파일당 최대 1개).</summary>
public sealed record TacticsProfile(
    string Id,
    float PreferredDistance, float DistanceTolerance,
    float Aggression, float CommitThreshold, float CounterWindow,
    float GuardBias, float FeintRate, float RiskTolerance, float StaminaReserve,
    TriggerRule? UniqueRule = null);

public static class TacticsTable
{
    public static readonly TacticsProfile Pressure  = new("TAC_PRESSURE",  1.0f, 0.55f, 0.70f, 0.35f, 0.10f, 0.20f, 0.15f, 0.50f, 0.10f);
    public static readonly TacticsProfile Counter   = new("TAC_COUNTER",   2.0f, 0.65f, 0.30f, 0.60f, 0.80f, 0.50f, 0.10f, 0.40f, 0.25f);
    public static readonly TacticsProfile Zoner     = new("TAC_ZONER",     2.8f, 0.75f, 0.45f, 0.50f, 0.30f, 0.30f, 0.50f, 0.30f, 0.30f);
    public static readonly TacticsProfile Brawler   = new("TAC_BRAWLER",   0.7f, 0.50f, 0.80f, 0.25f, 0.15f, 0.10f, 0.20f, 0.70f, 0.05f);
    public static readonly TacticsProfile Decision  = new("TAC_DECISION",  2.2f, 0.75f, 0.35f, 0.55f, 0.35f, 0.55f, 0.40f, 0.15f, 0.40f);
    public static readonly TacticsProfile Hunter    = new("TAC_HUNTER",    1.6f, 0.60f, 0.55f, 0.65f, 0.40f, 0.30f, 0.35f, 0.45f, 0.20f,
        // 전술 고유 조건 (문서[3] 4.2 메모): 상처 입은 사냥감 추적.
        // Phase 1 단순화: 상대 스태미나 고갈 인지는 미구현 → 상대 HP 35% 이하 조건만 사용.
        new TriggerRule("TRG_HUNT", TriggerCondition.OppHpBelowPct, 35f, TriggerEffectKind.Override,
            new[] { ParamMod.Add(TParam.Aggression, 0.30f) }, InterruptAction.None, 1.0f, 1.0f, 3.0f, "HUNT"));
    public static readonly TacticsProfile Gambler   = new("TAC_GAMBLER",   1.2f, 0.55f, 0.65f, 0.20f, 0.25f, 0.10f, 0.25f, 0.90f, 0.00f);
    public static readonly TacticsProfile Evader    = new("TAC_EVADER",    2.5f, 0.90f, 0.25f, 0.55f, 0.45f, 0.20f, 0.30f, 0.20f, 0.35f);
    public static readonly TacticsProfile Defender  = new("TAC_DEFENDER",  1.4f, 0.65f, 0.20f, 0.50f, 0.50f, 0.80f, 0.10f, 0.10f, 0.35f);
    public static readonly TacticsProfile Balanced  = new("TAC_BALANCED",  1.6f, 0.60f, 0.50f, 0.45f, 0.35f, 0.40f, 0.30f, 0.40f, 0.20f);

    public static readonly TacticsProfile[] All =
        { Pressure, Counter, Zoner, Brawler, Decision, Hunter, Gambler, Evader, Defender, Balanced };

    public static TacticsProfile Get(string id) => Array.Find(All, t => t.Id == id)!;
}

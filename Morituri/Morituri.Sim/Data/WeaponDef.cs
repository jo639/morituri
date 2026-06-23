namespace Morituri.Sim.Data;

/// <summary>
/// T01_Weapons 스키마 (문서[5] 2장 = 문서[4] 7장의 정규화 + PoiseMax 신규 컬럼).
/// PoiseMax 결정 근거 (M1 오픈 이슈 #1): 도끼 강공 Poise 피해 = 30×1.5 = 45를 기준선으로,
/// 경량 무기(창/쌍검/채찍) 사용자는 1방 Stagger, 검(50)은 1방을 버티고, 중량은 안정.
/// → 문서[4] 11장 "도끼 적중 → 창 Stagger" 시나리오 성립.
/// </summary>
public readonly record struct WeaponDef(
    string Id,
    float BaseDamage,
    int HitCount,            // 쌍검 = 2타 (26×2), 그 외 1
    float Range,             // 사거리 (m)
    float MotionSpeed,       // 모션 속도 계수
    float RecoverySec,       // 후딜 (초) — 카운터 기회의 크기
    float PoiseDamage,
    float PoiseMax,          // 사용자 Poise 최대치
    float GuardCrush,        // 가드 게이지 깎기 계수
    float GuardGaugeBonus,   // 방패검 +0.6, 그 외 0
    float GuardBypass,       // 채찍 0.1 (가드 우회), 그 외 0
    bool HyperArmor = false, // 중량 무기(대검/망치): 강공 선딜 중 약공에 안 끊김 (M3-A2)
    float HeavyBias = 0f,    // 강공 선호 가중 — 중량 무기의 정체성은 약공 견제가 아니라 한 방 (M3-A2)
    float MoveSpeedMult = 1.0f, // 이동속도 배율 — 무기 중량 표현 노브 (1.0 = 기본, <1 둔중, >1 날렵)
    float BleedDps = 0f,     // 출혈: 클린 히트당 1스택, 스택당 초당 피해(도끼 전용). 최대 BleedMaxStacks 합산, BleedDurationSec 지속
    float ParryWindowSec = 0f, // 패링(방패 전용): 가드 진입 후 이 시간 내 피격 시 무효+환급+프레임우위. 값↑ = 패링 성공률↑
    float EngageRangeRatio = 0.8f,   // [안E] AI 유지 교전거리 = min(PreferredDistance, EffRange×이값). 카이터↑(사거리 끝 유지)·인파이터 기본
    float PostAttackRetreatSec = 0f); // [안B] 공격 종료 후 이 시간만큼 후퇴 강제(찌르고 빠짐). 카이터 전용(창·채찍)

/// <summary>Phase 1 무기 8종. M1은 하드코딩, M3에서 CSV 임포터가 같은 구조체를 채운다.</summary>
public static class WeaponTable
{
    //                                                  id           base hit  rng   mSpd   rcv   pDmg pMax  crush  gBon  byps  hyper  hvBias  movMult
    public static readonly WeaponDef Sword       = new("WPN_SWORD",       33f, 1, 1.6f, 1.00f, 0.45f, 18f, 50f, 0.30f, 0f,    0f,   false, 0f,    1.00f);
    public static readonly WeaponDef Spear       = new("WPN_SPEAR",       38f, 1, 2.6f, 0.95f, 0.55f, 14f, 40f, 0.25f, 0f,    0f,   false, 0f,    1.00f, EngageRangeRatio: 0.92f, PostAttackRetreatSec: 0.50f);
    // 실험(2026-06-23): 무기별 이동속도 차등 — 동속 lockstep 깨기. movMult 스프레드 0.82~1.15(가벼울수록 빠름).
    public static readonly WeaponDef Axe         = new("WPN_AXE",         64f, 1, 1.4f, 0.70f, 0.85f, 30f, 60f, 0.55f, 0f,    0f,   false, 1.5f,  0.88f, 1.0f); // 출혈·버스트 — 하이퍼아머 없음(끊길 수 있는 고위험 한방) + 클린 히트당 출혈 1.0/s 스택(최대 3/s)
    public static readonly WeaponDef Greatsword  = new("WPN_GREATSWORD",  55f, 1, 2.0f, 0.75f, 0.75f, 26f, 65f, 0.45f, 0f,    0f,   true,  1.5f,  0.88f);
    public static readonly WeaponDef DualBlades  = new("WPN_DUALBLADES",  18f, 2, 1.1f, 1.10f, 0.42f, 10f, 35f, 0.15f, 0f,    0f,   false, 0f,    1.15f);
    public static readonly WeaponDef Hammer      = new("WPN_HAMMER",      58f, 1, 1.3f, 0.65f, 0.90f, 45f, 70f, 0.60f, 0f,    0f,   true,  1.5f,  0.82f); // 높은 경직 + 하이퍼아머(중량 무기의 '멈출 수 없는 일격')
    public static readonly WeaponDef Whip        = new("WPN_WHIP",        35f, 1, 3.0f, 1.05f, 0.50f,  8f, 35f, 0.10f, 0f,    0.10f,false, 0f,    1.10f, EngageRangeRatio: 0.95f, PostAttackRetreatSec: 0.60f);
    // 방패(기존 방패검 교체): 방어·CC 특화. 약공격(dmg↓) + 가드보너스 유지, CC는 스킬(방패밀치기). 방어 메커니즘 전면 재튜닝은 별도 방어형 마일스톤.
    public static readonly WeaponDef Shield      = new("WPN_SHIELD",      30f, 1, 1.5f, 0.90f, 0.50f, 16f, 55f, 0.25f, 0.60f, 0f,   false, 0f,    0.95f, 0f, 0.15f); // 패링 자격창 0.15s(반응 가드 포착) — 성공률은 BalanceConstants.ParryChance 다이얼

    public static readonly WeaponDef[] All =
        { Sword, Spear, Axe, Greatsword, DualBlades, Hammer, Whip, Shield };

    public static WeaponDef Get(string id) => Array.Find(All, w => w.Id == id);
}

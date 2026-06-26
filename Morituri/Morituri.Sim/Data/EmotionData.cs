namespace Morituri.Sim.Data;

/// <summary>
/// T10 감정 (문서[4] 9장 재정의 · 로드맵[0] Phase 2 1번 항목).
/// 경기 결과로 생성되는 <b>일시적 심리 상태</b>. 같은 결과라도 성격이 다르게 해석한다.
///
/// ★ 핵심 원칙 (라니스타 확정): 감정은 <b>의사선택(decision-layer)에만</b> 영향 —
///   데미지·받피·자원 배율에는 절대 손대지 않는다. 겁먹은 검투사는 데미지가 줄는 게 아니라
///   망설이고 거리를 둔다. 효과 = 트리거 발동 확률(TriggerProbMod) + 결정 가중치(ParamMod, Directive 합성).
///
/// 추후 누적되어 다음 경기(Phase 3 영속)·성격 변화(Phase 4: 세월/트라우마/영광)에 입력된다.
/// </summary>
public sealed record EmotionDef(
    string Id, string Name,
    float TriggerProbMod,        // 트리거 발동 확률 가산 (의사결정 — 도발/격노 등이 더/덜 터짐)
    ParamMod[] Mods,             // Directive 결정 가중치 (공격성·거리·커밋·리스크·편향). 데미지 배율 아님.
    float DecaySec = 0f);        // Phase 3 영속까지 미사용 (인매치 1경기 고정 적용)

/// <summary>감정 8종 카탈로그 (결과별: 승리 3 / 패배 3 / KO패 2). decision-only.</summary>
public static class EmotionTable
{
    private static ParamMod Add(TParam p, float v) => ParamMod.Add(p, v);

    // ── 승리 ──
    public const string Confident = "EMO_CONFIDENT";
    public const string Hubris    = "EMO_HUBRIS";
    public const string Pressure  = "EMO_PRESSURE";
    // ── 패배 ──
    public const string Inferior  = "EMO_INFERIOR";
    public const string Motivated = "EMO_MOTIVATED";
    public const string Frustrated = "EMO_FRUSTRATED";
    // ── KO 패배 ──
    public const string Trauma    = "EMO_TRAUMA";
    public const string Grudge    = "EMO_GRUDGE";

    public static readonly EmotionDef[] All =
    {
        // 승리
        new(Confident,  "자신감",   0f,     new[] { Add(TParam.Aggression, 0.10f), Add(TParam.CommitThreshold, -0.05f) }),
        new(Hubris,     "자만",     0.25f,  new[] { Add(TParam.CommitThreshold, 0.15f), Add(TParam.Aggression, -0.10f), Add(TParam.GuardBias, -0.10f) }),  // 방심 → 역전 드라마
        new(Pressure,   "부담감",   -0.05f, new[] { Add(TParam.Aggression, -0.10f), Add(TParam.CommitThreshold, 0.10f), Add(TParam.GuardBias, 0.10f) }),    // 지킬 게 생김
        // 패배
        new(Inferior,   "열등감",   -0.10f, new[] { Add(TParam.Aggression, -0.15f), Add(TParam.PreferredDistance, 0.4f), Add(TParam.GuardBias, 0.15f) }),   // 위축
        new(Motivated,  "동기부여", 0f,     new[] { Add(TParam.CommitThreshold, -0.10f), Add(TParam.Aggression, 0.10f), Add(TParam.CounterWindow, 0.05f) }),// 분발·집중
        new(Frustrated, "좌절",     0.10f,  new[] { Add(TParam.CommitThreshold, -0.15f), Add(TParam.Aggression, 0.10f), Add(TParam.GuardBias, -0.10f) }),   // 산만·자포자기
        // KO 패배
        new(Trauma,     "트라우마", -0.15f, new[] { Add(TParam.Aggression, -0.30f), Add(TParam.PreferredDistance, 1.0f), Add(TParam.GuardBias, 0.20f) }),   // 강한 공포
        new(Grudge,     "원한",     0.20f,  new[] { Add(TParam.Aggression, 0.20f), Add(TParam.RiskTolerance, 0.20f), Add(TParam.CommitThreshold, -0.10f), Add(TParam.HeavyBias, 0.20f) }), // 특정 상대 복수
    };

    private static readonly Dictionary<string, EmotionDef> _byId = All.ToDictionary(e => e.Id);
    public static EmotionDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}

/// <summary>
/// 경기 결과 → 감정 생성. <b>같은 결과라도 성격이 다르게 해석</b>한다(승리를 자신감으로 받는 선수,
/// 부담으로 받는 선수). 순수 분류 — 난수 없음(결정론). 반환 null = 중립(감정 없음, 무승부 등).
/// MatchResult 타입을 직접 참조하지 않게 결과를 분해해 받는다(Data 레이어를 leaf로 유지).
/// </summary>
public static class EmotionGen
{
    /// <param name="winner">0 / 1 / -1(무승부)</param>
    /// <param name="selfIdx">이 선수의 index (0 또는 1)</param>
    /// <param name="wasKo">경기가 KO로 끝났는가 (MatchResult.Reason == "KO")</param>
    /// <param name="selfMinHpPct">이 선수가 경기 중 떨어진 최저 HP 비율 (압승/석패 판별)</param>
    /// <param name="self">이 선수의 성격</param>
    public static string? FromResult(int winner, int selfIdx, bool wasKo, float selfMinHpPct, PersonalityDef self)
    {
        if (winner < 0) return null;                 // 무승부 = 중립
        bool won = winner == selfIdx;
        string id = self.Id;

        bool timidType   = id == PersonalityTable.Coward.Id || id == PersonalityTable.Wary.Id;
        bool resolveType = id == PersonalityTable.Bold.Id || id == PersonalityTable.Honorable.Id || id == PersonalityTable.Opportunist.Id;
        bool grudgeType  = id == PersonalityTable.Reckless.Id || id == PersonalityTable.Cruel.Id || id == PersonalityTable.Bold.Id;
        bool frustType   = id == PersonalityTable.Reckless.Id || id == PersonalityTable.Cruel.Id || id == PersonalityTable.Arrogant.Id;
        bool prideType   = id == PersonalityTable.Arrogant.Id || id == PersonalityTable.Showman.Id;

        if (won)
        {
            bool dominant = selfMinHpPct >= 0.6f;     // 거의 안 맞고 이김
            if (prideType && dominant) return EmotionTable.Hubris;
            if (timidType)             return EmotionTable.Pressure;
            return EmotionTable.Confident;
        }

        // 패배
        if (wasKo)
            return grudgeType ? EmotionTable.Grudge : EmotionTable.Trauma;

        // 판정/시간 패배
        if (timidType)   return EmotionTable.Inferior;
        if (resolveType) return EmotionTable.Motivated;
        if (frustType)   return EmotionTable.Frustrated;
        return EmotionTable.Inferior;
    }
}

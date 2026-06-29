namespace Morituri.Sim.Data;

/// <summary>관계 7종 (기획시안 10장). affinity −100(적대)~+100(유대) 축에서 교전 양상·성격으로 dominant 타입 파생.</summary>
public enum RelationType { Nemesis, Fear, Envy, Obsession, Rival, Respect, Friend }

/// <summary>
/// T11 관계 (로드맵[0] Phase 2 · [8] 네메시스). <b>특정 상대를 향한, 여러 경기에 걸쳐 누적되는 유대/적대.</b>
///
/// ★ 감정(T10)과의 차별: 감정=일시적·자기상태·연속 다이얼 / 관계=메타 영속·특정 상대 전용·<b>트리거 게이트</b>
///   (그 상대에게만 켜지는 행동) + <b>경기 외적 메타 신호</b>(복수전·라이벌리·서사). 둘 다 decision-only(데미지 무관).
/// 인매치 효과 = TriggerProbMod + decision ParamMod + 트리거 플래그(OppIsNemesis/Rival/Feared). DramaWeight = 메타 가중.
/// </summary>
public sealed record RelationDef(
    RelationType Type, string Name,
    ParamMod[] Mods,          // 인매치 decision 가중치 (그 상대 한정)
    float TriggerProbMod,     // 트리거 발동 확률 가산
    float DramaWeight,        // 메타: 매치메이킹·서사 관심도 (라이벌·원수 높음)
    TriggerRule? Rule = null);// 그 상대에게만 켜지는 게이트 행동 (원수 복수 도발 등) — 평소엔 없던 행동

public static class RelationTable
{
    private static ParamMod Add(TParam p, float v) => ParamMod.Add(p, v);
    private static readonly ParamMod[] NoMods = Array.Empty<ParamMod>();

    // 원수 게이트: 복수의 도발 집착 — 원수에게만 발동(평소엔 없던 행동). OppIsNemesis 조건.
    private static readonly TriggerRule VengeTaunt = new("REL_VENGE_TAUNT", TriggerCondition.OppIsNemesis, 0f,
        TriggerEffectKind.Interrupt, NoMods, InterruptAction.Taunt, 0.50f, 12f, 1.5f, "VENGE");
    // 공포 게이트: 천적 앞에서의 움찔 회피 — 공포 관계에만. OppIsFeared 조건.
    private static readonly TriggerRule DreadDodge = new("REL_DREAD_DODGE", TriggerCondition.OppIsFeared, 0f,
        TriggerEffectKind.Interrupt, NoMods, InterruptAction.DodgeBack, 0.50f, 3f, 0f, "DREAD");

    public static readonly RelationDef[] All =
    {
        // 원수: 복수심 폭발 — 평소 안 하던 저돌(그 상대에게만) + 복수 도발 게이트.
        new(RelationType.Nemesis, "원수",  new[] { Add(TParam.Aggression, 0.25f), Add(TParam.RiskTolerance, 0.30f), Add(TParam.CommitThreshold, -0.10f) }, 0.20f, 1.0f, VengeTaunt),
        // 공포(천적): 그 상대 앞에서만 위축·회피·거리 + 움찔 회피 게이트.
        new(RelationType.Fear,    "공포",  new[] { Add(TParam.Aggression, -0.25f), Add(TParam.PreferredDistance, 0.8f), Add(TParam.GuardBias, 0.15f) }, -0.10f, 0.4f, DreadDodge),
        // 질투: 능가 욕구 — 과공격·강공.
        new(RelationType.Envy,    "질투",  new[] { Add(TParam.Aggression, 0.15f), Add(TParam.HeavyBias, 0.10f) }, 0.15f, 0.6f),
        // 집착: 그 상대만 추격·반복공격.
        new(RelationType.Obsession, "집착", new[] { Add(TParam.RepeatBias, 0.30f), Add(TParam.Aggression, 0.10f), Add(TParam.CommitThreshold, -0.05f) }, 0.10f, 0.8f),
        // 라이벌: 방심 면역(자만 잠금 효과는 자만 감정과 상쇄되는 Commit−)+집중.
        new(RelationType.Rival,   "라이벌", new[] { Add(TParam.CommitThreshold, -0.10f), Add(TParam.Aggression, 0.10f), Add(TParam.CounterWindow, 0.05f) }, 0f, 1.0f),
        // 존경: 정정당당(가벼움 — 본격 추가타 자제는 Phase 3).
        new(RelationType.Respect, "존경",  new[] { Add(TParam.GuardBias, 0.05f) }, 0f, 0.5f),
        // 친구: 봐주기 — 소극.
        new(RelationType.Friend,  "친구",  new[] { Add(TParam.Aggression, -0.15f), Add(TParam.CommitThreshold, 0.05f) }, -0.10f, 0.3f),
    };

    private static readonly Dictionary<RelationType, RelationDef> _byType = All.ToDictionary(r => r.Type);
    public static RelationDef Get(RelationType t) => _byType[t];

    public static bool TryParse(string s, out RelationType t)
    {
        foreach (var d in All)
            if (string.Equals(d.Type.ToString(), s, StringComparison.OrdinalIgnoreCase)) { t = d.Type; return true; }
        t = default; return false;
    }
}

/// <summary>한 방향(self→opp) 관계 누적 상태. 가변(원장이 in-place 갱신).</summary>
public sealed class RelationState
{
    public float Affinity;       // −100(적대) ~ +100(유대)
    public int Encounters;
    public int Wins, Losses, KoLosses, CloseGames;

    public float CloseRatio => Encounters == 0 ? 0f : (float)CloseGames / Encounters;

    /// <summary>affinity 밴드 + 교전 양상(접전) + 성격으로 dominant 관계 타입 파생. 없으면 null(약한 사이). 수치는 초안(튜닝).</summary>
    public RelationType? Classify(string selfPersonalityId)
    {
        if (Encounters == 0) return null;
        string p = selfPersonalityId;
        bool timid    = p == PersonalityTable.Coward.Id || p == PersonalityTable.Wary.Id;
        bool aggro    = p == PersonalityTable.Reckless.Id || p == PersonalityTable.Cruel.Id || p == PersonalityTable.Bold.Id;
        bool prideful = p == PersonalityTable.Arrogant.Id || p == PersonalityTable.Showman.Id || p == PersonalityTable.Opportunist.Id;

        // 라이벌 = 막상막하 전적(승패 균형, 이 엔진은 대부분 KO 접전이라 closeRatio보다 승패 균형이 진짜 신호).
        // 지겹도록 많이(≥10) 박빙으로 싸운 호각이면 집착(드문 특수).
        bool evenRecord = MathF.Abs(Wins - Losses) <= MathF.Max(1f, Encounters * 0.25f);
        if (Encounters >= 4 && evenRecord && CloseRatio >= 0.4f)
            return Encounters >= 12 && CloseRatio >= 0.85f ? RelationType.Obsession : RelationType.Rival;  // 집착 = 드문 극단(지겹도록 박빙)
        if (Affinity <= -55f) return timid ? RelationType.Fear : RelationType.Nemesis;     // 일방적 패배 = 강한 적대
        if (Affinity <= -18f) return prideful ? RelationType.Envy : RelationType.Fear;     // 약한 적대
        if (Affinity >= 45f) return RelationType.Respect;                                  // 일방적 우위
        if (Affinity >= 18f) return RelationType.Friend;
        return null;                                                                        // 미미한 사이
    }
}

/// <summary>
/// 관계 그래프 = 로드맵의 "기억 최소판"(Phase 3 역사 DB 전신). 경기 결과를 누적해 선수 간 관계를 형성한다.
/// 결정론(난수 없음). 인메모리 — 영속 저장은 Phase 3.
/// </summary>
public sealed class RelationLedger
{
    private readonly Dictionary<(string, string), RelationState> _map = new();

    public RelationState Get(string self, string opp)
    {
        if (!_map.TryGetValue((self, opp), out var st)) { st = new RelationState(); _map[(self, opp)] = st; }
        return st;
    }

    /// <summary>경기 결과를 양방향으로 누적. winner: 0=a / 1=b / -1=무승부.</summary>
    public void RecordMatch(string aId, string bId, int winner, bool wasKo, float aMinHp, float bMinHp)
    {
        bool close = aMinHp <= 0.35f && bMinHp <= 0.35f;     // 둘 다 사선까지 = 접전(라이벌 형성)
        Apply(aId, bId, selfWon: winner == 0, isDraw: winner < 0, wasKo, close);
        Apply(bId, aId, selfWon: winner == 1, isDraw: winner < 0, wasKo, close);
    }

    private void Apply(string self, string opp, bool selfWon, bool isDraw, bool wasKo, bool close)
    {
        var st = Get(self, opp);
        st.Encounters++;
        if (isDraw) st.Affinity -= 1f;
        else if (selfWon) { st.Wins++; st.Affinity += wasKo ? 6f : 3f; }       // 이기면 약한 우위감
        else { st.Losses++; st.Affinity -= wasKo ? 20f : 9f; if (wasKo) st.KoLosses++; }  // 지면 적대↑(KO 강하게)
        if (close) st.CloseGames++;
        st.Affinity = Math.Clamp(st.Affinity, -100f, 100f);
    }

    // ── 메타 쿼리 (경기 외적 — Phase 3 매치메이킹/명성/서사의 입력) ──

    public IEnumerable<(string Self, string Opp, RelationType Type, RelationState State)> AllRelations(
        Func<string, string> personalityOf)
    {
        foreach (var ((self, opp), st) in _map)
        {
            var t = st.Classify(personalityOf(self));
            if (t is { } type) yield return (self, opp, type, st);
        }
    }

    /// <summary>라이벌리 점수(매치메이킹 관심도) = 양방향 DramaWeight × 교전강도 합.</summary>
    public float RivalryWeight(string a, string b, Func<string, string> personalityOf)
    {
        float W(string s, string o)
        {
            var st = Get(s, o);
            var t = st.Classify(personalityOf(s));
            return t is { } type ? RelationTable.Get(type).DramaWeight * (1f + st.Encounters * 0.1f) : 0f;
        }
        return W(a, b) + W(b, a);
    }

    /// <summary>복수전 후보: 강한 원수(adversity)면서 아직 못 갚음(패 > 승). (self가 opp에게 복수하고 싶다.)</summary>
    public IEnumerable<(string Self, string Opp, RelationState State)> RevengeCandidates(Func<string, string> personalityOf)
    {
        foreach (var ((self, opp), st) in _map)
        {
            var t = st.Classify(personalityOf(self));
            if ((t == RelationType.Nemesis || t == RelationType.Fear) && st.Losses > st.Wins)
                yield return (self, opp, st);
        }
    }
}

using Morituri.Sim.Combat;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;

namespace Morituri.Sim.Match;

public sealed record MatchFighterStats(
    string Name, float DamageDealt, int CleanHits, int Knockdowns, int AttackAttempts,
    int Whiffs, float CornerTime, float MinHpPct, float HpRemainPct, bool Taunted);

public sealed record MatchResult(
    int Winner,            // 0 / 1 / -1(무승부)
    string Reason,         // "KO" / "Judgement" / "Draw"
    float DurationSec,
    float ScoreA, float ScoreB,
    MatchFighterStats StatsA, MatchFighterStats StatsB);

/// <summary>
/// 1v1 실시간 자동 전투 시뮬레이터. 원칙 A(렌더링 무의존), B(결정론), C(데이터 주도).
/// 60Hz 고정 스텝, 단일 SimRandom, 모든 출력은 SimEvent + MatchResult.
/// Phase 1 단순화 (M4 이전 한시적):
///  - 아레나는 1D 라인 (전술 검증에는 거리 축이 본질, 각도는 프레젠테이션 단계에서)
///  - Strafe = 짧은 정지 행동 (각 잡기 연출은 M4)
/// </summary>
public sealed class MatchSim
{
    private const float Dt = 1f / 60f;
    private const int SnapRing = 64;

    private readonly BalanceConstants _c;
    private readonly FighterRuntime[] _f = new FighterRuntime[2];
    private readonly PerceptSnap[][] _snaps = { new PerceptSnap[SnapRing], new PerceptSnap[SnapRing] };
    private SimRandom _rng = null!;
    private List<SimEvent>? _events;
    private float _now;
    private int _tick;
    private float _crowd;   // 군중게이지 −100~+100 (+ = 선수0 편). 문서[10].

    // 관중 튜닝(초안 — 추후 T14_CrowdTuning 데이터로). 페이오프=유리한 쪽 기세 버프.
    // 감쇠는 적립이 쌓일 수 있게 약하게(4/s), 데드존 낮춰(15) 우세 스트릭이 게이지를 점유하게.
    private const float CrowdDecayPerSec = 4f, CrowdDeadzone = 15f, CrowdMaxAbs = 100f, CrowdFillScale = 2f;
    private const float CrowdDmgBuff = 0.12f, CrowdMoveBuff = 0.08f;   // 유리한 쪽 데미지·이속 최대 배율(norm=1)

    // 이동 반응 지연 — 결정(Approach/Retreat/Strafe)은 인지 지연을 거치지만, 추격 '방향'까지 상대 실시간
    // 위치를 0지연 호밍하면 거리 추적이 완벽해 둘이 붙어다니는 헤드-호밍이 된다. 추격 방향이 '마지막으로
    // 인지한 위치'를 따르게 해 인간 풋워크 랙(과스텝·간격 벌어짐 = 거리조절 댄스)을 만든다.
    // 밸런스 무관 연출 노브 — 충돌/히트 판정은 실시간 위치 유지(터널링 없음).
    private const float MoveReactDelaySec = 0.30f;

    private readonly IReadOnlyDictionary<string, float>? _weaponDmgScale; // 밸런스 스윕용 무기별 데미지 배율 주입

    public MatchSim(BalanceConstants? constants = null, IReadOnlyDictionary<string, float>? weaponDmgScale = null)
    {
        _c = constants ?? BalanceConstants.Default;
        _weaponDmgScale = weaponDmgScale;
    }

    // 뷰어 프레임 샘플링 주기 (15Hz = 4틱마다). 위치는 연속이라 60Hz 전량은 과하고, 15Hz면 보간으로 충분히 매끄럽다.
    private const int FrameSampleTicks = 4;

    public MatchResult Run(FighterDef a, FighterDef b, ulong seed,
                           List<SimEvent>? events = null, List<ReplayFrame>? frames = null)
    {
        _events = events;
        _rng = new SimRandom(seed);
        _f[0] = CreateRuntime(0, a, new Vec2(-_c.StartGap / 2f, 0f));
        _f[1] = CreateRuntime(1, b, new Vec2(_c.StartGap / 2f, 0f));
        _now = 0f; _tick = 0; _crowd = 0f;

        int strategyTicks = Math.Max(1, (int)MathF.Round(_c.StrategyTickSec / Dt));
        int decisionTicks = Math.Max(1, (int)MathF.Round(_c.DecisionTickSec / Dt));
        int maxTicks = (int)(_c.MatchTimeSec / Dt);

        for (_tick = 0; _tick < maxTicks; _tick++)
        {
            _now = _tick * Dt;
            RecordSnapshots();
            CrowdUpdate();   // 군중게이지 감쇠 + 기세/위축 강도 갱신 (이번 틱 데미지·이속·directive에 반영)

            // 처리 순서 교대(_tick & 1): A/B가 같은 난수열에서 순차로 행동을 뽑는 비대칭을
            // 매 틱 상쇄 → disc 근접 고정 난타에서도 거울전 대칭 보존. (결정론은 _tick 기반이라 유지)
            int p0 = _tick & 1, p1 = 1 - p0;
            for (int i = 0; i < 2; i++) PassiveUpdate(_f[i]);
            if (_tick % strategyTicks == 0)
                { StrategyTick(_f[p0]); StrategyTick(_f[p1]); }
            if (_tick % decisionTicks == 0)
                { TacticTick(_f[p0]); TacticTick(_f[p1]); }
            _f[0].PrevPos = _f[0].Pos; _f[1].PrevPos = _f[1].Pos;  // 이동 전 위치 — 충돌 귀속용
            FsmAdvance(_f[p0]); FsmAdvance(_f[p1]);
            ResolveCollision();   // Disc 충돌: 두 점유 공간(2×r) 통과·교환 금지
            ResolutionPhase();

            if (frames != null && _tick % FrameSampleTicks == 0) SampleFrame(frames);

            if (_f[0].Hp <= 0f || _f[1].Hp <= 0f) { SampleFrame(frames); return EndByKo(); }
        }
        SampleFrame(frames);
        return EndByJudgement();
    }

    private void SampleFrame(List<ReplayFrame>? frames)
    {
        if (frames == null) return;
        frames.Add(new ReplayFrame(_now,
            _f[0].Pos.X, _f[0].Pos.Y, _f[1].Pos.X, _f[1].Pos.Y,
            MathF.Max(0f, _f[0].HpPct), MathF.Max(0f, _f[1].HpPct),
            _f[0].StaminaPct, _f[1].StaminaPct,
            _f[0].State, _f[1].State,
            _f[0].MotionKindNow, _f[1].MotionKindNow, _crowd,
            _f[0].BleedStacks, _f[1].BleedStacks));
    }

    // ───────────────────────── 초기화 ─────────────────────────

    private FighterRuntime CreateRuntime(int idx, FighterDef def, Vec2 startPos)
    {
        var weapon = WeaponTable.Get(def.WeaponId);
        if (_weaponDmgScale != null && _weaponDmgScale.TryGetValue(def.WeaponId, out float sc))
            weapon = weapon with { BaseDamage = weapon.BaseDamage * sc };
        var rt = new FighterRuntime
        {
            Index = idx, Def = def, Weapon = weapon,
            Profile = TacticsTable.Get(def.TacticsId),
            Personality = PersonalityTable.Get(def.PersonalityId),
        };
        rt.HpMax = def.Stats.HpMax;
        rt.StaminaMax = CombatMath.StaminaMax(def.Stats, _c);
        rt.PoiseMax = weapon.PoiseMax;
        rt.GuardGaugeMax = CombatMath.GuardGaugeMax(def.Stats, weapon, _c);
        rt.MoveSpeed = CombatMath.MoveSpeedMps(def.Stats, weapon, _c);
        rt.PerceptDelaySec = CombatMath.PerceptionDelay(def.Stats);

        // 특성(T09): 파생스탯·전투 배율 반영. 고유 행동(좀비·숨고르기·초상비·선취점)은 Traits 보유 여부로 분기.
        if (def.TraitIds != null)
            foreach (var id in def.TraitIds)
            {
                if (!TraitTable.Exists(id)) continue;
                rt.Traits.Add(id);
                var t = TraitTable.Get(id);
                rt.HpMax *= t.HpMaxMult;
                rt.StaminaMax *= t.StaminaMaxMult;
                rt.PoiseMax *= t.PoiseMaxMult;
                rt.MoveSpeed *= t.MoveSpeedMult;
                rt.PerceptDelaySec += t.PerceptDelayAdd;
                rt.DamageTakenMult *= t.DamageTakenMult;
                rt.GuardDamageMult *= t.GuardDamageMult;
                rt.StamRegenTraitMult *= t.StamRegenMult;
                rt.DodgeCostMult *= t.DodgeCostMult;
                rt.RangeBonus += t.RangeAdd;
                rt.RangeMult *= t.RangeMult;
                rt.SizeScale *= t.SizeScale;
            }

        rt.Hp = rt.HpMax;
        rt.Stamina = rt.StaminaMax;
        rt.Poise = rt.PoiseMax;
        rt.GuardGauge = rt.GuardGaugeMax;
        rt.Pos = startPos;
        rt.CircleSign = idx == 0 ? 1f : -1f;   // 서로 반대로 선회 → 꼬리물기 대칭 회피
        rt.Patience = _c.PatienceMax;
        rt.RebuildDirective(0f);
        return rt;
    }

    // ───────────────────────── 인지 (문서[3] 6.3) ─────────────────────────

    private void RecordSnapshots()
    {
        for (int i = 0; i < 2; i++)
        {
            var f = _f[i];
            _snaps[i][_tick % SnapRing] = new PerceptSnap(
                f.State, f.MotionKindNow, f.Pos, f.HpPct,
                f.GuardGauge / f.GuardGaugeMax, f.StateElapsed, f.IsExhausted);
        }
    }

    /// <summary>viewer의 반응속도만큼 지연된 상대 스냅샷.</summary>
    private PerceptSnap Perceive(FighterRuntime viewer)
    {
        int delay = (int)MathF.Round(viewer.PerceptDelaySec / Dt);
        int t = Math.Max(0, _tick - delay);
        return _snaps[1 - viewer.Index][t % SnapRing];
    }

    /// <summary>이동 추격이 따라갈 상대 위치 — MoveReactDelaySec만큼 지연(인간 풋워크 랙).</summary>
    private Vec2 PerceivedMovePos(FighterRuntime viewer)
    {
        int delay = (int)MathF.Round(MoveReactDelaySec / Dt);
        int t = Math.Max(0, _tick - delay);
        return _snaps[1 - viewer.Index][t % SnapRing].Pos;
    }

    // ───────────────────────── 패시브 (자원/타이머) ─────────────────────────

    private void PassiveUpdate(FighterRuntime f)
    {
        f.StateElapsed += Dt;
        f.StateTimer -= Dt;
        f.NoHitTimer += Dt;
        if (f.NoHitTimer > 3f) f.ConsecHitsTaken = 0;

        // 출혈: 매 틱 누적 피해 (별도 트랙 — 상태 무관, 가드·다운 중에도 흐른다). 출혈사 가능(아래 KO 체크가 잡음).
        if (f.BleedStacks > 0)
        {
            if (_now >= f.BleedExpiry || DebuffImmune(f)) { f.BleedStacks = 0; f.BleedDps = 0f; f.BleedSource = -1; }
            else
            {
                float bd = f.BleedStacks * f.BleedDps * Dt;
                f.Hp -= bd;
                if (f.BleedSource >= 0) _f[f.BleedSource].DamageDealt += bd;
            }
        }

        // 패링당함 스택 감쇠 (연속 패링이 아니면 안 쌓이게 — 터틀 방지)
        if (f.ParriedStacks > 0 && _now >= f.ParriedDecayAt)
        {
            f.ParriedStacks--;
            f.ParriedDecayAt = _now + _c.ParryStunDecaySec;
        }

        f.MinHpPct = MathF.Min(f.MinHpPct, f.HpPct);

        // 인내심: 대치(Idle/이동)가 길수록 감소, 교전(공격모션/피격)하면 회복. 공격적일수록(Aggression) 빨리
        // 소진 → 전술/성격/특성 자동 반영. 0에 가까울수록 공격 충동(SelectAction)이 커져 영원 대치를 깬다.
        float pDrain = _c.PatienceDrainBase * (0.5f + f.Dir.Aggression);
        bool engaging = f.State is FighterState.Windup or FighterState.Active or FighterState.Recovery
                     or FighterState.HitStun or FighterState.Stagger or FighterState.Down;
        f.Patience = engaging
            ? MathF.Min(_c.PatienceMax, f.Patience + pDrain * 3f * Dt)   // 교전 중 빠르게 회복
            : MathF.Max(0f, f.Patience - pDrain * Dt);                    // 대치 중 감소

        // 스태미나 (문서[4] 6장)
        float regen = f.State switch
        {
            FighterState.Idle or FighterState.Taunt or FighterState.HitStun
                or FighterState.Stagger or FighterState.Down or FighterState.GetUp => _c.StamRegenIdle,
            FighterState.Move => _c.StamRegenMoving,
            FighterState.Guard => -_c.StamCostGuardPerSec,
            _ => 0f, // 공격 모션/회피 중 회복 없음
        };
        // 카이팅 비용(B): 존형이 거리 유지(후퇴/선회)로 빠지면 회복 대신 소모 — 영원히 카이팅 못 한다.
        // 위치 이중안정을 스태미나 소모로 완충해 브롤러:카이터를 부드럽게 착지시키는 노브.
        if (f.Weapon.Range >= _c.MinLongRange && f.State == FighterState.Move
            && f.CurrentAction is ActionRequest.Retreat or ActionRequest.Strafe)
            regen = -_c.KiteStamCostPerSec;
        if (regen > 0f) regen *= f.Dir.StamRegenMult * f.StamRegenTraitMult;
        f.Stamina = Math.Clamp(f.Stamina + regen * Dt, 0f, f.StaminaMax);

        if (f.IsExhausted) f.ExhaustTimer -= Dt;
        else if (f.Stamina <= 0f)
        {
            f.ExhaustTimer = _c.ExhaustDurationSec;
            Emit(new StaminaExhausted(_now, f.Index));
            if (f.State == FighterState.Guard) ChangeState(f, FighterState.Idle); // 가드 불가
        }

        // 가드 게이지 자연 회복 (비가드 상태)
        if (f.State != FighterState.Guard)
        {
            f.GuardGauge = MathF.Min(f.GuardGaugeMax, f.GuardGauge + f.GuardGaugeMax * _c.GuardGaugeRecoverPctPerSec * Dt);
            if (f.GuardDisabled && f.GuardGauge >= f.GuardGaugeMax * _c.GuardBreakRecoverToPct)
                f.GuardDisabled = false;
        }

        // Poise 회복 (피격 후 1초 정지)
        f.PoiseRegenBlockTimer -= Dt;
        if (f.PoiseRegenBlockTimer <= 0f)
            f.Poise = MathF.Min(f.PoiseMax, f.Poise + f.PoiseMax * _c.PoiseRecoverPctPerSec * Dt);

        // 가장자리 체류 (판정 패널티) — 중심에서 멀수록 몰린 것
        if (f.Pos.Length >= _c.ArenaRadius - _c.CornerZone)
            f.CornerTime += Dt;
    }

    // ───────────────────────── 전략층 (Override 트리거) ─────────────────────────

    private TriggerContext BuildTriggerContext(FighterRuntime f, in PerceptSnap opp)
    {
        return new TriggerContext(
            SelfHpPct: f.HpPct,
            OppHpPct: opp.HpPct,
            SelfWinning: f.HpPct > opp.HpPct + 0.05f,
            ConsecHitsTaken: f.ConsecHitsTaken,
            OppHeavyWindupPerceived: opp.State == FighterState.Windup && opp.MotionKind == MotionKind.Heavy,
            OppDownPerceived: opp.State == FighterState.Down,
            SecSinceCritTaken: _now - f.LastCritTakenAt,
            TimeRemainPct: 1f - _now / _c.MatchTimeSec,
            OppGuardGaugePct: opp.GuardGaugePct,
            StaminaPct: f.StaminaPct,
            ReservePct: f.Dir.StaminaReserve,
            SameWhiffCount: f.SameWhiffCount,
            HpDeficitPct: opp.HpPct - f.HpPct,
            OppExhaustedPerceived: opp.IsExhausted,
            OppStaggeredPerceived: opp.State == FighterState.Stagger,
            SecSinceTaunted: _now - f.LastTauntedAt);
    }

    private void StrategyTick(FighterRuntime f)
    {
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        var ctx = BuildTriggerContext(f, opp);

        // 성격 Override 규칙 + 전술 고유 조건 (같은 엔진 — 문서[5] 5장)
        EvalOverrideRules(f, f.Personality.Rules, ctx, f.Personality.GlobalProbMod);
        if (f.Profile.UniqueRule is { } unique)
            EvalOverrideRules(f, new[] { unique }, ctx, f.Personality.GlobalProbMod);

        f.RebuildDirective(_now);
    }

    private void EvalOverrideRules(FighterRuntime f, TriggerRule[] rules, in TriggerContext ctx, float probMod)
    {
        foreach (var r in rules)
        {
            if (r.Kind != TriggerEffectKind.Override) continue;
            if (f.CooldownUntil.TryGetValue(r.Id, out float until) && _now < until) continue;
            if (!TriggerEval.Matches(r, ctx)) continue;
            if (!_rng.Roll(r.Probability * (1f + probMod))) continue; // 냉철함: ×0.5 전처리

            f.Overrides.Add(new ActiveOverride { Mods = r.Mods, ExpiresAt = _now + r.DurationSec, ReasonTag = r.ReasonTag });
            f.CooldownUntil[r.Id] = _now + r.CooldownSec;
            Emit(new Decision(_now, f.Index, r.ReasonTag, "Strategy", r.DurationSec));
        }
    }

    // ───────────────────────── 전술층 (Interrupt + Utility) ─────────────────────────

    private void TacticTick(FighterRuntime f)
    {
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        var ctx = BuildTriggerContext(f, opp);

        // 1) 성격 Interrupt (즉발형 — 빠른 반응이 본질이라 전술층 주기로 평가)
        if (TryInterrupts(f, ctx)) return;

        // 2) ForcedHeavy 인터럽트 잔여분
        if (f.PendingForced != ActionRequest.None)
        {
            if (TryStartAction(f, f.PendingForced, opp)) { f.PendingForced = ActionRequest.None; return; }
        }

        // 2.5) [안B] 공격 후 이탈 창: 카이터는 일반 행동 대신 후퇴 강제(찌르고 빠짐). 인터럽트(회피·가드)는 위에서 이미 처리.
        if (_now < f.RepositionUntil)
        {
            TryStartAction(f, ActionRequest.Retreat, opp);   // 후딜 중이면 캔슬불가로 무효 → 후딜 끝나면 후퇴 발동
            return;
        }

        // 3) Utility 행동 선택
        var action = SelectAction(f, opp);
        if (action != f.CurrentAction || f.State == FighterState.Idle)
            TryStartAction(f, action, opp);
    }

    private bool TryInterrupts(FighterRuntime f, in TriggerContext ctx)
    {
        foreach (var r in f.Personality.Rules)
        {
            if (r.Kind != TriggerEffectKind.Interrupt) continue;
            if (f.CooldownUntil.TryGetValue(r.Id, out float until) && _now < until) continue;
            if (!TriggerEval.Matches(r, ctx)) continue;
            if (!_rng.Roll(r.Probability * (1f + f.Personality.GlobalProbMod))) continue;

            bool fired = r.Interrupt switch
            {
                InterruptAction.Taunt => DoTaunt(f),
                InterruptAction.DodgeBack => DoDodge(f, away: true, allowWindupCancel: true),
                InterruptAction.ForcedHeavy => DoForcedHeavy(f),
                InterruptAction.HoldOff => DoHoldOff(f, r.DurationSec),
                _ => false,
            };
            if (fired)
            {
                f.CooldownUntil[r.Id] = _now + r.CooldownSec;
                Emit(new Decision(_now, f.Index, r.ReasonTag, "Execution", MathF.Max(r.DurationSec, 1f)));
                return true;
            }
        }
        return false;
    }

    private bool DoTaunt(FighterRuntime f)
    {
        if (!IsCancellable(f, allowWindupCancel: false)) return false;
        f.EverTaunted = true;
        ChangeState(f, FighterState.Taunt, _c.TauntDurationSec);
        CrowdFill(f, 7f);   // 도발 — 관중 호응(쇼맨·오만 테마)
        // A: 도발당한 상대에게 분노를 건다. 지속(>도발 경직)이 길어 도발 후 카운터 창이 생긴다.
        //    성격은 WasTaunted 트리거로 이 분노를 증폭(충동)·상쇄(냉철)·반전(겁쟁이 위축)한다 (C).
        var opp = _f[1 - f.Index];
        opp.LastTauntedAt = _now;
        opp.Overrides.Add(new ActiveOverride
        {
            Mods = new[]
            {
                ParamMod.Add(TParam.Aggression, _c.TauntRageAggression),
                ParamMod.Add(TParam.CommitThreshold, _c.TauntRageCommitAdd),
            },
            ExpiresAt = _now + _c.TauntRageDurationSec,
            ReasonTag = "RAGED",
        });
        Emit(new Decision(_now, opp.Index, "RAGED", "Strategy", _c.TauntRageDurationSec));
        return true;
    }

    private bool DoForcedHeavy(FighterRuntime f)
    {
        f.PendingForced = ActionRequest.AttackHeavy;
        var opp = Perceive(f);
        if (TryStartAction(f, ActionRequest.AttackHeavy, opp)) { f.PendingForced = ActionRequest.None; return true; }
        return true; // 지금 못 하면 예약 유지
    }

    private bool DoHoldOff(FighterRuntime f, float duration)
    {
        f.Overrides.Add(new ActiveOverride
        {
            Mods = new[] { ParamMod.Set(TParam.NoAttack, 1f), ParamMod.Add(TParam.PreferredDistance, 1.0f) },
            ExpiresAt = _now + duration, ReasonTag = "HONOR",
        });
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        return TryStartAction(f, ActionRequest.Retreat, opp);
    }

    // --- Utility 점수 산식 (문서[3] 6.2) ---

    private ActionRequest SelectAction(FighterRuntime f, in PerceptSnap opp)
    {
        ref readonly Directive d = ref f.Dir;
        float dist = Vec2.Dist(opp.Pos, f.Pos);
        // 교전 거리 보정: 선호 거리가 자기 유효 공격 거리보다 멀면 영원한 대치가 된다 (거울 매치 KO 0%로 검출).
        // "때릴 의지가 있으면 닿는 곳까지 간다" — 단 NoAttack(명예중시 HoldOff) 중엔 원래 선호 거리 유지.
        float engage = d.NoAttack > 0.5f ? d.PreferredDistance
                     : MathF.Min(d.PreferredDistance, f.EffRange * f.Weapon.EngageRangeRatio); // [안E] 카이터는 사거리 끝 유지
        float gap = dist - engage;
        // 지친 상대는 반격할 수 없다 = 캔슬 불가 상태와 동급의 확정 처벌 창.
        bool oppLocked = opp.IsExhausted
                      || opp.State is FighterState.Recovery or FighterState.Stagger
                      or FighterState.Down or FighterState.Taunt or FighterState.HitStun;
        // 평시엔 인지 지연 보상 마진(사거리 끝 발사는 빈 곳을 때림),
        // 확정 기회(캔슬 불가 상태의 상대)엔 풀 사거리 — 후딜 처벌이 마진에 막히면 카운터형이 죽는다.
        bool inRange = dist <= f.EffRange * (oppLocked ? 1.0f : 0.88f);
        bool oppWindup = opp.State == FighterState.Windup;
        bool oppRecovery = opp.State == FighterState.Recovery;
        bool oppGuard = opp.State == FighterState.Guard;
        bool oppDown = opp.State is FighterState.Down or FighterState.GetUp;

        // 경계 근접도 (0 중심 ~ 1 가장자리). B: 원형 핏 — 경계선 후퇴는 막히므로 선회가 답.
        float edgeProx = MathF.Min(1f, f.Pos.Length / MathF.Max(0.5f, _c.ArenaRadius - 0.5f));

        Span<float> score = stackalloc float[9];
        score[(int)ActionRequest.Approach] = gap > d.DistanceTolerance ? 0.45f + MathF.Min(1f, gap / 2f) * 0.8f : 0.05f;
        // 후퇴: 경계에선 벽에 막혀 무의미 → 가치 감쇠 (옛 1D 벽-핀 문제의 제거)
        score[(int)ActionRequest.Retreat] = (gap < -d.DistanceTolerance ? 0.45f + MathF.Min(1f, -gap / 2f) * 0.8f : 0.05f)
                                          * (1f - 0.6f * edgeProx);
        // 선회(B 핵심): 사거리 우위 무기는 자기 스윗스팟을 '유지'하려 선회(견제 카이팅), 그 외엔 근접 시 거리벌리기.
        bool wantSpace = gap < -d.DistanceTolerance
                      || (f.Weapon.Range >= _c.MinLongRange && gap < d.DistanceTolerance);
        score[(int)ActionRequest.Strafe] = 0.10f + (wantSpace ? 0.30f + 0.7f * edgeProx : 0f);

        // 자기 약점 거리(inner ×0.6 구간)에서의 공격은 반토막 가치 — 창/채찍은 먼저 거리를 벌리는 게 정답
        bool selfInner = f.Weapon.Range >= _c.MinLongRange && dist < f.Weapon.Range * _c.InnerRangeRatio;
        float innerMul = selfInner ? 0.45f : 1f;
        float light = inRange ? (0.25f + d.Aggression) * innerMul : 0f; // 사거리 안 기본 공격성 (전 전술 공통 바닥값)
        float heavy = inRange ? d.Aggression * (0.4f + 0.6f * d.RiskTolerance) * (1f + d.HeavyBias + f.Weapon.HeavyBias) * innerMul : 0f;
        float feint = (dist <= f.EffRange * 1.3f) ? d.FeintRate * 0.8f : 0f;

        bool oppHeavyWindup = oppWindup && opp.MotionKind == MotionKind.Heavy;

        // 리치 우위: 상대는 내 사거리 안, 나는 상대 사거리 밖 = 일방 유효타 구간.
        // 창/채찍 "견제"의 기계적 본질 (문서[4] 8장 거리 대역 / 기획서 "창=견제 특화").
        var oppRt = _f[1 - f.Index];
        float oppRange = oppRt.EffRange;
        bool reachAdvantage = inRange && dist > oppRange + 0.1f;
        if (reachAdvantage) { light *= 2.2f; feint *= 1.3f; }
        // 장거리 무기가 적 사거리 안쪽으로 끌려든 상태(cramped) = 자기 우위 거리(reachAdvantage)를 못 잡은 위험 구간.
        // 여기선 처벌 욕심(아래 후딜/선딜 부스트)이 거리 회복보다 우선하면 도끼 카운터에 갈린다 — '창은 먼저 거리를 벌린다'.
        bool reachWeaponCramped = f.Weapon.Range >= _c.MinLongRange && dist <= oppRange + 0.1f;

        // 수싸움 프레임 판정 (상성 매트릭스 디버깅으로 도입). 상대 선딜을 인지했을 때 세 갈래:
        //  · 레이스 승산: (내 인지지연 + 내 약공 선딜) < 상대 잔여 선딜 → 반격이 먼저 닿는다 = 카운터
        //  · 헛스윙 처벌 가능: 내 응답시간 < 상대 후딜 → 회피로 빼서 후딜을 때린다 = 회피
        //  · 둘 다 아님(검 약공 등 빠르고 안전한 공격) → 가드가 정답. 이길 수 없는 레이스에
        //    뛰어들면 항상 먼저 맞고 스윙이 끊기고, 처벌 못 할 공격을 피하면 스태미나만 샌다.
        bool raceWinnable = false, whiffPunishable = false;
        if (oppWindup)
        {
            var oppMotion = MotionTable.Get(oppRt.Weapon.Id, opp.MotionKind);
            float oppWindupTotal = CombatMath.MotionTime(oppMotion.WindupBaseSec, oppRt.Weapon, oppRt.Def.Stats, _c);
            float myStrike = f.PerceptDelaySec
                + CombatMath.MotionTime(MotionTable.Get(f.Weapon.Id, MotionKind.Light).WindupBaseSec, f.Weapon, f.Def.Stats, _c);
            raceWinnable = myStrike < oppWindupTotal * 0.9f;
            whiffPunishable = myStrike < oppRt.Weapon.RecoverySec * oppMotion.RecoveryMult;
        }

        // OpportunityMod — 수싸움의 핵심
        if (oppWindup && raceWinnable) light *= 1f + 1.875f * d.CounterWindow; // CounterWindow 0.8 → ×2.5
        if (oppRecovery)
        {
            // 후딜 처벌은 카운터형의 본업 — CounterWindow가 여기에도 기여 (M2 튜닝: 1.8 고정이면 게이트 미달)
            float recBoost = 1.8f * (1f + 0.5f * d.CounterWindow);
            light *= recBoost; heavy *= recBoost;
        }
        // 아머 트레이드: 하이퍼아머 무기는 상대가 커밋(선딜/액티브)했을 때 강공으로 받아친다.
        // 상대 약공을 몸으로 받고(경직 무효) 내 일격을 꽂는다 — 중량 무기의 본래 게임플랜.
        if (f.Weapon.HyperArmor && (oppWindup || opp.State == FighterState.Active)) heavy *= 2.6f;
        if (oppGuard) { heavy *= 1.4f; feint *= 1.5f; }               // 가드 깎기 / 흔들기
        if (oppDown) { light *= 1.3f; heavy *= 1.3f; }                // 기본 AI도 추가타 선호 (성격이 가감)
        if (opp.IsExhausted) { light *= 1.8f; heavy *= 2.2f; }        // 지친 적 = 인내형이 기다린 확정 처벌 창 (강공으로 Stagger→다운 노림)
        if (opp.State == FighterState.Taunt) { light *= 2.0f; heavy *= 2.0f; } // 도발 = 무방비 — 역전패 제조기가 작동하려면 처벌자가 있어야 한다

        // RepeatBias (집착적): 직전 공격과 같은 종류 가중
        if (f.LastAttack == ActionRequest.AttackLight) light *= 1f + d.RepeatBias;
        if (f.LastAttack == ActionRequest.AttackHeavy) heavy *= 1f + d.RepeatBias;

        // StaminaFit: Reserve 이하로는 쓰지 않는다 — 단, 확정 기회(캔슬 불가 상대)엔 규율 면제.
        // Reserve는 평시 수싸움의 절제이지 황금 기회를 흘려보내는 규칙이 아니다. 이 면제가 없으면
        // 수비형은 가드로 비축분까지 말라 "기다리던 처벌 창"이 와도 못 때린다 (상성 매트릭스 디버깅).
        float reserveAbs = oppLocked ? 0f : d.StaminaReserve * f.StaminaMax;
        if (f.Stamina - _c.StamCostAttackLight < reserveAbs || f.IsExhausted) light = 0f;
        if (f.Stamina - _c.StamCostAttackHeavy < reserveAbs || f.IsExhausted) heavy = 0f;
        if (f.Stamina - _c.StamCostAttackLight < reserveAbs || f.IsExhausted) feint = 0f;
        if (d.NoAttack > 0.5f) { light = 0f; heavy = 0f; feint = 0f; }

        // cramped(적 품속)에 끌려든 장거리 무기는 처벌 부스트를 깎아 거리 회복(후퇴/선회)이 이기게 한다.
        // 이러면 창은 1.1m에서 찌르지 않고 자기 스윗스팟(1.5m+)으로 물러난 뒤, 거기서 reachAdvantage 처벌.
        // 단 인내심이 바닥나면(조바심) 페널티를 완화 — 공격 결단이 거리 회복을 이긴다.
        // 충동은 카이터(사거리 우위)에만 — 근접 무기는 원래 붙어 싸워 영원 대치가 없다(검 거울 baseline 보존).
        float impulse = f.Weapon.Range >= _c.MinLongRange ? 1f - f.Patience / _c.PatienceMax : 0f;  // 0(인내)~1(소진)
        if (reachWeaponCramped) { float p = 0.35f + 0.65f * impulse; light *= p; heavy *= p; }
        // 인내심 충동: 바닥날수록 공격↑ + 카이팅(선회/후퇴)↓ → 대치를 끝내고 달려든다(거울전 영원 대치 해소).
        if (impulse > 0f)
        {
            float mImp = 1f + _c.PatienceImpulseScale * impulse;
            light *= mImp; heavy *= mImp;
            float damp = 1f - 0.8f * impulse;
            score[(int)ActionRequest.Strafe] *= damp;
            score[(int)ActionRequest.Retreat] *= damp;
        }
        score[(int)ActionRequest.AttackLight] = light;
        score[(int)ActionRequest.AttackHeavy] = heavy;
        score[(int)ActionRequest.Feint] = feint;

        bool oppAttacking = oppWindup || opp.State == FighterState.Active;
        // 상대 무기의 가드 깎기 성능(정적 정보)을 반영: 도끼/망치 상대 가드는 게이지 자살
        float crushFear = 1f - _f[1 - f.Index].Weapon.GuardCrush;
        float guard = (f.IsExhausted || f.GuardDisabled) ? 0f : d.GuardBias * (oppAttacking ? 2.0f : 0.5f) * crushFear;
        // 회피는 '선딜 인지' 시에만 가치 있음 — Active를 보고 구르면 i-frame이 늦고 스태미나만 샌다 (M2 디버깅 교훈).
        // 단, 상대 공격이 실제로 닿을 수 있을 때만 — 사거리 밖(창>도끼)에서 헛스윙할 공격을 반사적으로 회피하면
        // 스태미나만 탕진해, 정작 처벌 창에서 정작 자신이 지쳐 못 찌른다 (M3 이슈 #2 핵심 원인).
        bool oppCanReachMe = dist <= oppRange + 0.5f;
        float dodge = (f.Stamina < _c.StamCostDodge || f.IsExhausted)
            ? 0f
            : (oppWindup && oppCanReachMe ? (0.35f + 0.6f * (1f - d.RiskTolerance)) * 1.3f : 0.08f);
        // 회피의 가치는 "회피가 만든 헛스윙을 처벌할 수 있는가"에 달렸다. 후딜 짧은 공격(검 약공)을
        // 피하는 건 스태미나 낭비(15/회)지만, 후딜 긴 공격(도끼)을 빼는 건 처벌 창 제조다.
        if (oppWindup && !whiffPunishable) dodge *= 0.6f;
        // 강공은 가드 크러시 위협 — 가드 대신 회피로 흘리는 게 보편적 격투 상식
        if (oppHeavyWindup && oppCanReachMe) { guard *= 0.7f; dodge *= 1.6f; }
        score[(int)ActionRequest.Guard] = guard;
        score[(int)ActionRequest.Dodge] = dodge;

        // Noise — 이변의 원천 1
        for (int i = 1; i < 9; i++)
            score[i] *= 1f + _rng.Range(-_c.UtilityNoise, _c.UtilityNoise);

        // 최고점 + Commit 게이트 (공격은 확신도 요구치 이상일 때만)
        int best = 1;
        for (int i = 2; i < 9; i++) if (score[i] > score[best]) best = i;

        var bestAction = (ActionRequest)best;
        bool isAttack = bestAction is ActionRequest.AttackLight or ActionRequest.AttackHeavy or ActionRequest.Feint;
        if (isAttack && score[best] < f.Dir.CommitThreshold * _c.AttackGateScale)
        {
            // 확신도 미달 → 차순위 비공격 행동 (신중함이 공격을 아끼는 메커니즘)
            int alt = (int)ActionRequest.Approach;
            for (int i = 1; i < 9; i++)
            {
                var a = (ActionRequest)i;
                if (a is ActionRequest.AttackLight or ActionRequest.AttackHeavy or ActionRequest.Feint) continue;
                if (score[i] > score[alt]) alt = i;
            }
            best = alt;
        }
        return (ActionRequest)best;
    }

    // ───────────────────────── 실행층 FSM ─────────────────────────

    private bool IsCancellable(FighterRuntime f, bool allowWindupCancel)
    {
        if (f.State is FighterState.Idle or FighterState.Move or FighterState.Guard) return true;
        if (allowWindupCancel && f.State == FighterState.Windup && !f.IsFeintSwing
            && f.StateElapsed <= f.WindupTotalSec * _c.CancelWindowRatio) return true; // 선딜 70%까지 캔슬
        return false;
    }

    private bool TryStartAction(FighterRuntime f, ActionRequest action, in PerceptSnap opp)
    {
        // Utility 경로는 선딜 캔슬 불가 — 스윙 커밋은 커밋이다.
        // 선딜 캔슬은 성격 Interrupt(겁쟁이 DodgeBack) 전용 (문서[3] 7.1 "겁쟁이 Interrupt가 여길 노림").
        if (!IsCancellable(f, allowWindupCancel: false)) return false;

        switch (action)
        {
            case ActionRequest.Approach:
            case ActionRequest.Retreat:
                f.CurrentAction = action;
                if (f.State != FighterState.Move) ChangeState(f, FighterState.Move, float.MaxValue);
                return true;

            case ActionRequest.Strafe:
                f.CurrentAction = action;
                ChangeState(f, FighterState.Move, _c.DecisionTickSec); // Phase 1: 짧은 정지(각 잡기 연출은 M4)
                return true;

            case ActionRequest.Guard:
                if (f.IsExhausted || f.GuardDisabled) return false;
                f.CurrentAction = action;
                if (f.State != FighterState.Guard) ChangeState(f, FighterState.Guard, float.MaxValue);
                return true;

            case ActionRequest.Dodge:
                return DoDodge(f, away: true, allowWindupCancel: true);

            case ActionRequest.AttackLight:
            case ActionRequest.AttackHeavy:
            case ActionRequest.Feint:
                return StartSwing(f, action);

            default:
                return false;
        }
    }

    private bool DoDodge(FighterRuntime f, bool away, bool allowWindupCancel)
    {
        if (!IsCancellable(f, allowWindupCancel)) return false;
        float cost = _c.StamCostDodge * f.DodgeCostMult;   // 초상비: 대시 ST소모 감소
        if (f.Stamina < cost || f.IsExhausted) return false;
        f.Stamina -= cost;
        if (f.Has(TraitTable.Lightfoot)) f.DashSpeedBuffUntil = _now + 1f;  // 초상비: 대시 후 1초 이속↑

        var opp = _f[1 - f.Index];
        Vec2 back = (f.Pos - opp.Pos).Normalized();        // 상대 반대쪽
        if (back.Length < 1e-6f) back = new Vec2(f.Index == 0 ? -1f : 1f, 0f);
        if (!away) back = back * -1f;

        Vec2 target = f.Pos + back * _c.DodgeDistance;
        // 후방이 경계 밖이면 접선 성분을 섞어 측면으로 빠진다 (옛 1D '코너 통과 롤'의 2D 자연 등가 — 핵 불필요)
        if (target.Length > _c.ArenaRadius - 0.5f)
            target = f.Pos + (back + back.Perp() * f.CircleSign).Normalized() * _c.DodgeDistance;
        f.Pos = ClampToArena(target);
        f.CurrentAction = ActionRequest.Dodge;
        ChangeState(f, FighterState.Dodge, _c.DodgeDurationSec);
        return true;
    }

    private bool StartSwing(FighterRuntime f, ActionRequest action)
    {
        bool isFeint = action == ActionRequest.Feint;
        var kind = action == ActionRequest.AttackHeavy ? MotionKind.Heavy : MotionKind.Light;
        float cost = kind == MotionKind.Heavy ? _c.StamCostAttackHeavy : _c.StamCostAttackLight;
        if (f.Stamina < cost || f.IsExhausted) return false;

        f.Stamina -= cost * (isFeint ? 0.5f : 1f);
        f.Motion = MotionTable.Get(f.Weapon.Id, kind);
        f.MotionKindNow = kind;
        f.IsFeintSwing = isFeint;
        f.SwingResolved = false;
        f.LastSwingGuarded = false;
        f.WindupTotalSec = CombatMath.MotionTime(f.Motion.WindupBaseSec, f.Weapon, f.Def.Stats, _c);
        f.CurrentAction = action;
        if (!isFeint) { f.AttackAttempts++; f.LastAttack = action; }
        ChangeState(f, FighterState.Windup, f.WindupTotalSec);
        Emit(new AttackSwung(_now, f.Index, f.Motion.Id, isFeint));
        return true;
    }

    /// <summary>원형 핏 경계 안으로 투영. 선회는 경계에 닿아도 접선으로 미끄러진다(벽-핀 제거 = B의 핵심).</summary>
    private Vec2 ClampToArena(Vec2 p)
    {
        float maxR = _c.ArenaRadius - 0.5f, len = p.Length;
        return len <= maxR ? p : p * (maxR / len);
    }

    /// <summary>
    /// Disc 충돌 해소: 두 캐릭터의 점유 공간(반경 CollisionRadius)이 겹치면 밀어내 통과·위치교환을
    /// 막는다. 정면으로 박으면 0.8m에서 정지, 비스듬히 오면 경계를 따라 미끄러진다(= 플랭크의 물리적 기반).
    /// 보정은 대칭이 아니라 "이번 틱 누가 상대 쪽으로 파고들었나(접근 기여)"에 비례 — 가만히 선 쪽은
    /// 안 밀리고 파고든 쪽만 정지한다(넉백 스킬 외에는 제자리 사수). 둘 다 접근 안 했는데 겹친 비상시
    /// (경계 클램프 등)만 대칭 폴백. 매 틱 보정량이 작아(이동 0.03m/틱 ≪ 반경 0.4m) 부드럽고 터널링 없음.
    /// </summary>
    private void ResolveCollision()
    {
        Vec2 delta = _f[1].Pos - _f[0].Pos;
        float dist = delta.Length;
        float minD = 2f * _c.CollisionRadius;
        if (dist >= minD) return;
        // 완전히 겹친(dist≈0) 비상시: 시작 배치 축(±x)으로 가른다.
        Vec2 dir = dist > 1e-4f ? delta.Normalized() : new Vec2(1f, 0f);
        float pen = minD - dist;   // 겹친 양

        // 각자 이번 틱 이동 중 상대 쪽으로 좁힌 성분만 = 접근 기여. 가만히 선 쪽은 0 → 안 밀린다.
        Vec2 m0 = _f[0].Pos - _f[0].PrevPos, m1 = _f[1].Pos - _f[1].PrevPos;
        float c0 = MathF.Max(0f, m0.X * dir.X + m0.Y * dir.Y);    // f0가 +dir(f1 쪽)으로 전진
        float c1 = MathF.Max(0f, -(m1.X * dir.X + m1.Y * dir.Y)); // f1이 -dir(f0 쪽)으로 전진
        float total = c0 + c1;

        float push0, push1;
        if (total > 1e-5f) { push0 = pen * (c0 / total); push1 = pen * (c1 / total); }
        else               { push0 = push1 = pen * 0.5f; }   // 접근 기여 없음 → 대칭 폴백
        _f[0].Pos = ClampToArena(_f[0].Pos - dir * push0);
        _f[1].Pos = ClampToArena(_f[1].Pos + dir * push1);
    }

    private void FsmAdvance(FighterRuntime f)
    {
        switch (f.State)
        {
            case FighterState.Move:
            {
                float speed = f.MoveSpeed * (f.IsExhausted ? _c.ExhaustMoveSpeedMult : 1f) * (1f + CrowdMoveBuff * f.CrowdMomentum)
                            * (_now < f.DashSpeedBuffUntil ? 1.25f : 1f);   // 초상비: 대시 직후 이속↑
                // 추격 방향은 '마지막으로 인지한' 위치를 따른다(인간 풋워크 랙) — 실시간 호밍 금지.
                Vec2 toOpp = (PerceivedMovePos(f) - f.Pos).Normalized();
                Vec2 move = f.CurrentAction switch
                {
                    // 선회(B): 교전선에 수직 접선 이동 → 등속 추격자로부터 간격 유지(카이팅). 옛 1D 불가 동작.
                    ActionRequest.Strafe => toOpp.Perp() * f.CircleSign,
                    ActionRequest.Retreat => toOpp * -1f,
                    _ => toOpp, // Approach
                };
                // 카이터 레버: 후퇴/선회 시 경계로 밀리면 dodge-back 리셋 공간이 사라진다. 열린 중앙으로
                // 당겨 벽-핀을 피하고 거리 유지 발판을 확보. 존형(장사거리) 전용 — 근접형 거울 질감 보존.
                float rN = f.Pos.Length / _c.ArenaRadius;
                if (f.Weapon.Range >= _c.MinLongRange && rN > 0.5f && f.CurrentAction != ActionRequest.Approach)
                    move = (move + (f.Pos * -1f).Normalized() * ((rN - 0.5f) * 2.6f)).Normalized();
                f.Pos = ClampToArena(f.Pos + move * (speed * Dt));
                if (f.StateTimer <= 0f && f.CurrentAction == ActionRequest.Strafe)
                    ChangeState(f, FighterState.Idle);
                break;
            }
            case FighterState.Windup:
                if (f.IsFeintSwing && f.StateElapsed >= f.WindupTotalSec * _c.FeintCancelRatio)
                    ChangeState(f, FighterState.Recovery, _c.FeintRecoverySec);
                else if (f.StateTimer <= 0f)
                    ChangeState(f, FighterState.Active, f.Motion.ActiveSec);
                break;

            case FighterState.Active:
                // 히트 판정은 ResolutionPhase에서 동시 해결 (선공 고정 버그 방지 — M2 거울 검증으로 발견)
                break;

            case FighterState.Recovery:
            case FighterState.Dodge:
            case FighterState.HitStun:
            case FighterState.Stagger:
            case FighterState.GetUp:
            case FighterState.Taunt:
                if (f.StateTimer <= 0f) ChangeState(f, FighterState.Idle);
                break;

            case FighterState.Down:
                if (f.StateTimer <= 0f)
                {
                    f.DownHitConsumed = false;
                    ChangeState(f, FighterState.GetUp, _c.GetUpDurationSec);
                }
                break;
        }
    }

    private void RegisterWhiff(FighterRuntime f)
    {
        f.Whiffs++;
        // 허공을 가르는 과도한 커밋엔 스태미나 대가가 따른다 — 난전형의 헛스윙 난사가 제풀에 가스아웃되게 한다.
        // (다음 PassiveUpdate가 0 도달 시 Exhausted 진입을 처리.) 절제된 카운터형(헛스윙 0)은 영향받지 않는다.
        f.Stamina = MathF.Max(0f, f.Stamina - _c.StamCostWhiff);
        if (f.LastWhiffed == f.CurrentAction) f.SameWhiffCount++;
        else { f.LastWhiffed = f.CurrentAction; f.SameWhiffCount = 1; }
    }

    // ───────────────────────── 히트 판정 (문서[4] 3장 처리 순서) ─────────────────────────

    private readonly record struct DefenseSnap(
        FighterState State, float StateElapsed, bool DownHitConsumed, Vec2 Pos, bool IsExhausted, bool Armored, bool Striking);

    /// <summary>
    /// 동시 해결: 양측의 방어 상태를 먼저 캡처한 뒤 상호 적용한다.
    /// 순차 적용 시 선수 0의 타격이 항상 먼저 들어가 동시 교환(트레이드)을 독점하는
    /// 공정성 버그가 생긴다 (거울 매치 100:0으로 검출됨).
    /// </summary>
    private void ResolutionPhase()
    {
        Span<DefenseSnap> snap = stackalloc DefenseSnap[2];
        for (int i = 0; i < 2; i++)
        {
            var f = _f[i];
            // 하이퍼아머: 중량 무기가 강공 선딜을 커밋한 순간 = 약공에 안 끊기는 상태 (페인트 제외).
            bool armored = f.Weapon.HyperArmor && f.State == FighterState.Windup
                        && f.MotionKindNow == MotionKind.Heavy && !f.IsFeintSwing;
            // Striking: 이번 틱에 이 선수가 타격 적용 가능한 상태(Active·미해결)인지를 미리 고정.
            // 이걸 실시간(atk.State)으로 보면, 선수0의 타격이 상대를 Stagger시켜 상대의 동시 반격을
            // 취소시키는 선공 독점이 남는다 (거울 51:49 → disc 난타로 66:33 증폭의 원인).
            bool striking = f.State == FighterState.Active && !f.SwingResolved;
            snap[i] = new DefenseSnap(f.State, f.StateElapsed, f.DownHitConsumed, f.Pos, f.IsExhausted, armored, striking);
        }

        // 완전 동시 해결: snap에 고정된 '이번 틱 타격 중' 플래그로 양측 타격을 적용한다.
        // 한쪽의 타격이 상대를 경직시켜도 상대의 동시 스윙은 취소되지 않는다(인덱스 0 선공 독점 제거 = 거울 대칭).
        for (int i = 0; i < 2; i++)
            if (snap[i].Striking) TryResolveHit(_f[i], _f[1 - i], snap[1 - i]);
        for (int i = 0; i < 2; i++)
        {
            var atk = _f[i];
            if (atk.State == FighterState.Active && atk.StateTimer <= 0f)
            {
                if (!atk.SwingResolved) RegisterWhiff(atk);
                // 후딜 = 무기 기본 × 모션 배율 (약공 0.8 안전 / 강공 1.6 처벌 가능 — T02 RecoveryMult)
                //       × 가드됨 배율 (막힌 공격은 프레임 불리 — 방어자의 턴)
                float recDur = atk.Weapon.RecoverySec * atk.Motion.RecoveryMult
                    * (atk.LastSwingGuarded ? _c.GuardedRecoveryMult : 1f);
                ChangeState(atk, FighterState.Recovery, recDur);
                // [안B] 공격 후 이탈: 카이터(창·채찍)는 후딜 후 일정 시간 후퇴 강제 → '찌르고 빠짐' 리듬.
                if (atk.Weapon.PostAttackRetreatSec > 0f)
                    atk.RepositionUntil = _now + recDur + atk.Weapon.PostAttackRetreatSec;
            }
        }
    }

    private void TryResolveHit(FighterRuntime atk, FighterRuntime def, in DefenseSnap ds)
    {
        float dist = Vec2.Dist(atk.Pos, ds.Pos);
        if (dist > atk.EffRange + 0.05f) return; // 아직 범위 밖 — Active 동안 계속 시도 (거인 EffRange 반영)

        atk.SwingResolved = true;

        // 1) 회피 무적 프레임
        if (ds.State == FighterState.Dodge && ds.StateElapsed <= _c.DodgeIFrameSec)
        {
            RegisterWhiff(atk);
            return;
        }
        // 다운 추가타 1회 제한 (무한 루프 방지)
        if (ds.State is FighterState.Down && ds.DownHitConsumed) { RegisterWhiff(atk); return; }

        float motionMult = atk.MotionKindNow == MotionKind.Heavy ? _c.MotionMultHeavy : _c.MotionMultLight;
        bool inner = atk.Weapon.Range >= _c.MinLongRange && dist < atk.Weapon.Range * _c.InnerRangeRatio;

        // 2) 가드 판정
        if (ds.State == FighterState.Guard)
        {
            // 패링(방패 전용): 가드 진입 후 ParryWindowSec 이내 피격 = 자격 → ParryChance 롤 성공 시 무효+환급+프레임우위.
            // 자격창은 '반응 가드'만 포착(오래 든 가드·스팸 제외 = 타이밍 비용), 성공률은 ParryChance 다이얼(창 계단 회피).
            if (def.Weapon.ParryWindowSec > 0f && ds.StateElapsed <= def.Weapon.ParryWindowSec
                && _rng.Roll(_c.ParryChance))
            {
                ApplyParry(atk, def);
                return;
            }
            atk.LastSwingGuarded = true; // 막힌 칼 = 프레임 불리 (후딜 ×GuardedRecoveryMult)
            float raw = CombatMath.RawDamage(atk.Weapon, motionMult, atk.Def.Stats) * (inner ? _c.InnerRangePenalty : 1f);
            var gr = CombatMath.ResolveGuardHit(raw, atk.Weapon, def.GuardGauge, def.Stamina, _c);
            def.GuardGauge = gr.GuardGaugeAfter;
            def.Stamina = MathF.Max(0f, gr.StaminaAfter);

            var ctx = new CombatMath.HitContext(false, true, false, inner, 1f + CrowdDmgBuff * atk.CrowdMomentum, _rng.Range(_c.VarianceMin, _c.VarianceMax));
            float dmg = CombatMath.FinalDamage(atk.Weapon, motionMult, atk.Def.Stats, def.Def.Stats, ctx, _c);
            ApplyDamage(atk, def, dmg, false, false, true);

            if (gr.IsGuardBreak)
            {
                def.GuardDisabled = true;
                Emit(new GuardBroken(_now, def.Index));
                CrowdFill(atk, 8f);   // 가드 파괴 — 함성
                // 방패는 붕괴 완화(파국적 0% 모드 축소) — 그 외 무기는 기존 풀스태거. 좀비(저HP)는 면역.
                float breakStagger = def.Weapon.ParryWindowSec > 0f ? _c.ShieldGuardBreakStaggerSec : gr.StaggerSec;
                if (!DebuffImmune(def)) ChangeState(def, FighterState.Stagger, breakStagger);
            }
            return;
        }

        // 3) 풀 히트
        bool isCounter = ds.State is FighterState.Windup or FighterState.Recovery;
        bool isCrit = _rng.Roll(CombatMath.CritChancePct(atk.Def.Stats, def.Def.Stats, _c) / 100f);
        if (atk.Has(TraitTable.CatchBreath) && atk.StaminaPct >= 0.80f) isCrit = true; // 숨고르기: ST≥80% 확정 크리
        var hitCtx = new CombatMath.HitContext(isCrit, false, isCounter, inner, 1f + CrowdDmgBuff * atk.CrowdMomentum,
            _rng.Range(_c.VarianceMin, _c.VarianceMax), ds.IsExhausted);
        float damage = CombatMath.FinalDamage(atk.Weapon, motionMult, atk.Def.Stats, def.Def.Stats, hitCtx, _c);

        // 하이퍼아머: 방어자가 중량 강공을 커밋 중인데 들어온 게 약공 → 데미지·카운터딜은 받되 경직 무효.
        // (강공으로 받아쳐야 끊긴다. 약공 스팸으로는 못 막는다 — 중량 무기의 '막을 수 없는 일격' 정체성.)
        bool armorHeld = ds.Armored && atk.MotionKindNow == MotionKind.Light;

        bool wasStaggered = ds.State == FighterState.Stagger;
        bool wasDown = ds.State is FighterState.Down;
        ApplyDamage(atk, def, damage, isCrit, isCounter, false, armorHeld);
        atk.CleanHits++;
        if (isCrit) def.LastCritTakenAt = _now;
        if (def.Hp <= 0f) return;
        if (armorHeld) return; // 경직 무효 — 방어자는 강공 선딜을 그대로 이어간다

        // 경직 처리
        if (wasDown) { def.DownHitConsumed = true; return; }
        // 좀비: HP≤30%면 모든 경직(다운/스태거/히트스턴) 면역 — 피해는 받되 흐름은 안 끊긴다.
        if (DebuffImmune(def)) return;
        if (wasStaggered && atk.MotionKindNow == MotionKind.Heavy)
        {
            // Stagger 중 강공 적중 → 다운
            atk.Knockdowns++;
            Emit(new KnockedDown(_now, def.Index));
            CrowdFill(atk, 15f);   // 넉다운 — 큰 환호
            ChangeState(def, FighterState.Down, _c.DownDurationSec);
            return;
        }
        var pr = CombatMath.ApplyPoiseDamage(def.Poise, def.PoiseMax, atk.Weapon, motionMult, ds.IsExhausted, _c);
        def.Poise = pr.PoiseAfter;
        def.PoiseRegenBlockTimer = _c.PoiseRecoverDelaySec;
        if (pr.IsStagger)
        {
            Emit(new PoiseBroken(_now, def.Index));
            CrowdFill(atk, 5f);   // 자세 붕괴 — 함성
            ChangeState(def, FighterState.Stagger, pr.StunSec);
        }
        else if (def.State is not (FighterState.Stagger or FighterState.Down or FighterState.GetUp))
        {
            ChangeState(def, FighterState.HitStun, pr.StunSec);
        }
    }

    /// <summary>좀비: HP≤30% 시 디버프(경직·출혈·기절) 면역. 피해 자체는 정상.</summary>
    private static bool DebuffImmune(FighterRuntime f) => f.Has(TraitTable.Zombie) && f.HpPct <= 0.30f;

    private void ApplyDamage(FighterRuntime atk, FighterRuntime def, float dmg, bool crit, bool counter, bool guarded, bool armored = false)
    {
        // 선취점: 첫 클린 히트(가드 제외) ×1.25 + 본인에 흡수 쉴드 부여(다음 피격 완충).
        if (!guarded && atk.Has(TraitTable.FirstBlood) && !atk.FirstHitDone)
        {
            atk.FirstHitDone = true;
            dmg *= 1.25f;
            atk.ShieldHp = _c.FirstBloodShield;
            atk.ShieldExpiry = _now + _c.FirstBloodShieldSec;
        }
        dmg *= def.DamageTakenMult;   // 받피 특성 (유리몸·질긴가죽·둔감)
        if (guarded) dmg *= def.GuardDamageMult;   // 봉쇄자: 가드 시 추가 감쇠
        // 흡수 쉴드: 잔량만큼 먼저 흡수 (선취점·향후 액티브)
        if (def.ShieldHp > 0f && _now < def.ShieldExpiry)
        {
            float a = MathF.Min(dmg, def.ShieldHp);
            def.ShieldHp -= a; dmg -= a;
        }
        def.Hp -= dmg;
        atk.DamageDealt += dmg;
        def.ConsecHitsTaken++;
        def.NoHitTimer = 0f;
        Emit(new HitLanded(_now, atk.Index, def.Index, dmg, crit, counter, guarded, armored));
        // 관중 적립: 가드칩 약함 / 크리·카운터 강함 / 결정타(KO) 피날레 보너스. (스태거·넉다운·가드붕괴는 호출부에서 추가.)
        CrowdFill(atk, (guarded ? 1f : (crit || counter) ? 6f : 2f) + (def.Hp <= 0f ? 20f : 0f));
        // 출혈: 칼날이 살을 가른 클린 히트만(가드는 막혀 출혈 없음). 도끼 전용(BleedDps>0).
        if (!guarded && atk.Weapon.BleedDps > 0f && def.Hp > 0f)
            ApplyBleed(atk, def);
    }

    /// <summary>
    /// 패링 성공(방패 전용): 데미지·게이지 칩 무효 + 스태미나 환급 + 프레임 우위(공격자 스윙을 흘려 후딜로).
    /// 방어자는 가드 캔슬로 즉시 행동 가능 — 기존 'oppRecovery 공격 부스트'가 이 창을 처벌(승리 조건).
    /// 기절 스택(패링 한정+감쇠): 공격자가 누적 → 임계 도달 시 기절(스태거).
    /// </summary>
    private void ApplyParry(FighterRuntime atk, FighterRuntime def)
    {
        atk.SwingResolved = true;
        def.Stamina = MathF.Min(def.StaminaMax, def.Stamina + _c.ParryRefundStamina); // 환급 = 프레임우위 자원
        ChangeState(atk, FighterState.Recovery, atk.Weapon.RecoverySec);               // 스윙 흘림 → 후딜(처벌창)
        CrowdFill(def, 5f);   // 패링 — 함성

        atk.ParriedStacks++;
        atk.ParriedDecayAt = _now + _c.ParryStunDecaySec;
        bool stunned = atk.ParriedStacks >= _c.ParryStunStacksMax && !DebuffImmune(atk); // 좀비(저HP)는 기절 면역
        Emit(new Parried(_now, def.Index, atk.Index, stunned ? 0 : atk.ParriedStacks)); // 0 = 누적 임계 → 기절
        if (stunned)
        {
            atk.ParriedStacks = 0;
            ChangeState(atk, FighterState.Stagger, _c.StaggerSec);   // 누적 패링 → 기절
            CrowdFill(def, 8f);
        }
    }

    /// <summary>출혈 1스택 적립·갱신 (합산, 상한 BleedMaxStacks). 지속 시간 갱신(연장 아님).</summary>
    private void ApplyBleed(FighterRuntime atk, FighterRuntime def)
    {
        if (DebuffImmune(def)) return;   // 좀비(저HP): 출혈 면역
        def.BleedStacks = Math.Min(_c.BleedMaxStacks, def.BleedStacks + 1);
        def.BleedDps = atk.Weapon.BleedDps;
        def.BleedExpiry = _now + _c.BleedDurationSec;
        def.BleedSource = atk.Index;
        Emit(new BleedApplied(_now, atk.Index, def.Index, def.BleedStacks));
    }

    private void ChangeState(FighterRuntime f, FighterState to, float timer = 0f)
    {
        if (f.State == to) { f.StateTimer = timer; return; }
        Emit(new StateChanged(_now, f.Index, f.State, to));
        f.State = to;
        f.StateTimer = timer;
        f.StateElapsed = 0f;
    }

    // ───────────────────────── 종료 (문서[4] 10장) ─────────────────────────

    private MatchResult EndByKo()
    {
        bool aDead = _f[0].Hp <= 0f, bDead = _f[1].Hp <= 0f;
        // 동시 KO(양쪽 트레이드): 무조건 무승부 대신 판정 점수로 — 더 잘 싸운 쪽이 이긴다.
        // disc 근접 동시 스윙이 잦아 트레이드가 늘었는데, 점수 승부가 비대칭 매치의 변별력을 회복한다.
        // 거울전은 점수가 대칭이라 무승부가 자연히 유지된다.
        int winner;
        if (aDead && bDead)
        {
            float sa = Score(_f[0]), sb = Score(_f[1]);
            winner = MathF.Abs(sa - sb) < 0.001f ? -1 : (sa > sb ? 0 : 1);
        }
        else winner = aDead ? 1 : 0;
        string reason = winner == -1 ? "Draw" : "KO";
        return Finish(winner, reason);
    }

    private MatchResult EndByJudgement()
    {
        float sa = Score(_f[0]), sb = Score(_f[1]);
        int winner = MathF.Abs(sa - sb) < 0.001f
            ? (MathF.Abs(_f[0].HpPct - _f[1].HpPct) < 0.001f ? -1 : (_f[0].HpPct > _f[1].HpPct ? 0 : 1))
            : (sa > sb ? 0 : 1);
        return Finish(winner, winner == -1 ? "Draw" : "Judgement", sa, sb);
    }

    private float Score(FighterRuntime f)
        => CombatMath.JudgementScore(f.DamageDealt, f.CleanHits, f.Knockdowns,
                                     f.AttackAttempts - f.Whiffs, f.CornerTime, _c);

    private MatchResult Finish(int winner, string reason, float? sa = null, float? sb = null)
    {
        float scoreA = sa ?? Score(_f[0]);
        float scoreB = sb ?? Score(_f[1]);
        Emit(new MatchEnded(_now, winner, reason, scoreA, scoreB));
        return new MatchResult(winner, reason, _now, scoreA, scoreB, Summary(_f[0]), Summary(_f[1]));
    }

    private static MatchFighterStats Summary(FighterRuntime f) => new(
        f.Def.Name, f.DamageDealt, f.CleanHits, f.Knockdowns, f.AttackAttempts,
        f.Whiffs, f.CornerTime, f.MinHpPct, MathF.Max(0f, f.HpPct), f.EverTaunted);

    // ───────────────────────── 관중 (문서[10]) ─────────────────────────
    /// <summary>군중게이지 감쇠 + 기세(유리)/위축(불리) 강도 갱신. 매 틱 — 이번 틱 데미지·이속·directive에 반영.</summary>
    private void CrowdUpdate()
    {
        // 감쇠: 0으로 회귀. 양쪽 비교전(소극)이면 ×2 가속(야유로 관중이 식음).
        bool passive = _f[0].State is FighterState.Idle or FighterState.Move
                    && _f[1].State is FighterState.Idle or FighterState.Move;
        float dec = CrowdDecayPerSec * (passive ? 2f : 1f) * Dt;
        if (_crowd > 0f) _crowd = MathF.Max(0f, _crowd - dec);
        else if (_crowd < 0f) _crowd = MathF.Min(0f, _crowd + dec);

        // 기세/위축 강도: |게이지| 데드존 위에서만 선형(0~1). 거울전은 대칭이라 _crowd≈0 → norm 0 → 버프 0.
        float a = MathF.Abs(_crowd);
        float norm = a <= CrowdDeadzone ? 0f : MathF.Min(1f, (a - CrowdDeadzone) / (CrowdMaxAbs - CrowdDeadzone));
        int fav = _crowd >= 0f ? 0 : 1;
        _f[fav].CrowdMomentum = norm;     _f[fav].CrowdPressure = 0f;
        _f[1 - fav].CrowdMomentum = 0f;   _f[1 - fav].CrowdPressure = norm;
    }

    /// <summary>멋진 행동 → 행위자 편으로 게이지 적립. 빈사(HP&lt;30%)면 ×2 = 역전 호응. 문서[10] §3.</summary>
    private void CrowdFill(FighterRuntime actor, float delta)
    {
        float d = delta * CrowdFillScale * (actor.HpPct < 0.30f ? 2f : 1f);
        _crowd = Math.Clamp(_crowd + (actor.Index == 0 ? d : -d), -CrowdMaxAbs, CrowdMaxAbs);
    }

    private void Emit(SimEvent e) => _events?.Add(e);
}

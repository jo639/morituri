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

    public MatchSim(BalanceConstants? constants = null) => _c = constants ?? BalanceConstants.Default;

    public MatchResult Run(FighterDef a, FighterDef b, ulong seed, List<SimEvent>? events = null)
    {
        _events = events;
        _rng = new SimRandom(seed);
        _f[0] = CreateRuntime(0, a, _c.ArenaWidth / 2f - _c.StartGap / 2f);
        _f[1] = CreateRuntime(1, b, _c.ArenaWidth / 2f + _c.StartGap / 2f);
        _now = 0f; _tick = 0;

        int strategyTicks = Math.Max(1, (int)MathF.Round(_c.StrategyTickSec / Dt));
        int decisionTicks = Math.Max(1, (int)MathF.Round(_c.DecisionTickSec / Dt));
        int maxTicks = (int)(_c.MatchTimeSec / Dt);

        for (_tick = 0; _tick < maxTicks; _tick++)
        {
            _now = _tick * Dt;
            RecordSnapshots();

            for (int i = 0; i < 2; i++) PassiveUpdate(_f[i]);
            if (_tick % strategyTicks == 0)
                for (int i = 0; i < 2; i++) StrategyTick(_f[i]);
            if (_tick % decisionTicks == 0)
                for (int i = 0; i < 2; i++) TacticTick(_f[i]);
            for (int i = 0; i < 2; i++) FsmAdvance(_f[i]);
            ResolutionPhase();

            if (_f[0].Hp <= 0f || _f[1].Hp <= 0f) return EndByKo();
        }
        return EndByJudgement();
    }

    // ───────────────────────── 초기화 ─────────────────────────

    private FighterRuntime CreateRuntime(int idx, FighterDef def, float startPos)
    {
        var weapon = WeaponTable.Get(def.WeaponId);
        var rt = new FighterRuntime
        {
            Index = idx, Def = def, Weapon = weapon,
            Profile = TacticsTable.Get(def.TacticsId),
            Personality = PersonalityTable.Get(def.PersonalityId),
        };
        rt.HpMax = def.Stats.HpMax;
        rt.Hp = rt.HpMax;
        rt.StaminaMax = CombatMath.StaminaMax(def.Stats, _c);
        rt.Stamina = rt.StaminaMax;
        rt.PoiseMax = weapon.PoiseMax;
        rt.Poise = rt.PoiseMax;
        rt.GuardGaugeMax = CombatMath.GuardGaugeMax(def.Stats, weapon, _c);
        rt.GuardGauge = rt.GuardGaugeMax;
        rt.MoveSpeed = CombatMath.MoveSpeedMps(def.Stats, _c);
        rt.PerceptDelaySec = CombatMath.PerceptionDelay(def.Stats);
        rt.Position = startPos;
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
                f.State, f.MotionKindNow, f.Position, f.HpPct,
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

    // ───────────────────────── 패시브 (자원/타이머) ─────────────────────────

    private void PassiveUpdate(FighterRuntime f)
    {
        f.StateElapsed += Dt;
        f.StateTimer -= Dt;
        f.NoHitTimer += Dt;
        if (f.NoHitTimer > 3f) f.ConsecHitsTaken = 0;
        f.MinHpPct = MathF.Min(f.MinHpPct, f.HpPct);

        // 스태미나 (문서[4] 6장)
        float regen = f.State switch
        {
            FighterState.Idle or FighterState.Taunt or FighterState.HitStun
                or FighterState.Stagger or FighterState.Down or FighterState.GetUp => _c.StamRegenIdle,
            FighterState.Move => _c.StamRegenMoving,
            FighterState.Guard => -_c.StamCostGuardPerSec,
            _ => 0f, // 공격 모션/회피 중 회복 없음
        };
        if (regen > 0f) regen *= f.Dir.StamRegenMult;
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

        // 코너 체류 (판정 패널티)
        if (f.Position <= _c.CornerZone || f.Position >= _c.ArenaWidth - _c.CornerZone)
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
            OppExhaustedPerceived: opp.IsExhausted);
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
        float dist = MathF.Abs(opp.Position - f.Position);
        // 교전 거리 보정: 선호 거리가 자기 유효 공격 거리보다 멀면 영원한 대치가 된다 (거울 매치 KO 0%로 검출).
        // "때릴 의지가 있으면 닿는 곳까지 간다" — 단 NoAttack(명예중시 HoldOff) 중엔 원래 선호 거리 유지.
        float engage = d.NoAttack > 0.5f ? d.PreferredDistance
                     : MathF.Min(d.PreferredDistance, f.Weapon.Range * 0.8f);
        float gap = dist - engage;
        // 지친 상대는 반격할 수 없다 = 캔슬 불가 상태와 동급의 확정 처벌 창.
        bool oppLocked = opp.IsExhausted
                      || opp.State is FighterState.Recovery or FighterState.Stagger
                      or FighterState.Down or FighterState.Taunt or FighterState.HitStun;
        // 평시엔 인지 지연 보상 마진(사거리 끝 발사는 빈 곳을 때림),
        // 확정 기회(캔슬 불가 상태의 상대)엔 풀 사거리 — 후딜 처벌이 마진에 막히면 카운터형이 죽는다.
        bool inRange = dist <= f.Weapon.Range * (oppLocked ? 1.0f : 0.88f);
        bool oppWindup = opp.State == FighterState.Windup;
        bool oppRecovery = opp.State == FighterState.Recovery;
        bool oppGuard = opp.State == FighterState.Guard;
        bool oppDown = opp.State is FighterState.Down or FighterState.GetUp;

        Span<float> score = stackalloc float[9];
        score[(int)ActionRequest.Approach] = gap > d.DistanceTolerance ? 0.45f + MathF.Min(1f, gap / 2f) * 0.8f : 0.05f;
        score[(int)ActionRequest.Retreat] = gap < -d.DistanceTolerance ? 0.45f + MathF.Min(1f, -gap / 2f) * 0.8f : 0.05f;
        score[(int)ActionRequest.Strafe] = 0.12f;

        // 자기 약점 거리(inner ×0.6 구간)에서의 공격은 반토막 가치 — 창/채찍은 먼저 거리를 벌리는 게 정답
        bool selfInner = f.Weapon.Range >= _c.MinLongRange && dist < f.Weapon.Range * _c.InnerRangeRatio;
        float innerMul = selfInner ? 0.45f : 1f;
        float light = inRange ? (0.25f + d.Aggression) * innerMul : 0f; // 사거리 안 기본 공격성 (전 전술 공통 바닥값)
        float heavy = inRange ? d.Aggression * (0.4f + 0.6f * d.RiskTolerance) * (1f + d.HeavyBias) * innerMul : 0f;
        float feint = (dist <= f.Weapon.Range * 1.3f) ? d.FeintRate * 0.8f : 0f;

        bool oppHeavyWindup = oppWindup && opp.MotionKind == MotionKind.Heavy;

        // 리치 우위: 상대는 내 사거리 안, 나는 상대 사거리 밖 = 일방 유효타 구간.
        // 창/채찍 "견제"의 기계적 본질 (문서[4] 8장 거리 대역 / 기획서 "창=견제 특화").
        var oppRt = _f[1 - f.Index];
        float oppRange = oppRt.Weapon.Range;
        bool reachAdvantage = inRange && dist > oppRange + 0.1f;
        if (reachAdvantage) { light *= 2.2f; feint *= 1.3f; }

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
        if (f.Stamina < _c.StamCostDodge || f.IsExhausted) return false;
        f.Stamina -= _c.StamCostDodge;

        var opp = _f[1 - f.Index];
        float backDir = MathF.Sign(f.Position - opp.Position); // 상대 반대쪽
        if (backDir == 0f) backDir = f.Index == 0 ? -1f : 1f;
        if (!away) backDir = -backDir;

        float target = f.Position + backDir * _c.DodgeDistance;
        if (target <= 0.5f || target >= _c.ArenaWidth - 0.5f)
        {
            // 코너 통과 롤: 후방 공간 없음 → 상대 등 뒤로 빠져나감 (원형 경기장 측면 이동의 1D 등가)
            target = opp.Position + (opp.Position - f.Position >= 0f ? 1f : -1f) * 1.6f;
        }
        f.Position = Math.Clamp(target, 0.5f, _c.ArenaWidth - 0.5f);
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

    private void FsmAdvance(FighterRuntime f)
    {
        var opp = _f[1 - f.Index];
        switch (f.State)
        {
            case FighterState.Move:
            {
                float speed = f.MoveSpeed * (f.IsExhausted ? _c.ExhaustMoveSpeedMult : 1f);
                float dir = MathF.Sign(opp.Position - f.Position);
                if (f.CurrentAction == ActionRequest.Retreat) dir = -dir;
                if (f.CurrentAction != ActionRequest.Strafe)
                    f.Position = Math.Clamp(f.Position + dir * speed * Dt, 0.5f, _c.ArenaWidth - 0.5f);
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
        FighterState State, float StateElapsed, bool DownHitConsumed, float Position, bool IsExhausted);

    /// <summary>
    /// 동시 해결: 양측의 방어 상태를 먼저 캡처한 뒤 상호 적용한다.
    /// 순차 적용 시 선수 0의 타격이 항상 먼저 들어가 동시 교환(트레이드)을 독점하는
    /// 공정성 버그가 생긴다 (거울 매치 100:0으로 검출됨).
    /// </summary>
    private void ResolutionPhase()
    {
        Span<DefenseSnap> snap = stackalloc DefenseSnap[2];
        for (int i = 0; i < 2; i++)
            snap[i] = new DefenseSnap(_f[i].State, _f[i].StateElapsed, _f[i].DownHitConsumed,
                                      _f[i].Position, _f[i].IsExhausted);

        for (int i = 0; i < 2; i++)
        {
            var atk = _f[i];
            if (atk.State == FighterState.Active && !atk.SwingResolved)
                TryResolveHit(atk, _f[1 - i], snap[1 - i]);
        }
        for (int i = 0; i < 2; i++)
        {
            var atk = _f[i];
            if (atk.State == FighterState.Active && atk.StateTimer <= 0f)
            {
                if (!atk.SwingResolved) RegisterWhiff(atk);
                // 후딜 = 무기 기본 × 모션 배율 (약공 0.8 안전 / 강공 1.6 처벌 가능 — T02 RecoveryMult)
                //       × 가드됨 배율 (막힌 공격은 프레임 불리 — 방어자의 턴)
                ChangeState(atk, FighterState.Recovery, atk.Weapon.RecoverySec * atk.Motion.RecoveryMult
                    * (atk.LastSwingGuarded ? _c.GuardedRecoveryMult : 1f));
            }
        }
    }

    private void TryResolveHit(FighterRuntime atk, FighterRuntime def, in DefenseSnap ds)
    {
        float dist = MathF.Abs(atk.Position - ds.Position);
        if (dist > atk.Weapon.Range + 0.05f) return; // 아직 범위 밖 — Active 동안 계속 시도

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
            atk.LastSwingGuarded = true; // 막힌 칼 = 프레임 불리 (후딜 ×GuardedRecoveryMult)
            float raw = CombatMath.RawDamage(atk.Weapon, motionMult, atk.Def.Stats) * (inner ? _c.InnerRangePenalty : 1f);
            var gr = CombatMath.ResolveGuardHit(raw, atk.Weapon, def.GuardGauge, def.Stamina, _c);
            def.GuardGauge = gr.GuardGaugeAfter;
            def.Stamina = MathF.Max(0f, gr.StaminaAfter);

            var ctx = new CombatMath.HitContext(false, true, false, inner, 1f, _rng.Range(_c.VarianceMin, _c.VarianceMax));
            float dmg = CombatMath.FinalDamage(atk.Weapon, motionMult, atk.Def.Stats, def.Def.Stats, ctx, _c);
            ApplyDamage(atk, def, dmg, false, false, true);

            if (gr.IsGuardBreak)
            {
                def.GuardDisabled = true;
                Emit(new GuardBroken(_now, def.Index));
                ChangeState(def, FighterState.Stagger, gr.StaggerSec);
            }
            return;
        }

        // 3) 풀 히트
        bool isCounter = ds.State is FighterState.Windup or FighterState.Recovery;
        bool isCrit = _rng.Roll(CombatMath.CritChancePct(atk.Def.Stats, def.Def.Stats, _c) / 100f);
        var hitCtx = new CombatMath.HitContext(isCrit, false, isCounter, inner, 1f,
            _rng.Range(_c.VarianceMin, _c.VarianceMax), ds.IsExhausted);
        float damage = CombatMath.FinalDamage(atk.Weapon, motionMult, atk.Def.Stats, def.Def.Stats, hitCtx, _c);

        bool wasStaggered = ds.State == FighterState.Stagger;
        bool wasDown = ds.State is FighterState.Down;
        ApplyDamage(atk, def, damage, isCrit, isCounter, false);
        atk.CleanHits++;
        if (isCrit) def.LastCritTakenAt = _now;
        if (def.Hp <= 0f) return;

        // 경직 처리
        if (wasDown) { def.DownHitConsumed = true; return; }
        if (wasStaggered && atk.MotionKindNow == MotionKind.Heavy)
        {
            // Stagger 중 강공 적중 → 다운
            atk.Knockdowns++;
            Emit(new KnockedDown(_now, def.Index));
            ChangeState(def, FighterState.Down, _c.DownDurationSec);
            return;
        }
        var pr = CombatMath.ApplyPoiseDamage(def.Poise, def.PoiseMax, atk.Weapon, motionMult, ds.IsExhausted, _c);
        def.Poise = pr.PoiseAfter;
        def.PoiseRegenBlockTimer = _c.PoiseRecoverDelaySec;
        if (pr.IsStagger)
        {
            Emit(new PoiseBroken(_now, def.Index));
            ChangeState(def, FighterState.Stagger, pr.StunSec);
        }
        else if (def.State is not (FighterState.Stagger or FighterState.Down or FighterState.GetUp))
        {
            ChangeState(def, FighterState.HitStun, pr.StunSec);
        }
    }

    private void ApplyDamage(FighterRuntime atk, FighterRuntime def, float dmg, bool crit, bool counter, bool guarded)
    {
        def.Hp -= dmg;
        atk.DamageDealt += dmg;
        def.ConsecHitsTaken++;
        def.NoHitTimer = 0f;
        Emit(new HitLanded(_now, atk.Index, def.Index, dmg, crit, counter, guarded));
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
        int winner = aDead && bDead ? -1 : aDead ? 1 : 0;
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

    private void Emit(SimEvent e) => _events?.Add(e);
}

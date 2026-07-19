using Morituri.Sim.Combat;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;

namespace Morituri.Sim.Match;

public sealed record MatchFighterStats(
    string Name, float DamageDealt, int CleanHits, int Knockdowns, int AttackAttempts,
    int Whiffs, float CornerTime, float MinHpPct, float HpRemainPct, bool Taunted,
    int Blocks = 0, int Dodges = 0);

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
    private readonly SimRandom[] _decisionRng = new SimRandom[2];  // 선수별 판단주기 지터 전용 파생 스트림(액션 RNG 순서 불변)
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
    private readonly IReadOnlyDictionary<string, TacticsProfile>? _tacticOverride; // 튜닝 스윕용 전술 프로파일 주입

    public MatchSim(BalanceConstants? constants = null, IReadOnlyDictionary<string, float>? weaponDmgScale = null,
                    IReadOnlyDictionary<string, TacticsProfile>? tacticOverride = null)
    {
        _c = constants ?? BalanceConstants.Default;
        _weaponDmgScale = weaponDmgScale;
        _tacticOverride = tacticOverride;
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
        // 판단주기 지터(1단계): 선수별 파생 RNG. Derive는 _rng 상태를 소비하지 않아 액션 RNG 수열 불변.
        // 거울매치는 둘이 다른 스트림 → per-game 비대칭이지만 같은 분포라 통계적으로 50/50 수렴(실측 검증 대상).
        _decisionRng[0] = _rng.Derive(0xD1CE05UL);
        _decisionRng[1] = _rng.Derive(0xD1CE06UL);
        _now = 0f; _tick = 0; _crowd = 0f;

        int strategyTicks = Math.Max(1, (int)MathF.Round(_c.StrategyTickSec / Dt));
        int decisionTicks = Math.Max(1, (int)MathF.Round(_c.DecisionTickSec / Dt));  // 인터럽트 평가 = 고정 박자(메타 보존)
        int maxTicks = (int)(_c.MatchTimeSec / Dt);

        for (_tick = 0; _tick < maxTicks; _tick++)
        {
            _now = _tick * Dt;
            RecordSnapshots();
            CrowdUpdate();   // 군중게이지 감쇠 + 기세/위축 강도 갱신 (이번 틱 데미지·이속·directive에 반영)
            for (int i = 0; i < 2; i++) ApplyTacticSwitch(_f[i]);   // 감독 실시간 개입(예약 시각 도달 시 전술 교체)

            // 처리 순서 교대(_tick & 1): A/B가 같은 난수열에서 순차로 행동을 뽑는 비대칭을
            // 매 틱 상쇄 → disc 근접 고정 난타에서도 거울전 대칭 보존. (결정론은 _tick 기반이라 유지)
            int p0 = _tick & 1, p1 = 1 - p0;
            for (int i = 0; i < 2; i++) PassiveUpdate(_f[i]);
            if (_tick % strategyTicks == 0)
                { StrategyTick(_f[p0]); StrategyTick(_f[p1]); }
            // 인터럽트층(도발·회피·강제강공·후딜이탈) = 고정 박자(트리거 메타 보존). 소비 시 같은 틱 행동 스킵.
            bool consumed0 = false, consumed1 = false;
            if (_tick % decisionTicks == 0)
                { consumed0 = TacticInterrupts(_f[p0]); consumed1 = TacticInterrupts(_f[p1]); }
            // 유틸리티 행동층 = 선수별 지터 박자(평균=DecisionTickSec, 폭만 흔듦). 둘이 겹칠 때만 parity 순서 교대.
            // [7]§1: 장착 액티브는 일반 SelectAction보다 먼저 평가 — 발동 시 그 틱의 일반 행동 생략.
            if (_tick >= _f[p0].NextDecisionTick) { TickPassives(_f[p0], _f[p1]); if (!consumed0 && !TrySkillActivate(_f[p0], _f[p1])) TacticAction(_f[p0]); ScheduleNextDecision(_f[p0]); }
            if (_tick >= _f[p1].NextDecisionTick) { TickPassives(_f[p1], _f[p0]); if (!consumed1 && !TrySkillActivate(_f[p1], _f[p0])) TacticAction(_f[p1]); ScheduleNextDecision(_f[p1]); }
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
            Profile = _tacticOverride != null && _tacticOverride.TryGetValue(def.TacticsId, out var tp) ? tp : TacticsTable.Get(def.TacticsId),
            Personality = PersonalityTable.Get(def.PersonalityId),
            Switches = def.TacticSwitches,   // 감독 실시간 개입(시각 예약) — null이면 기존과 완전 동일
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
                // 무기 액티브([7]§4): 장착된 액티브는 런타임에 명세를 실어둔다([7]§1 — 동시 액티브 1개).
                if (rt.ActiveSkill == null && SkillTable.Exists(id) && SkillTable.Get(id).Active is { } asp)
                    rt.ActiveSkill = asp;
                // 성격 패시브([7]§5): proc형 명세. 상시형의 공격성 페널티는 영구 Override로(방비).
                if (rt.Passive == null && SkillTable.Exists(id) && SkillTable.Get(id).Passive is { } psp)
                {
                    rt.Passive = psp;
                    if (psp.AggressionAdd != 0f)
                        rt.Overrides.Add(new ActiveOverride
                        {
                            Mods = new[] { ParamMod.Add(TParam.Aggression, psp.AggressionAdd) },
                            ExpiresAt = float.MaxValue, ReasonTag = psp.ReasonTag,
                        });
                }
            }

        // 감정(T10): 일시적 심리 상태를 결정 경로에만 주입 (트리거 확률 + Directive 가중치). 데미지 수식 무관.
        if (def.EmotionIds != null)
            foreach (var id in def.EmotionIds)
            {
                if (!EmotionTable.Exists(id)) continue;
                var e = EmotionTable.Get(id);
                rt.EmotionTriggerProbMod += e.TriggerProbMod;
                rt.EmotionMods.AddRange(e.Mods);
                // 가독성 원칙([3]§8): 경기 시작 시 감정 아이콘 노출 (Decision reasonTag).
                Emit(new Decision(0f, idx, e.Id, "Strategy", 3f));
            }

        // 관계(T11): 특정 상대를 향한 누적 관계를 결정 경로에만 주입(트리거 게이트 + decision 가중치). 데미지 무관.
        if (def.RelationToOpp is { } relType)
        {
            var rd = RelationTable.Get(relType);
            rt.Relation = relType;
            float k = def.RelationIntensity <= 0f ? 1f : MathF.Min(1f, def.RelationIntensity);
            foreach (var m in rd.Mods) rt.RelationMods.Add(ParamMod.Add(m.Param, m.Value * k));
            rt.RelationTriggerProbMod += rd.TriggerProbMod * k;
            Emit(new Decision(0f, idx, "REL_" + relType.ToString().ToUpperInvariant(), "Strategy", 3f));
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

        // 무기 액티브([7]) 프레임 처리: 연격 조기 종료 · 공간 지배 자동 견제 · 심판의 일격 차지 해결
        if (f.ActiveSkill is { } ask)
        {
            var other = _f[1 - f.Index];
            if (ask.EarlyEndGapM > 0f && _now < f.SkillBuffUntil
                && Vec2.Dist(f.Pos, other.Pos) > ask.EarlyEndGapM) f.SkillBuffUntil = _now;   // 연격: 멀어지면 종료
            if (ask.AutoPokeMult > 0f && _now < f.SkillBuffUntil && _now >= f.SkillNextPokeAt
                && !f.IsAttackSwing && other.Hp > 0f
                && Vec2.Dist(f.Pos, other.Pos) <= f.EffRange)                                  // 공간 지배: 진입자 자동 견제
            {
                f.SkillNextPokeAt = _now + ask.AutoPokeIntervalSec;
                bool g = other.State == FighterState.Guard;
                var pctx = new CombatMath.HitContext(false, g, false, false,
                    1f + CrowdDmgBuff * f.CrowdMomentum, _rng.Range(_c.VarianceMin, _c.VarianceMax), other.IsExhausted);
                ApplyDamage(f, other, CombatMath.FinalDamage(f.Weapon, _c.MotionMultLight * ask.AutoPokeMult,
                    f.Def.Stats, other.Def.Stats, pctx, _c), false, false, g);
            }
            if (f.SkillExecStrikeAt > 0f && _now >= f.SkillExecStrikeAt)                       // 심판의 일격: 차지 완료
            {
                f.SkillExecStrikeAt = -1f; f.SkillBuffUntil = _now;
                ResolveExecute(f, other, ask);
            }
        }

        // 스태미나 (문서[4] 6장)
        float regen = f.State switch
        {
            FighterState.Idle or FighterState.Taunt or FighterState.HitStun
                or FighterState.Stagger or FighterState.Down or FighterState.GetUp => _c.StamRegenIdle,
            FighterState.Move => _c.StamRegenMoving,
            FighterState.Guard => -_c.StamCostGuardPerSec,
            _ => 0f, // 공격 모션/회피 중 회복 없음
        };
        // 카이팅 비용(B): 거리 유지(후퇴/선회)로 빠지면 회복 대신 소모 — 영원히 카이팅 못 한다.
        // 위치 이중안정을 스태미나 소모로 완충. 과금 범위는 KiteCostMinRange(기본 장사거리 전용, 튜닝 스윕 차원).
        if (f.Weapon.Range >= _c.KiteCostMinRange && f.State == FighterState.Move
            && f.CurrentAction is ActionRequest.Retreat or ActionRequest.Strafe
            && SkillNow(f) is not { KiteExempt: true })   // 공간 지배([7]): 발동 중 카이팅 소모 면제
            regen = -_c.KiteStamCostPerSec;
        if (regen > 0f) regen *= f.Dir.StamRegenMult * f.StamRegenTraitMult
                               * (PassiveNow(f) is { } prg ? prg.IdleRegenMult : 1f);   // 여유([7]§5 오만)
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
            SecSinceTaunted: _now - f.LastTauntedAt,
            OppIsNemesis: f.Relation == RelationType.Nemesis,   // 관계(T11) 게이트 — 특정 상대 한정 행동
            OppIsRival: f.Relation == RelationType.Rival,
            OppIsFeared: f.Relation == RelationType.Fear);
    }

    private void StrategyTick(FighterRuntime f)
    {
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        var ctx = BuildTriggerContext(f, opp);

        // 성격 Override 규칙 + 전술 고유 조건 (같은 엔진 — 문서[5] 5장). 감정(T10)·관계(T11)가 트리거 확률을 가감(의사결정).
        float probMod = f.Personality.GlobalProbMod + f.EmotionTriggerProbMod + f.RelationTriggerProbMod;
        EvalOverrideRules(f, f.Personality.Rules, ctx, probMod);
        if (f.Profile.UniqueRule is { } unique)
            EvalOverrideRules(f, new[] { unique }, ctx, probMod);

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

    /// <summary>다음 전술 판단 틱 예약 — 평균 = DecisionTickSec × 반응(RCT) 배율(2단계), [Min,Max]Mult로 매번 지터.
    /// 중앙값(반응속도)은 RCT 스탯이 정하고(평균선수 RCT 70 = ×1.0 → baseline 불변), 폭은 전역 — 인간의
    /// 불규칙 반응 리듬 + 캐릭터별 반응속도 정체성.</summary>
    private void ScheduleNextDecision(FighterRuntime f)
    {
        float mean = _c.DecisionTickSec * CombatMath.DecisionCadenceFactor(f.Def.Stats);
        float mult = _decisionRng[f.Index].Range(_c.DecisionJitterMinMult, _c.DecisionJitterMaxMult);
        int interval = Math.Max(1, (int)MathF.Round(mean * mult / Dt));
        f.NextDecisionTick = _tick + interval;
    }

    /// <summary>
    /// 성격 Interrupt(도발·회피·강제강공)와 후딜 이탈 = 즉발 반응층. **정상(고정) 박자로 평가** — 판단주기 지터가
    /// 이걸 흔들면 인터럽트 평가가 선수 자신의 공격-후딜 사이클과 탈동조돼 transient 트리거(도발 등)를 과포착해
    /// 트리거 메타가 붕괴한다(거울 도발 21→57%로 검출). 반응 메타 보존을 위해 지터에서 분리.
    /// 반환: 인터럽트가 이번 턴을 소비(행동 시작)했으면 true → 같은 틱 유틸리티 행동 스킵.
    /// </summary>
    private bool TacticInterrupts(FighterRuntime f)
    {
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        var ctx = BuildTriggerContext(f, opp);

        if (TryInterrupts(f, ctx)) return true;

        if (f.PendingForced != ActionRequest.None)
        {
            if (TryStartAction(f, f.PendingForced, opp)) { f.PendingForced = ActionRequest.None; return true; }
        }

        // [안B] 공격 후 이탈 창: 카이터는 후퇴 강제(찌르고 빠짐).
        if (_now < f.RepositionUntil)
        {
            TryStartAction(f, ActionRequest.Retreat, opp);   // 후딜 중이면 캔슬불가로 무효 → 후딜 끝나면 후퇴 발동
            return true;
        }
        return false;
    }

    /// <summary>유틸리티 행동(접근/후퇴/선회/Hold/공격/가드) 선택 = 교전 리듬층. **선수별 지터 박자로 평가** —
    /// 메트로놈 같은 0.2s 격자 대신 불규칙한 인간 반응 리듬. 트리거 메타와 무관(위 인터럽트층이 따로 담당).</summary>
    private void TacticAction(FighterRuntime f)
    {
        f.RebuildDirective(_now);
        var opp = Perceive(f);
        var action = SelectAction(f, opp);
        if (action != f.CurrentAction || f.State == FighterState.Idle)
            TryStartAction(f, action, opp);
    }

    private bool TryInterrupts(FighterRuntime f, in TriggerContext ctx)
    {
        foreach (var r in f.Personality.Rules)
            if (TryInterruptRule(f, r, ctx)) return true;
        // 관계(T11) 게이트 룰 — 그 상대에게만 켜지는 행동(원수 복수 도발 등). 성격 인터럽트 다음 우선순위.
        if (f.Relation is { } relt && RelationTable.Get(relt).Rule is { } relRule
            && TryInterruptRule(f, relRule, ctx)) return true;
        return false;
    }

    private bool TryInterruptRule(FighterRuntime f, TriggerRule r, in TriggerContext ctx)
    {
        if (r.Kind != TriggerEffectKind.Interrupt) return false;
        if (f.CooldownUntil.TryGetValue(r.Id, out float until) && _now < until) return false;
        if (!TriggerEval.Matches(r, ctx)) return false;
        // 도발만 전역 보정: 판단주기 지터가 전투를 더 결단적으로 만들어 "우세+건강/스태거" 도발 조건 노출이
        // 늘어 도발이 증폭됐다(거울 21→50%). TauntProbMult로 도발 메타만 새 운영점에 재정렬(다른 인터럽트 무관).
        float pMod = (1f + f.Personality.GlobalProbMod + f.EmotionTriggerProbMod + f.RelationTriggerProbMod) * (r.Interrupt == InterruptAction.Taunt ? _c.TauntProbMult : 1f);
        if (!_rng.Roll(r.Probability * pMod)) return false;

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
        // 황제의 위압([7]§5 오만): 도발 분노 2배 + 본인 크리율 버프
        float rageMult = f.Passive is { Trigger: PassiveTrigger.AfterTaunt } ? 2f : 1f;
        opp.Overrides.Add(new ActiveOverride
        {
            Mods = new[]
            {
                ParamMod.Add(TParam.Aggression, _c.TauntRageAggression * rageMult),
                ParamMod.Add(TParam.CommitThreshold, _c.TauntRageCommitAdd * rageMult),
            },
            ExpiresAt = _now + _c.TauntRageDurationSec,
            ReasonTag = "RAGED",
        });
        Emit(new Decision(_now, opp.Index, "RAGED", "Strategy", _c.TauntRageDurationSec));
        if (f.Passive is { Trigger: PassiveTrigger.AfterTaunt } pim) ProcPassive(f, pim, pim.Duration);
        CrowdStackGain(f);   // 관중몰이: 도발
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

    /// <summary>
    /// 스페이싱 의도(Close/Hold/Space) 히스테리시스 갱신(안2). gap이 교전거리 ±band를 명백히 벗어나면 좁힘/벌림으로
    /// 진입하고, 중심쪽으로 band×ReleaseRatio 안까지 돌아와야 Hold로 해제(이중임계). 전환은 SpacingDwellSec마다 1회로
    /// 제한 → 0.2s 결정틱마다 Approach↔Retreat가 뒤집히던 잔떨림(거리 댄스)을 commit된 결단으로 바꾼다.
    /// </summary>
    private void UpdateSpacingIntent(FighterRuntime f, float gap, float band)
    {
        SpacingIntent want = f.Intent;
        if (gap > band) want = SpacingIntent.Close;            // 명백히 멀다 → 좁힌다
        else if (gap < -band) want = SpacingIntent.Space;      // 명백히 가깝다 → 벌린다
        else                                                   // 밴드 안: 중심쪽으로 충분히 돌아왔을 때만 Hold 해제
        {
            float inner = band * _c.SpacingHoldReleaseRatio;
            if (f.Intent == SpacingIntent.Close && gap <= inner) want = SpacingIntent.Hold;
            else if (f.Intent == SpacingIntent.Space && gap >= -inner) want = SpacingIntent.Hold;
        }
        if (want != f.Intent && (_now - f.IntentSince) >= _c.SpacingDwellSec)
        {
            f.Intent = want;
            f.IntentSince = _now;
        }
    }

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

        // 스페이싱 의도(안2): Close/Hold/Space 3-상태 히스테리시스. gap 경계에서 매 틱 뒤집히던 거리 댄스를
        // commit(최소 dwell·이중임계)으로 바꾸고, Hold(중립 대기)를 도입해 "할 게 없을 때 빙빙 도는" 무의미한
        // 선회를 제자리 회복·관망으로 대체한다.
        UpdateSpacingIntent(f, gap, d.DistanceTolerance);

        Span<float> score = stackalloc float[10];
        // 접근은 Close 의도일 때만, 후퇴는 Space 의도일 때만 — 의도가 안정돼 경계 잔떨림 제거. (후퇴는 경계서 벽 막힘 → 감쇠)
        score[(int)ActionRequest.Approach] = f.Intent == SpacingIntent.Close
            ? 0.45f + MathF.Min(1f, gap / 2f) * 0.8f : 0.03f;
        score[(int)ActionRequest.Retreat] = (f.Intent == SpacingIntent.Space
            ? 0.45f + MathF.Min(1f, -gap / 2f) * 0.8f : 0.03f) * (1f - 0.6f * edgeProx);
        // 선회(B 핵심): Space 의도일 때만 — 카이터의 스윗스팟 유지(장사거리) + 근접 시 거리벌리기. 의도 없는 기본
        // 선회(옛 0.10 바닥)는 제거 → "빙빙 도는" 인위적 동작이 사라지고 Hold가 그 자리를 대신한다.
        bool wantSpace = f.Intent == SpacingIntent.Space
                      && (gap < -d.DistanceTolerance || (f.Weapon.Range >= _c.MinLongRange && gap < d.DistanceTolerance));
        score[(int)ActionRequest.Strafe] = wantSpace ? 0.40f + 0.7f * edgeProx : 0.02f;
        // Hold(중립 대기): 교전거리에 안착(Hold 의도)했고 더 나은 행동이 없을 때 제자리에서 회복·관망(옛 Strafe 바닥값 대체).
        score[(int)ActionRequest.Hold] = f.Intent == SpacingIntent.Hold ? 0.12f : 0.02f;

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
        // 오의 비축([7]§1 코스트 우선): 액티브가 쿨을 마쳤거나 곧 마치는데 ST가 코스트에 못 미치면
        // 평범한 스윙이 그 한 번을 삼켜버린다(계측: 코스트 기각이 판단 틱의 40~60%). 준비된 스킬이 있으면
        // 그 코스트만큼을 평시 비축선에 얹어, 스킬을 쓸 몫을 남긴다 — 확정 기회(oppLocked) 면제는 그대로.
        // 스킬 미장착(액티브 null)이면 이 보정은 전혀 걸리지 않는다 = 상성 매트릭스 무영향.
        // ⚠ 타격형(Strike/Charge)은 제외한다. 그쪽은 스킬 자체가 '한 대'라서 ST를 보장해 주면
        //    평타를 아낀 값보다 스킬 타격이 더 커져 순이득이 된다 — 실측에서 쇄도 베기 승률 0%→100%,
        //    대지 강타 0%→93%로 무너졌다. 타격형의 낮은 발동률은 ST22가 매기는 정당한 값이다.
        if (!oppLocked && f.ActiveSkill is { StCost: > 0f } rsp
            && rsp.Kind is not (ActiveKind.Strike or ActiveKind.Charge)
            && _now >= f.SkillReadyAt - _c.SkillReserveLookaheadSec)
            reserveAbs = MathF.Max(reserveAbs, MathF.Min(rsp.StCost, f.StaminaMax * _c.SkillReserveMaxPct));
        if (f.Stamina - _c.StamCostAttackLight < reserveAbs || f.IsExhausted) light = 0f;
        if (f.Stamina - _c.StamCostAttackHeavy < reserveAbs || f.IsExhausted) heavy = 0f;
        if (f.Stamina - _c.StamCostAttackLight < reserveAbs || f.IsExhausted) feint = 0f;
        if (d.NoAttack > 0.5f) { light = 0f; heavy = 0f; feint = 0f; }

        // cramped(적 품속)에 끌려든 장거리 무기는 처벌 부스트를 깎아 거리 회복(후퇴/선회)이 이기게 한다.
        // 이러면 창은 1.1m에서 찌르지 않고 자기 스윗스팟(1.5m+)으로 물러난 뒤, 거기서 reachAdvantage 처벌.
        // 단 인내심이 바닥나면(조바심) 페널티를 완화 — 공격 결단이 거리 회복을 이긴다.
        // 충동은 카이터(사거리 우위)엔 상시 — 근접 무기는 평소 제외(검 거울 baseline 보존)하되,
        // 안 A: '쌍방 장기 무교전'(교착)에서는 근접에도 개방 — 수비형 짝(카운터/방어)의 180초 동결 해소.
        // stall = 마지막 클린히트 이후 경과(min NoHitTimer). 정상 근접전은 유예를 못 넘겨 게이트 0 → 대조군 불변.
        float stall = MathF.Min(_f[0].NoHitTimer, _f[1].NoHitTimer);
        float meleeGate = f.Weapon.Range >= _c.MinLongRange ? 1f
            : Math.Clamp((stall - _c.StalemateGraceSec) / _c.StalemateRampSec, 0f, 1f);
        float impulse = (1f - f.Patience / _c.PatienceMax) * meleeGate;  // 0(인내)~1(소진)
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
        // [접촉 핀 해소 실험] 서로 사거리 이내(상호 타격 가능) + 내 선호거리가 상대보다 큰 쪽(=거리를 더 원하는 쪽)만
        // 데드밴드·감쇠 무시하고 후퇴. 둘 다 빠지지 않고 카이터(높은 pref)만 빠져 → 상대(공격형)는 추격, 거리 분화.
        bool bothInRange = dist <= f.EffRange && dist <= oppRt.EffRange;
        bool iWantMoreSpace = d.PreferredDistance > oppRt.Dir.PreferredDistance && gap < -0.05f;
        if (bothInRange && iWantMoreSpace && impulse < 0.5f) score[(int)ActionRequest.Retreat] = MathF.Max(score[(int)ActionRequest.Retreat], 0.7f);
        // [스태미나 자멸 브레이크] 카이팅 세금을 무는 장사거리 무기가 스태미나 열세로 스스로 가스아웃 중이면,
        // 무한 후퇴/선회(−1.5/s) 대신 Hold(제자리 Idle 회복 +6, 세금 0)로 숨을 고른다 — 대검·창 카운터의
        // '버티다 반격' 정체성 복원. 매트릭스 안전: 낮은 스태미나(<KiteBrakeStamFrac)에서만 발동, 세금 값·게이트 불변.
        if (_c.KiteBrakeStamFrac > 0f && f.Weapon.Range >= _c.KiteCostMinRange)
        {
            float stamFrac = f.Stamina / MathF.Max(1f, f.StaminaMax);
            // 리치 감쇠: 리치가 길수록(창·채찍) 'Hold 회복'이 리치 지배를 되살리므로 브레이크를 약화 → 카이팅 세금 보존.
            float reachF = Math.Clamp(1f - (f.Weapon.Range - _c.KiteCostMinRange) / _c.KiteBrakeReachSpan, 0f, 1f);
            if (stamFrac < _c.KiteBrakeStamFrac && reachF > 0f)
            {
                float brake = stamFrac / _c.KiteBrakeStamFrac;          // 0(바닥)~1(문턱)
                float dampFull = 0.25f + 0.75f * brake;                 // 리치F=1일 때 문턱=무영향, 바닥≈0.25배
                float damp = 1f - (1f - dampFull) * reachF;             // 리치F=0 → damp=1(무영향)
                score[(int)ActionRequest.Retreat] *= damp;
                score[(int)ActionRequest.Strafe] *= damp;
                score[(int)ActionRequest.Hold] = MathF.Max(score[(int)ActionRequest.Hold], 0.5f * (1f - brake) * reachF);
            }
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
        for (int i = 1; i < 10; i++)
            score[i] *= 1f + _rng.Range(-_c.UtilityNoise, _c.UtilityNoise);

        // 최고점 + Commit 게이트 (공격은 확신도 요구치 이상일 때만)
        int best = 1;
        for (int i = 2; i < 10; i++) if (score[i] > score[best]) best = i;

        var bestAction = (ActionRequest)best;
        bool isAttack = bestAction is ActionRequest.AttackLight or ActionRequest.AttackHeavy or ActionRequest.Feint;
        if (isAttack && score[best] < f.Dir.CommitThreshold * _c.AttackGateScale)
        {
            // 확신도 미달 → 차순위 비공격 행동 (신중함이 공격을 아끼는 메커니즘)
            int alt = (int)ActionRequest.Approach;
            for (int i = 1; i < 10; i++)
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

            case ActionRequest.Hold:   // 안2: 중립 대기 — 발 멈추고 회복·관망(빙빙 도는 무의미한 선회 대체)
                f.CurrentAction = action;
                f.Vel = default;                                        // 관성 0 (제자리 정지)
                if (f.State != FighterState.Idle) ChangeState(f, FighterState.Idle);
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
        // 배짱([7]§5 대담): 강공을 휘두른 뒤 후딜 단축 / 관중몰이: 강공 마무리 스택
        if (kind == MotionKind.Heavy && !isFeint)
        {
            if (f.Passive is { Trigger: PassiveTrigger.AfterHeavySwing } pnv && _now >= f.PassiveReadyAt)
                ProcPassive(f, pnv, pnv.Duration);
            CrowdStackGain(f);
        }
        f.WindupTotalSec = CombatMath.MotionTime(f.Motion.WindupBaseSec, f.Weapon, f.Def.Stats, _c)
            / (SkillNow(f) is { AttackSpeedMult: > 1f } cwa ? cwa.AttackSpeedMult : 1f)    // 연격([7]): 공속 +35%
            / (PassiveNow(f) is { AtkSpeedMult: > 1f } pwa ? pwa.AtkSpeedMult : 1f); // 최후의 발악·쇼타임 등
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
                if (_now < f.SkillRootedUntil) break;   // 휘감기([7]): 이동봉쇄 — 이동만 묶인다(행동은 가능)
                float speed = f.MoveSpeed * (f.IsExhausted ? _c.ExhaustMoveSpeedMult : 1f) * (1f + CrowdMoveBuff * f.CrowdMomentum)
                            * (_now < f.DashSpeedBuffUntil ? 1.25f : 1f)    // 초상비: 대시 직후 이속↑
                            * (PassiveNow(f) is { } pmv ? pmv.MoveMult : 1f);   // 성격 패시브([7]§5)
                // 추격 방향은 '마지막으로 인지한' 위치를 따른다(인간 풋워크 랙) — 실시간 호밍 금지.
                Vec2 toOpp = PerceivedMovePos(f) - f.Pos;
                float distO = toOpp.Length;
                Vec2 radial = distO > 1e-4f ? toOpp * (1f / distO) : new Vec2(f.Index == 0 ? 1f : -1f, 0f);
                Vec2 tangent = radial.Perp() * f.CircleSign;   // 교전선 수직(선회)

                // arrive(안1): 목표 교전거리(SelectAction과 동일 산식)에 가까울수록 방사속도를 0으로 감속.
                // gap>0=더 다가갈 여지 / gap<0=물러설 여지. 밴드 안에서 선형 램프 → 오버슈트·튕김(거리 댄스) 제거.
                ref readonly Directive d = ref f.Dir;
                float engage = d.NoAttack > 0.5f ? d.PreferredDistance
                             : MathF.Min(d.PreferredDistance, f.EffRange * f.Weapon.EngageRangeRatio);
                float arrive = Math.Clamp((distO - engage) / _c.SteerArriveBand, -1f, 1f);

                // 라벨(결정층)이 방사 의도를 정한다. orbit(접선)은 Strafe(선회)에만 — 접근/후퇴에 상시 섞으면
                // 두 선수가 중심을 공전만 하다 교전이 급감한다(KO 0%·거울 비대칭으로 검출). 접근/후퇴는 직선
                // 의도를 유지하되 arrive 감속 + 속도 관성이 급반전(거리 댄스)만 매끄러운 위빙으로 바꾼다.
                Vec2 desiredDir = f.CurrentAction switch
                {
                    // 접근: 멀면 방사로 좁히고(arrive>0), 교전거리 마지막 한 뼘에서 감속 → 오버슈트·튕김 제거
                    ActionRequest.Approach => radial * MathF.Max(0f, arrive),
                    // 후퇴: 너무 가까울 때만(arrive<0) 방사 후진, 교전거리 닿으면 감속 정지(카이터 벽까지 안 도망)
                    ActionRequest.Retreat => radial * MathF.Min(0f, arrive),
                    // 선회: 거리 유지하며 접선 주, 거리 오차는 약하게 보정 (옛 Strafe 동작 + 관성 스무딩)
                    _ => tangent + radial * (arrive * 0.5f),
                };

                // 벽-접선 탈출(M4-b 재설계): 경계에 몰린 채 거리를 벌리려 할 때(후퇴/선회), 반경(벽)으로 미는
                // 대신 경계 접선을 따라 상대 반대쪽으로 미끄러진다. 두 disc가 벽에 나란히 박히면 방사 후진이
                // ≈반경이 돼 클램프로 잘려 동결되는 핀 버그(74s 정지)를 푼다. 접선은 경계를 안 벗어나 안 잘림.
                float rN = f.Pos.Length / _c.ArenaRadius;
                bool wantsSpaceNearWall = rN > 0.5f && f.CurrentAction is ActionRequest.Retreat or ActionRequest.Strafe;
                if (wantsSpaceNearWall)
                {
                    Vec2 wallTan = f.Pos.Normalized().Perp();
                    if (radial.X * wallTan.X + radial.Y * wallTan.Y > 0f) wallTan = wallTan * -1f; // 상대 반대쪽 호
                    desiredDir = wallTan;
                }

                // desired velocity = 방향 × 속도(상한 speed). 속도 관성으로 가속제한 수렴 → 방향 순간이동 금지(무게감).
                Vec2 desiredVel = desiredDir * speed;
                if (desiredVel.Length > speed) desiredVel = desiredVel.Normalized() * speed;
                Vec2 dv = desiredVel - f.Vel;
                float maxDv = _c.SteerMaxAccel * Dt;
                if (dv.Length > maxDv) dv = dv.Normalized() * maxDv;
                f.Vel += dv;
                f.Pos = ClampToArena(f.Pos + f.Vel * Dt);
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
                    * (atk.LastSwingGuarded ? _c.GuardedRecoveryMult : 1f)
                    / (SkillNow(atk) is { AttackSpeedMult: > 1f } cra ? cra.AttackSpeedMult : 1f)    // 연격([7]): 공속 +35%
                    * (PassiveNow(atk) is { } pre ? pre.RecoveryMult : 1f)                           // 배짱([7]§5): 후딜 −25%
                    / (PassiveNow(atk) is { AtkSpeedMult: > 1f } pra ? pra.AtkSpeedMult : 1f);
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
        // 생존 본능([7]§5 겁쟁이): 창이 열려 있으면 무적 연장 + 성공 시 스태미나 환급
        float iFrame = _c.DodgeIFrameSec + (_now < def.PassiveBuffUntil ? def.DodgeIFrameBonus : 0f);
        if (ds.State == FighterState.Dodge && ds.StateElapsed <= iFrame)
        {
            def.Dodges++;   // 기록실 계측: 회피 성공 (흐름 무영향)
            if (_now < def.PassiveBuffUntil && def.DodgeRefundPct > 0f)
                def.Stamina = MathF.Min(def.StaminaMax, def.Stamina + _c.StamCostDodge * def.DodgeRefundPct);
            RegisterWhiff(atk);
            return;
        }
        // 다운 추가타 1회 제한 (무한 루프 방지)
        if (ds.State is FighterState.Down && ds.DownHitConsumed) { RegisterWhiff(atk); return; }

        // 방패 막기([7]§4 방패 Ⅰ): 완전 차단 창 — 피해·게이지 칩 전무, 공격자는 막힌 후딜(프레임 불리),
        // 차단 직후 반격 보너스 창 개시. 도끼 분쇄·망치 관통도 여기선 무효(완전가드 우선 — [7]§2).
        if (_now < def.SkillFullBlockUntil)
        {
            def.Blocks++;
            atk.LastSwingGuarded = true;
            def.SkillCounterBoostUntil = _now + (def.ActiveSkill?.CounterBoostSec ?? 0f);
            return;
        }

        float motionMult = atk.MotionKindNow == MotionKind.Heavy ? _c.MotionMultHeavy : _c.MotionMultLight;
        bool inner = atk.Weapon.Range >= _c.MinLongRange && dist < atk.Weapon.Range * _c.InnerRangeRatio;

        // 2) 가드 판정
        if (ds.State == FighterState.Guard)
        {
            def.Blocks++;   // 기록실 계측: 방어(가드·패링) 성공 (흐름 무영향)
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

            // 분쇄 일격([7]§4 도끼 Ⅰ): 버프 보유 중 강공이 가드에 닿으면 무조건 가드파괴 + 출혈. 버프 1회 소모.
            // 예외([7]): 방패 막기 완전가드는 관통 무시 — 완전가드는 미탑재라 현재는 패링(위에서 이미 무효)이 그 역할.
            bool sunder = SkillNow(atk) is { SunderNextHeavy: true } && atk.MotionKindNow == MotionKind.Heavy;
            if (sunder)
            {
                atk.SkillBuffUntil = -1f;      // 1회 소모
                ApplyBleed(atk, def);          // 출혈 라이더 — 기존 스택 모델로 매핑(도끼 BleedDps 기준)
            }

            if (gr.IsGuardBreak || sunder)
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
        if (_now < atk.SkillNextLightCritUntil && atk.MotionKindNow == MotionKind.Light)
        { isCrit = true; atk.SkillNextLightCritUntil = 0f; }   // 그림자 보([7]): 다음 약공 확정 크리(1회)
        if (PassiveNow(atk) is { } pcr)                        // 기회의 일격(확정 크리)·황제의 위압(크리율 +15%)
        {
            if (pcr.ForceCrit) { isCrit = true; atk.PassiveBuffUntil = _now; }
            else if (pcr.CritAdd > 0f && !isCrit && _rng.Roll(pcr.CritAdd)) isCrit = true;
        }
        var hitCtx = new CombatMath.HitContext(isCrit, false, isCounter, inner, 1f + CrowdDmgBuff * atk.CrowdMomentum,
            _rng.Range(_c.VarianceMin, _c.VarianceMax), ds.IsExhausted);
        float damage = CombatMath.FinalDamage(atk.Weapon, motionMult, atk.Def.Stats, def.Def.Stats, hitCtx, _c);
        // 신규 전투 특성(#16) — 조건부 데미지 배율. 결정론(_rng), 특성 보유자 한정이라 매트릭스(무특성 baseline) 무영향.
        if (atk.Has(TraitTable.Executioner) && def.HpPct <= 0.30f) damage *= 1.5f;   // 처형자: 마무리
        if (atk.Has(TraitTable.Berserk) && atk.HpPct <= 0.35f) damage *= 1.3f;       // 광폭화: 궁지의 폭발
        if (atk.Has(TraitTable.Assassin) && _rng.Roll(0.04f)) { damage *= 2.4f; Emit(new Decision(_now, atk.Index, "ASSASSINATE", "Trait", 1.5f)); }  // 일격필살

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
        // 불퇴의 자세([7]§4 대검 Ⅱ): 포이즈 무한 — 스태거/넉백 면역(피해·히트스턴·가드붕괴·다운은 정상).
        // (강공 차지 −30% 라이더는 모션 시간 계층이라 애니메이션 트랙에서.)
        if (SkillNow(def) is { PoiseImmune: true })
        {
            if (def.State is not (FighterState.Stagger or FighterState.Down or FighterState.GetUp))
                ChangeState(def, FighterState.HitStun, CombatMath.ApplyPoiseDamage(def.Poise, def.PoiseMax, atk.Weapon, motionMult, ds.IsExhausted, _c).StunSec);
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

    /// <summary>액티브 스킬이 발동 중이면 명세를, 아니면 null. 미장착이면 언제나 null(매트릭스 무영향).</summary>
    private ActiveSpec? SkillNow(FighterRuntime f) => f.ActiveSkill is { } sp && _now < f.SkillBuffUntil ? sp : null;

    /// <summary>성격 패시브 효과가 켜져 있으면 명세를, 아니면 null.</summary>
    private PassiveSpec? PassiveNow(FighterRuntime f) => f.Passive is { } ps && _now < f.PassiveBuffUntil ? ps : null;

    /// <summary>투지·관중몰이 스택 배율(만료 시 0).</summary>
    private float StackDmgMult(FighterRuntime f)
    {
        if (f.Passive is not { StackMax: > 0 } ps || f.PassiveStacks <= 0) return 1f;
        if (ps.Trigger == PassiveTrigger.ConsecHitsTaken && _now >= f.PassiveStackExpiry) { f.PassiveStacks = 0; return 1f; }
        return 1f + ps.PerStackDmg * f.PassiveStacks;
    }

    private void ProcPassive(FighterRuntime f, PassiveSpec ps, float dur)
    {
        f.PassiveBuffUntil = _now + MathF.Max(dur, 0.05f);
        if (ps.ProcCdSec > 0f) f.PassiveReadyAt = _now + ps.ProcCdSec;
        if (ps.CounterWindowAdd != 0f)
            f.Overrides.Add(new ActiveOverride
            {
                Mods = new[] { ParamMod.Add(TParam.CounterWindow, ps.CounterWindowAdd) },
                ExpiresAt = _now + MathF.Max(dur, 0.05f), ReasonTag = ps.ReasonTag,
            });
        if (ps.ProcCdSec >= 3f)   // 잦은 스택형(관중몰이 2s)은 제외 — 라벨 도배 방지
            Emit(new Decision(_now, f.Index, "PASV_" + ps.ReasonTag, "Passive", MathF.Max(2f, dur)));
    }

    /// <summary>[7]§5 성격 패시브 — 판단 틱마다 조건 평가(상시조건형은 매 틱 갱신, 이산 proc은 쿨다운).</summary>
    private void TickPassives(FighterRuntime f, FighterRuntime opp)
    {
        if (f.Passive is not { } ps) return;
        bool ready = _now >= f.PassiveReadyAt;
        switch (ps.Trigger)
        {
            case PassiveTrigger.Periodic:                                     // 전장 분석
                if (ready) ProcPassive(f, ps, ps.Duration);
                break;
            case PassiveTrigger.ConsecHitsTaken:                              // 투지(스택)
                if (ready && f.ConsecHitsTaken >= (int)ps.Threshold)
                {
                    f.PassiveStacks = Math.Min(ps.StackMax, f.PassiveStacks + 1);
                    f.PassiveStackExpiry = _now + ps.Duration;
                    ProcPassive(f, ps, ps.Duration);
                }
                break;
            case PassiveTrigger.SelfHpBelow:                                  // 최후의 발악(상시조건)
                if (f.HpPct <= ps.Threshold) f.PassiveBuffUntil = _now + 0.25f;
                break;
            case PassiveTrigger.SelfHpAboveWinning:                           // 여유
                if (f.HpPct >= ps.Threshold && f.HpPct > opp.HpPct) f.PassiveBuffUntil = _now + 0.25f;
                break;
            case PassiveTrigger.HpDeficit:                                    // 기사도의 보답
                if (opp.HpPct - f.HpPct >= ps.Threshold) f.PassiveBuffUntil = _now + 0.25f;
                break;
            case PassiveTrigger.TimeLowAndLosing:                             // 역전의 영웅
                if (_c.MatchTimeSec > 0f
                    && (_c.MatchTimeSec - _now) / _c.MatchTimeSec <= ps.Threshold
                    && f.HpPct < opp.HpPct) f.PassiveBuffUntil = _now + 0.25f;
                break;
            case PassiveTrigger.OppHeavyWindup:                               // 생존 본능
                if (ready && !f.IsExhausted && f.Stamina >= ps.StCost
                    && opp.State == FighterState.Windup && opp.MotionKindNow == MotionKind.Heavy)
                {
                    f.Stamina = MathF.Max(0f, f.Stamina - ps.StCost);
                    f.DodgeIFrameBonus = ps.DodgeIFrameAdd; f.DodgeRefundPct = ps.DodgeRefundPct;
                    ProcPassive(f, ps, ps.Duration);
                }
                break;
            case PassiveTrigger.OppRecovery:                                  // 기회의 일격
                if (ready && opp.State == FighterState.Recovery) ProcPassive(f, ps, ps.Duration);
                break;
            case PassiveTrigger.OppHpBelow:                                   // 어부지리(처형 대시 — 고결은 거부, [7]§8)
                if (f.Def.PersonalityId == "PER_HONORABLE") break;
                if (ready && !f.IsExhausted && f.Stamina >= ps.StCost && opp.HpPct <= ps.Threshold
                    && f.State is FighterState.Idle or FighterState.Move && _rng.Roll(ps.Prob))
                {
                    f.Stamina = MathF.Max(0f, f.Stamina - ps.StCost);
                    f.PassiveReadyAt = _now + ps.ProcCdSec;
                    Emit(new Decision(_now, f.Index, "PASV_" + ps.ReasonTag, "Passive", 2f));
                    DoSkillStrike(f, opp, new ActiveSpec(ps.ReasonTag, SkillTrigger.InRange, 0f, 1f, 0f, 0f,
                        ActiveKind.Strike, DashIn: true, StrikeHeavy: true));
                }
                break;
            case PassiveTrigger.CrowdStackFull:                               // 쇼타임(군중 5스택 소모)
                if (ready && f.PassiveStacks >= 5) { f.PassiveStacks = 0; ProcPassive(f, ps, ps.Duration); }
                break;
        }
    }

    /// <summary>관중몰이 스택 적립([7]§5 쇼맨) — 크리·강공 마무리·도발에서 호출.</summary>
    private void CrowdStackGain(FighterRuntime f)
    {
        if (f.Passive is not { Trigger: PassiveTrigger.OnCritOrHeavyOrTaunt } ps) return;
        if (_now < f.PassiveReadyAt) return;
        f.PassiveStacks = Math.Min(ps.StackMax, f.PassiveStacks + 1);
        f.PassiveReadyAt = _now + ps.ProcCdSec;
    }

    /// <summary>
    /// [7]§1 공통 발동 파이프라인 — 판단 틱마다 SelectAction보다 먼저 평가, 발동 시 그 틱 일반행동 생략.
    /// 트리: ①쿨타임 ②캔슬 가능 상태(Idle/Move/Guard) ③코스트(지침 중 ST 불가·자기치사 방지 [7]§3)
    ///       ④거부권(1차 탑재분엔 처형류 없음 — 심판의 일격 등 탑재 시 검사) ⑤조건 게이트 ⑥전술 타당성 ⑦확률 롤.
    /// 발동은 장착 시에만 평가·롤 — 미장착 세계의 RNG 수열 불변(매트릭스 안전).
    /// </summary>
    private bool TrySkillActivate(FighterRuntime f, FighterRuntime opp)
    {
        if (f.ActiveSkill is not { } sp) return false;
        if (f.SkillExecStrikeAt > 0f) return true;   // 심판의 일격 차지 중 = 무방비 정지(그 틱 행동 소비)
        if (_now < f.SkillReadyAt || _now < f.SkillBuffUntil) return false;                       // ①
        if (f.State is not (FighterState.Idle or FighterState.Move or FighterState.Guard)) return false; // ②
        if (sp.StCost > 0f && (f.IsExhausted || f.Stamina < sp.StCost)) return false;             // ③ 지침 중 ST 불가
        if (sp.GgCost > 0f && f.GuardGauge < sp.GgCost) return false;
        // ④ 거부권([7]§8) — 고결 성격, 또는 '정정당당' 패시브 보유자는 처형류를 쓰지 않는다
        if (sp.VetoExecution && (f.Def.PersonalityId == "PER_HONORABLE"
                                 || f.Passive is { VetoExecution: true })) return false;
        float gap = (opp.Pos - f.Pos).Length;
        bool oppHeavyWindup = opp.State == FighterState.Windup && opp.MotionKindNow == MotionKind.Heavy;
        bool cond = sp.Trigger switch                                                             // ⑤+⑥(타당성 겸)
        {
            SkillTrigger.SelfHpBelow     => f.HpPct <= sp.Threshold && gap <= f.EffRange + 2.0f,  // 교전 가능 거리([7] 광전사 타당성)
            SkillTrigger.EvenFight       => MathF.Abs(f.HpPct - opp.HpPct) <= sp.Threshold && gap <= f.EffRange * 1.3f, // 호각·근접 지속
            SkillTrigger.ConsecHitsTaken => f.ConsecHitsTaken >= (int)sp.Threshold,
            // 상대 가드 중([7] 분쇄): 상대의 가드는 대개 내 공격 중이라 내 판단 틱과 안 겹친다 —
            // '직전 스윙이 막힘'(LastSwingGuarded)을 가드 유지의 근거로 함께 본다(문서 타당성: 가드/근접 유지 중).
            SkillTrigger.OppGuarding     => (opp.State == FighterState.Guard || f.LastSwingGuarded) && gap <= f.EffRange + 0.4f,
            // 연격([7]): 게이지<임계 — 단 게이지 회복이 빨라 틱 시점엔 만충이기 일쑤 → '방금 막힘'(=게이지를 지금 깎는 중)을 등가 신호로 병행
            SkillTrigger.OppGuardGaugeBelow => gap <= f.EffRange + 0.4f
                                            && (opp.GuardGauge < opp.GuardGaugeMax * sp.Threshold || f.LastSwingGuarded),
            SkillTrigger.GapBand         => gap >= sp.GapMinM && gap <= sp.GapMaxM,
            SkillTrigger.OppHeavyWindupOrPress => oppHeavyWindup || gap < sp.Threshold,           // 강공 선딜 인지 or 근접 압박
            SkillTrigger.OppHeavyWindupOrRecovery => oppHeavyWindup || opp.State == FighterState.Recovery,
            // 난무([7]): 경직/가드붕괴/스태거 확정창 — 경직 순간은 내 후딜과 겹쳐 틱에 안 걸리므로 진입 후 0.5s 잔향을 인정
            SkillTrigger.OppVulnerable   => gap <= f.EffRange + 0.4f
                                            && (opp.State is FighterState.HitStun or FighterState.Stagger
                                                || _now - opp.LastStunAt <= 0.5f || opp.GuardDisabled),
            SkillTrigger.InRange         => gap <= f.EffRange + 0.2f,
            SkillTrigger.OppExecutable   => (opp.HpPct <= sp.Threshold || opp.State is FighterState.Down or FighterState.Stagger)
                                            && gap <= f.EffRange + 1.0f,
            SkillTrigger.OppWindupAny    => opp.State == FighterState.Windup && gap <= opp.EffRange + 0.6f,   // 반응형([7] 최우선)
            SkillTrigger.OppGuardingOrStunned => (opp.State is FighterState.Guard or FighterState.HitStun or FighterState.Stagger || f.LastSwingGuarded)
                                            && gap <= f.EffRange + 1.5f,
            _ => false,
        };
        // 조건 우회([7]§1-5 완화): 쿨이 끝나고도 트리거가 안 열리면 오의는 영영 안 나온다.
        // 준비 후 SkillBypassSec이 지나면 조건을 접고 사거리 타당성만 보고 쓴다 — "쿨 돌면 웬만하면 쓴다".
        // 단, 조건이 곧 그 스킬의 정체성인 것은 우회하지 않는다:
        //   처형(빈사) · 확정타(상대 취약) · 간격 돌파(DashIn).
        //   특히 DashIn은 거리 조건을 지우면 돌진이 상시 보장돼 카이팅 상성이 무너진다(실측 대검 0%→100%).
        bool identityTrigger = sp.Trigger is SkillTrigger.OppExecutable or SkillTrigger.OppVulnerable || sp.DashIn;
        bool bypass = !identityTrigger
                   && _now - f.SkillReadyAt >= _c.SkillBypassSec
                   && gap <= f.EffRange + _c.SkillBypassRangeSlackM;
        if (!cond && !bypass) return false;
        // ⑦ 확률 롤 — 인내심 낮을수록 공격 충동↑([7]§1-7 patienceMod 준용)
        float patienceMod = 1f + (1f - f.Patience / _c.PatienceMax) * 0.5f;
        float prob = sp.Prob * patienceMod;
        // 기회 활용 보정: 쿨이 돈 채로 오래 놀고 있었다면 확률을 상한까지 끌어올린다.
        // 조건이 열리는 순간이 드문 스킬(가드 중·빈사 등)은 그 한 번을 확률 롤로 흘려보내면
        // 사실상 없는 스킬이 된다 — 준비 시간이 길수록 "웬만하면 쓴다"로 수렴시킨다.
        // SkillReadyAt은 발동 시 now+Duration+Cooldown으로 갱신되므로 (now − ReadyAt) = 준비 후 경과.
        float readyFor = _now - f.SkillReadyAt;
        if (readyFor > 0f && _c.SkillPityRampSec > 0f)
        {
            float t = MathF.Min(1f, readyFor / _c.SkillPityRampSec);
            prob += (_c.SkillPityCap - prob) * t;
        }
        if (!_rng.Roll(MathF.Min(_c.SkillPityCap, prob))) return false;
        // ── 발동: 코스트 차감 + 효과 적용 + 쿨타임 + 가시화([7]§3 — 조용한 발동 금지) ──
        if (sp.StCost > 0f) f.Stamina = MathF.Max(0f, f.Stamina - sp.StCost);
        if (sp.SelfHpPctCost > 0f) f.Hp = MathF.Max(1f, f.Hp - f.HpMax * sp.SelfHpPctCost);       // HP%라 자기치사 없음
        if (sp.GgCost > 0f) f.GuardGauge = MathF.Max(0f, f.GuardGauge - sp.GgCost);
        f.SkillReadyAt = _now + sp.Duration + sp.CooldownSec;
        Emit(new Decision(_now, f.Index, "SKILL_" + sp.ReasonTag, "Skill", MathF.Max(2f, sp.Duration)));
        // 함정 간파([7]§5 신중): 상대가 오의를 꺼낸 직후 1초 — 카운터 창 +0.4·피해 +25%
        if (opp.Passive is { Trigger: PassiveTrigger.OppSkillActivated } pfs && _now >= opp.PassiveReadyAt)
            ProcPassive(opp, pfs, pfs.Duration);
        switch (sp.Kind)
        {
            case ActiveKind.Buff:
                f.SkillBuffUntil = _now + sp.Duration;
                f.SkillAtkBonus = sp.AtkPerMissingHpPct > 0f
                    ? MathF.Min(sp.AtkCap, sp.AtkPerMissingHpPct * (1f - f.HpPct) * 100f) : 0f;   // 광전사: 발동 시점 스냅샷
                if (sp.AutoPokeMult > 0f) f.SkillNextPokeAt = _now;                               // 공간 지배: 즉시 1타 가능
                if (sp.CounterWindowAdd != 0f)                                                    // 결투의 격: Override 파이프([7]§2 가산·만료 롤백)
                    f.Overrides.Add(new ActiveOverride
                    {
                        Mods = new[] { ParamMod.Add(TParam.CounterWindow, sp.CounterWindowAdd) },
                        ExpiresAt = _now + sp.Duration, ReasonTag = sp.ReasonTag,
                    });
                break;
            case ActiveKind.Stance:
                f.SkillBuffUntil = _now + sp.Duration;
                if (sp.FullBlock) f.SkillFullBlockUntil = _now + sp.Duration;                     // 방패 막기
                if (sp.AutoCounter) f.SkillAutoCounterUntil = _now + sp.Duration;                 // 철벽 반격
                break;
            case ActiveKind.Strike:
                DoSkillStrike(f, opp, sp);                                                        // 즉발(모션 없는 1차 구현)
                break;
            case ActiveKind.Charge:                                                               // 심판의 일격: 무방비 차지
                f.SkillExecStrikeAt = _now + sp.ChargeSec;
                f.SkillBuffUntil = _now + sp.ChargeSec;
                if (f.State == FighterState.Move) ChangeState(f, FighterState.Idle);              // 정지(무방비)
                break;
        }
        return true;
    }

    /// <summary>[7]§4 즉발 타격류(모션 없는 1차 구현) — 돌진/배후 이동은 위치 세팅, 타격은 ApplyDamage 직행.</summary>
    private void DoSkillStrike(FighterRuntime f, FighterRuntime opp, ActiveSpec sp)
    {
        float maxR = _c.ArenaRadius - 0.5f;
        if (sp.DashIn)   // 쇄도 베기·방패 밀치기: 상대 앞까지 짓쳐든다(디스크 반경 존중·아레나 클램프)
        {
            Vec2 to = opp.Pos - f.Pos; float d = to.Length;
            float stop = MathF.Max(0.9f, f.EffRange * 0.55f);
            if (d > stop)
            {
                Vec2 np = opp.Pos - to * (stop / MathF.Max(1e-4f, d));
                if (np.Length > maxR) np *= maxR / np.Length;
                f.Pos = np;
            }
        }
        if (sp.TeleportBehind)   // 그림자 보: 상대 배후로 — 벽이면 클램프(측면 이탈 효과). 이탈 자체가 회피를 겸한다.
        {
            Vec2 dir = f.Pos - opp.Pos; float d = dir.Length;
            dir = d > 1e-4f ? dir * (1f / d) : new Vec2(1f, 0f);
            Vec2 np = opp.Pos - dir * 1.8f;
            if (np.Length > maxR) np *= maxR / np.Length;
            f.Pos = np;
            if (sp.NextLightCritSec > 0f) f.SkillNextLightCritUntil = _now + sp.NextLightCritSec;
            return;   // 타격 없음 — 다음 약공이 본체
        }
        float gap = Vec2.Dist(f.Pos, opp.Pos);
        if (gap > f.EffRange + 0.75f) return;   // 빗나감([7]§3 — 후딜 처벌 연출은 모션 트랙에서)
        bool immune = HardCcImmune(opp);
        bool guarding = opp.State == FighterState.Guard;
        float mm = (sp.StrikeHeavy ? _c.MotionMultHeavy : _c.MotionMultLight) * sp.StrikeDmgMult;
        for (int h = 0; h < sp.StrikeHits; h++)
        {
            bool asGuarded = guarding && sp.GuardPierce <= 0f && !sp.BashBreak;   // 관통·배쉬는 가드 감쇠 없이
            var ctx = new CombatMath.HitContext(false, asGuarded, false, false,
                1f + CrowdDmgBuff * f.CrowdMomentum, _rng.Range(_c.VarianceMin, _c.VarianceMax), opp.IsExhausted);
            float dmg = CombatMath.FinalDamage(f.Weapon, mm, f.Def.Stats, opp.Def.Stats, ctx, _c);
            if (guarding && sp.GuardPierce > 0f) dmg *= sp.GuardPierce;           // 대지 강타: 가드관통 50%
            ApplyDamage(f, opp, dmg, false, false, asGuarded);
            if (opp.Hp <= 0f) return;
        }
        if (sp.KnockbackM > 0f && !immune)   // 견제 찌르기: 넉백(하이퍼아머·불퇴면 무효, 피해는 이미 적용)
        {
            Vec2 dir = opp.Pos - f.Pos; float d = dir.Length;
            dir = d > 1e-4f ? dir * (1f / d) : new Vec2(1f, 0f);
            Vec2 np = opp.Pos + dir * sp.KnockbackM;
            if (np.Length > maxR) np *= maxR / np.Length;
            opp.Pos = np;
        }
        if (sp.PullM > 0f || sp.RootSec > 0f)   // 휘감기: 멀면 끌어당김 / 가까우면 이동봉쇄(택1, [7]§4)
        {
            if (!immune)
            {
                if (gap > (sp.GapMinM + sp.GapMaxM) * 0.5f && sp.PullM > 0f)
                {
                    Vec2 dir = f.Pos - opp.Pos; float d = dir.Length;
                    dir = d > 1e-4f ? dir * (1f / d) : new Vec2(1f, 0f);
                    opp.Pos += dir * sp.PullM;
                }
                else if (sp.RootSec > 0f) opp.SkillRootedUntil = _now + sp.RootSec;
            }
        }
        if (sp.StaggerOnHitSec > 0f && !immune && opp.State is not (FighterState.Stagger or FighterState.Down))
        {
            Emit(new PoiseBroken(_now, opp.Index));
            CrowdFill(f, 5f);
            ChangeState(opp, FighterState.Stagger, sp.StaggerOnHitSec);            // 대지 강타: 명중 시 스태거
        }
        if (sp.BashBreak && guarding)   // 방패 밀치기: 가드붕괴 + 다운(하이퍼아머·불퇴면 붕괴만 — [7]§2)
        {
            opp.GuardDisabled = true;
            Emit(new GuardBroken(_now, opp.Index));
            CrowdFill(f, 8f);
            if (!immune && sp.DownSec > 0f)
            {
                f.Knockdowns++;
                Emit(new KnockedDown(_now, opp.Index));
                ChangeState(opp, FighterState.Down, sp.DownSec);
            }
        }
    }

    /// <summary>경직류 CC 면역([7]§2): 하이퍼아머(중량 강공 커밋 중)·불퇴의 자세·좀비(저HP).</summary>
    private bool HardCcImmune(FighterRuntime f) =>
        DebuffImmune(f) || SkillNow(f) is { PoiseImmune: true }
        || (f.Weapon.HyperArmor && f.State is FighterState.Windup or FighterState.Active && f.MotionKindNow == MotionKind.Heavy);

    /// <summary>심판의 일격 해결 — 1.2s 차지 후 타격(차지 중 피격 시 ApplyDamage에서 취소·CD 50%).</summary>
    private void ResolveExecute(FighterRuntime f, FighterRuntime opp, ActiveSpec sp)
    {
        float gap = Vec2.Dist(f.Pos, opp.Pos);
        if (gap > f.EffRange + 0.9f) return;   // 헛침 — 쿨타임은 정상 적용([7]§3 대처벌)
        var ctx = new CombatMath.HitContext(false, opp.State == FighterState.Guard, false, false,
            1f + CrowdDmgBuff * f.CrowdMomentum, _rng.Range(_c.VarianceMin, _c.VarianceMax), opp.IsExhausted);
        float dmg = CombatMath.FinalDamage(f.Weapon, _c.MotionMultHeavy * sp.ExecuteDmgMult, f.Def.Stats, opp.Def.Stats, ctx, _c);
        if (opp.HpPct < sp.ExecuteKillPct) dmg = MathF.Max(dmg, opp.Hp + 1f);      // HP<15% 즉사
        ApplyDamage(f, opp, dmg, true, false, opp.State == FighterState.Guard);
    }

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
        // 무기 액티브([7]§4, 발동 중만): 광전사의 도끼 — 공격력 스냅샷 보너스 / 받피 +25%(설계된 리스크)
        if (SkillNow(atk) is not null && atk.SkillAtkBonus > 0f) dmg *= 1f + atk.SkillAtkBonus;
        if (SkillNow(def) is { } skd) dmg *= skd.DmgTakenMult;
        if (_now < atk.SkillCounterBoostUntil) dmg *= atk.ActiveSkill?.CounterBoostMult ?? 1f;   // 방패 막기: 차단 직후 반격 +30%
        // ── 성격 패시브([7]§5) ──
        dmg *= StackDmgMult(atk);                                                    // 투지·관중몰이 스택
        if (PassiveNow(atk) is { } pa2) { dmg *= pa2.DmgDealtMult; if (crit) dmg *= pa2.CritDmgMult; }
        if (PassiveNow(def) is { } pd2) dmg *= pd2.DmgTakenMult;                     // 최후의 발악: 받피 +25%
        if (atk.Passive is { Trigger: PassiveTrigger.OppVulnerable } pex               // 약점 포착(상시조건)
            && (def.GuardDisabled || def.IsExhausted || def.State == FighterState.Stagger))
            dmg *= pex.DmgDealtMult;
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
        // 피의 갈증([7]§5 잔혹): 출혈 중이거나 빈사인 상대를 가격하면 흡혈
        if (atk.Passive is { Trigger: PassiveTrigger.OnDealHit } pbl && _now >= atk.PassiveReadyAt
            && (def.BleedStacks > 0 || def.HpPct <= pbl.LifestealOppHpBelow))
        {
            atk.PassiveReadyAt = _now + pbl.ProcCdSec;
            atk.Hp = MathF.Min(atk.HpMax, atk.Hp + dmg * pbl.LifestealPct);
        }
        // 공포 군림([7]§5 잔혹): 상대 HP가 임계 단위로 깎일 때마다 공포 1단(공격성↓, 최대 3단)
        if (atk.Passive is { Trigger: PassiveTrigger.OppHpStep } pt
            && def.FearStacks < pt.FearStackMax && def.HpPct <= def.FearHpMark - pt.Threshold)
        {
            def.FearHpMark = def.HpPct; def.FearStacks++;
            def.Overrides.Add(new ActiveOverride
            {
                Mods = new[] { ParamMod.Add(TParam.Aggression, pt.FearAggPerStack) },
                ExpiresAt = float.MaxValue, ReasonTag = pt.ReasonTag,
            });
            Emit(new Decision(_now, def.Index, "PASV_" + pt.ReasonTag, "Passive", 2.5f));
        }
        if (crit) CrowdStackGain(atk);   // 관중몰이([7]§5 쇼맨): 크리
        // 심판의 일격 차지 취소([7]§3): 차지 중 피격 → 시전 취소·쿨타임 50%만 적용
        if (def.SkillExecStrikeAt > 0f)
        {
            def.SkillExecStrikeAt = -1f; def.SkillBuffUntil = _now;
            if (def.ActiveSkill is { } es) def.SkillReadyAt = _now + es.CooldownSec * 0.5f;
        }
        // 철벽 반격([7]§4 창 Ⅱ): 자세 중 최초 피격 1회 — 즉시 반격(카운터 판정). 선해제로 상호 재귀 없음.
        if (_now < def.SkillAutoCounterUntil && def.Hp > 0f && atk.Hp > 0f)
        {
            def.SkillAutoCounterUntil = 0f;
            var rctx = new CombatMath.HitContext(false, false, true, false, 1f + CrowdDmgBuff * def.CrowdMomentum,
                _rng.Range(_c.VarianceMin, _c.VarianceMax), atk.IsExhausted);
            ApplyDamage(def, atk, CombatMath.FinalDamage(def.Weapon, _c.MotionMultLight, def.Def.Stats, atk.Def.Stats, rctx, _c),
                false, true, false);
        }
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
        if (to is FighterState.HitStun or FighterState.Stagger) f.LastStunAt = _now;   // 난무([7]) 확정창 프록시
        // 침착([7]§5 냉철): 피격 경직 진입 시 확률로 분노·도발 상태를 떨쳐낸다
        if (to == FighterState.HitStun && f.Passive is { Trigger: PassiveTrigger.OnHitStun, ClearDebuffs: true } pc
            && _now >= f.PassiveReadyAt && _rng.Roll(pc.Prob))
        {
            f.PassiveReadyAt = _now + pc.ProcCdSec;
            if (f.Overrides.RemoveAll(o => o.ReasonTag == "RAGED") > 0)
                Emit(new Decision(_now, f.Index, "PASV_" + pc.ReasonTag, "Passive", 2f));
        }
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
        f.Whiffs, f.CornerTime, f.MinHpPct, MathF.Max(0f, f.HpPct), f.EverTaunted, f.Blocks, f.Dodges);

    // ───────────────────────── 관중 (문서[10]) ─────────────────────────
    /// <summary>군중게이지 감쇠 + 기세(유리)/위축(불리) 강도 갱신. 매 틱 — 이번 틱 데미지·이속·directive에 반영.</summary>
    /// <summary>감독 실시간 개입: 예약 시각 도달 시 전술 프로파일 교체 + 지시 재합성. 예약 없으면 완전 무비용.</summary>
    private void ApplyTacticSwitch(FighterRuntime f)
    {
        if (f.Switches == null || f.SwitchIdx >= f.Switches.Length) return;
        var sw = f.Switches[f.SwitchIdx];
        if (_now < sw.Time) return;
        f.SwitchIdx++;
        var np = Array.Find(TacticsTable.All, t => t.Id == sw.TacticId);
        if (np == null) return;   // 무효 id 방어
        f.Profile = np;
        f.RebuildDirective(_now);
        Emit(new Decision(_now, f.Index, "TACTIC_" + sw.TacticId.Replace("TAC_", ""), "Strategy", 2.5f));
    }

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

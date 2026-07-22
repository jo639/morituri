using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Sim.Tests;

/// <summary>M2: 매치 시뮬레이터 검증 — 결정론(원칙 B), 완주, FSM 무결성.</summary>
[TestFixture]
public class MatchSimTests
{
    [Test]
    public void Determinism_SameSeed_IdenticalResult()
    {
        // 원칙 B의 핵심 검증: 같은 (시드, 데이터) → 같은 결과. 리플레이/배치 재현성의 기반.
        var e1 = new List<SimEvent>(); var e2 = new List<SimEvent>();
        var r1 = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 42, e1);
        var r2 = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 42, e2);

        Assert.That(r2.Winner, Is.EqualTo(r1.Winner));
        Assert.That(r2.DurationSec, Is.EqualTo(r1.DurationSec).Within(1e-6));
        Assert.That(r2.ScoreA, Is.EqualTo(r1.ScoreA).Within(1e-4));
        Assert.That(e2.Count, Is.EqualTo(e1.Count));
    }

    [Test]
    public void Determinism_DifferentSeeds_ProduceValidResults()
    {
        for (ulong s = 1; s <= 10; s++)
        {
            var r = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, s);
            Assert.That(r.Winner, Is.InRange(-1, 1));
        }
    }

    [Test]
    public void Smoke_30Matches_AllFinishWithinTimeAndNoNan()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var r = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, seed);
            Assert.That(r.DurationSec, Is.InRange(0.0, 180.1), $"seed {seed}");
            Assert.That(float.IsNaN(r.ScoreA) || float.IsNaN(r.ScoreB), Is.False, $"seed {seed}: NaN 점수");
            Assert.That(r.Winner, Is.InRange(-1, 1));
        }
    }

    [Test]
    public void Events_ContainCoreLifecycle()
    {
        var events = new List<SimEvent>();
        new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 7, events);

        Assert.That(events.OfType<MatchEnded>().Count(), Is.EqualTo(1));
        Assert.That(events.OfType<AttackSwung>().Any(), Is.True, "공격 시도가 있어야 함");
        Assert.That(events.OfType<StateChanged>().Any(), Is.True);
    }

    [Test]
    public void TraitGen_RespectsCountDistributionAndExclusion()
    {
        // 부여 개수 0(15%)/1(50%)/2(25%)/3(8%)/4(2%) + 상반 배타(같은 축 반대극성 공존 금지) — 결정론.
        var rng = new Morituri.Sim.Core.SimRandom(12345);
        var counts = new int[6];
        for (int i = 0; i < 5000; i++)
        {
            var ids = TraitGen.Roll(rng);
            counts[ids.Length]++;
            // 배타 검증: 같은 ExclAxis에서 +/− 동시 보유 없음
            var defs = ids.Select(TraitTable.Get).ToList();
            foreach (var g in defs.Where(t => t.ExclAxis.Length > 0).GroupBy(t => t.ExclAxis))
                Assert.That(g.Select(t => t.ExclPolarity).Distinct().Count() <= 1, Is.True,
                    $"상반 특성 공존: {string.Join(",", ids)}");
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length), "중복 특성");
        }
        Assert.That(counts[0] / 5000.0, Is.EqualTo(0.15).Within(0.03), "0개 부여 ≈15%");
        Assert.That(counts[1] / 5000.0, Is.EqualTo(0.50).Within(0.04), "1개 부여 ≈50%");
        Assert.That(counts[2] / 5000.0, Is.EqualTo(0.25).Within(0.04), "2개 부여 ≈25%");
        Assert.That(counts[3] / 5000.0, Is.EqualTo(0.08).Within(0.03), "3개 부여 ≈8%");
        Assert.That(counts[4] / 5000.0, Is.EqualTo(0.02).Within(0.02), "4개 부여 ≈2%");
    }

    [Test]
    public void Trait_GiantBoostsHpAndRange_FleetReducesHp()
    {
        // 거인: HP·사거리↑(생성 시 파생스탯 반영). 결정론적으로 스탯 변화만 확인(런타임 노출은 프레임으로).
        var giant = new FighterDef("거인", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CALM",
            new[] { "TRT_GIANT" });
        var plain = new FighterDef("기본", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CALM");
        // 거인은 HP·사거리 우위라 동일 빌드(검·압박·냉철) 거울에서 평균 이상 승률이어야 함
        int giantWins = 0, decided = 0;
        for (ulong s = 1; s <= 200; s++)
        {
            var r = new MatchSim().Run(giant, plain, s);
            if (r.Winner == 0) { giantWins++; decided++; } else if (r.Winner == 1) decided++;
        }
        Assert.That((float)giantWins / decided, Is.GreaterThan(0.55f), "거인(HP·사거리↑)이 기본보다 유리해야 함");
    }

    [Test]
    public void Bleed_AxeAppliesAndStacks_NonBleedWeaponDoesNot()
    {
        // 출혈(문서[7]§2): 도끼 클린 히트 → BleedApplied, 최대 3스택까지 누적. 검(BleedDps=0)은 출혈 없음.
        var axe   = new FighterDef("도끼", FighterStats.Baseline, "WPN_AXE",   "TAC_BRAWLER", "PER_CRUEL");
        var sword = new FighterDef("검",   FighterStats.Baseline, "WPN_SWORD", "TAC_COUNTER", "PER_CALM");

        bool axeBled = false, reached3 = false, swordBled = false;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var ev = new List<SimEvent>();
            new MatchSim().Run(axe, sword, seed, ev);
            foreach (var b in ev.OfType<BleedApplied>())
            {
                if (b.Attacker == 0) { axeBled = true; if (b.Stacks >= 3) reached3 = true; }
                if (b.Attacker == 1) swordBled = true; // 검은 출혈 안 시킴
            }
        }
        Assert.That(axeBled, Is.True, "도끼 클린 히트는 출혈을 일으켜야 함");
        Assert.That(reached3, Is.True, "30경기 내 3스택까지 누적돼야 함");
        Assert.That(swordBled, Is.False, "검(BleedDps=0)은 출혈을 일으키면 안 됨");
    }

    [Test]
    public void Personality_RecklessTrigger_FiresInLowHpMatches()
    {
        // 충동적(HP≤30%) 트리거가 실제 경기에서 발화하는지 — 100시드 중 1회 이상이면 배선 OK
        bool fired = false;
        for (ulong seed = 1; seed <= 100 && !fired; seed++)
        {
            var events = new List<SimEvent>();
            new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, seed, events);
            fired = events.OfType<Decision>().Any(d => d.ReasonTag == "RECKLESS");
        }
        Assert.That(fired, Is.True, "100경기 동안 RECKLESS 트리거가 한 번도 안 터지면 배선 오류");
    }

    [Test]
    public void Taunt_EnragesOpponent_RagedEventEmitted()
    {
        // 도발이 상대 심리에 영향(A): 도발 발동 → 상대에 RAGED(분노) Decision이 따라붙는다.
        var arrogant = new FighterDef("오만", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT");
        var rival    = new FighterDef("도전", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_RECKLESS");
        bool taunted = false, raged = false;
        for (ulong seed = 1; seed <= 500 && !(taunted && raged); seed++)
        {
            var ev = new List<SimEvent>();
            new MatchSim().Run(arrogant, rival, seed, ev);
            var d = ev.OfType<Decision>().ToList();
            if (d.Any(x => x.ReasonTag == "TAUNT")) taunted = true;
            if (d.Any(x => x.ReasonTag == "RAGED")) raged = true;
        }
        Assert.That(taunted, Is.True, "오만함 도발이 500경기 내 한 번은 발동해야 함");
        Assert.That(raged, Is.True, "도발당한 상대에 RAGED(분노)가 배선돼야 함");
    }

    [Test]
    public void Personality_CowardVsCruel_ProduceDifferentFights()
    {
        // 문서[3] 9장 체크 1의 수치 버전: 같은 전술(압박형), 다른 성격 → 통계적으로 구분되는가
        var cruel  = new FighterDef("학살자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL");
        var coward = new FighterDef("허당",   FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_COWARD");
        var anvil  = new FighterDef("기준",   FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM");

        int fearEvents = 0, cruelEvents = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var e1 = new List<SimEvent>();
            new MatchSim().Run(coward, anvil, seed, e1);
            fearEvents += e1.Count(ev => ev is Decision d && d.ReasonTag == "FEAR");

            var e2 = new List<SimEvent>();
            new MatchSim().Run(cruel, anvil, seed, e2);
            cruelEvents += e2.Count(ev => ev is Decision d && d.ReasonTag == "CRUEL");
        }
        Assert.That(fearEvents, Is.GreaterThan(0), "겁쟁이 FEAR 트리거 발화 필요");
        Assert.That(cruelEvents, Is.GreaterThan(0), "잔혹함 CRUEL 트리거 발화 필요");
    }
}

/// <summary>트리거 조건식 단위 검증 (전략층 입력 → bool).</summary>
[TestFixture]
public class TriggerEvalTests
{
    private static TriggerContext Ctx(
        float selfHp = 1f, float oppHp = 1f, int consecHits = 0, bool oppHeavyWindup = false,
        bool oppDown = false, float sinceCrit = 999f, float timeRemain = 1f,
        float oppGauge = 1f, float stamina = 1f, float reserve = 0.2f, int sameWhiff = 0, float deficit = 0f,
        bool oppStaggered = false, float secSinceTaunted = 999f)
        => new(selfHp, oppHp, selfHp > oppHp + 0.05f, consecHits, oppHeavyWindup, oppDown,
               sinceCrit, timeRemain, oppGauge, stamina, reserve, sameWhiff, deficit,
               OppStaggeredPerceived: oppStaggered, SecSinceTaunted: secSinceTaunted);

    [Test]
    public void Reckless_FiresAtOrBelow30PctHp()
    {
        var rule = PersonalityTable.Reckless.Rules[0];
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.30f)), Is.True);
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.31f)), Is.False);
    }

    [Test]
    public void Arrogant_TauntsWhenLeadingAndOppStaggered_NotWhenBehindOrThinLead()
    {
        // M3-B 재설계: 옛 "상대 빈사(HP≤10%)" 트리거는 처벌자가 이미 죽어 역전 0%였다.
        // 새 트리거 "상대 스태거 + HP 리드 ≥ 5.5%p" — 상대가 살아서 처벌 가능한 시점.
        // 리드 폭이 도발 자격 경기를 선별해 조건부 역전패율을 5~10% 밴드로 조인다(M3-B 진단).
        var rule = PersonalityTable.Arrogant.Rules[0];
        Assert.That(rule.Interrupt, Is.EqualTo(InterruptAction.Taunt));
        Assert.That(rule.Cond, Is.EqualTo(TriggerCondition.OppStaggeredWhileAhead));
        // 충분한 리드(40%p) + 상대 스태거 → 발동
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.80f, oppHp: 0.40f, oppStaggered: true)), Is.True);
        // 상대가 스태거 아님 → 미발동
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.80f, oppHp: 0.40f, oppStaggered: false)), Is.False);
        // 열세 → 미발동 (오만함은 앞설 때만 방심)
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.30f, oppHp: 0.80f, oppStaggered: true)), Is.False);
        // 리드가 얇음(3%p < 5.5%p) → 미발동 (박빙 도발이 만드는 잔여 역전을 차단)
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.50f, oppHp: 0.47f, oppStaggered: true)), Is.False);
    }

    [Test]
    public void WasTaunted_FiresOnlyWithinRageWindow()
    {
        // C: 도발 반응 트리거는 분노 창(5초) 안에서만 발동, 도발 이력 없으면(999s) 미발동.
        var rule = Array.Find(PersonalityTable.Calm.Rules, r => r.Cond == TriggerCondition.WasTaunted)!;
        Assert.That(TriggerEval.Matches(rule, Ctx(secSinceTaunted: 1f)), Is.True);
        Assert.That(TriggerEval.Matches(rule, Ctx(secSinceTaunted: 6f)), Is.False, "분노 창 밖");
        Assert.That(TriggerEval.Matches(rule, Ctx()), Is.False, "도발 이력 없음");
    }

    [Test]
    public void TauntReaction_DiffersByPersonality()
    {
        // C 보완: 같은 도발 베이스(A) 위에서 성격별 반응 방향이 갈린다 — 증폭/중화/위축.
        Directive WithRage(PersonalityDef p)
        {
            var d = Directive.From(TacticsTable.Pressure);
            d.Apply(ParamMod.Add(TParam.Aggression, 0.25f));      // A 베이스 분노
            d.Apply(ParamMod.Add(TParam.CommitThreshold, -0.10f));
            var rule = Array.Find(p.Rules, r => r.Cond == TriggerCondition.WasTaunted);
            if (rule != null) foreach (var m in rule.Mods) d.Apply(m);
            return d;
        }
        var bare = Directive.From(TacticsTable.Pressure);
        Assert.That(WithRage(PersonalityTable.Reckless).Aggression, Is.GreaterThan(bare.Aggression), "충동: 분노 증폭");
        Assert.That(WithRage(PersonalityTable.Calm).Aggression, Is.EqualTo(bare.Aggression).Within(1e-5), "냉철: 분노 중화");
        Assert.That(WithRage(PersonalityTable.Coward).PreferredDistance, Is.GreaterThan(bare.PreferredDistance), "겁쟁이: 위축(거리↑)");
    }

    [Test]
    public void Coward_ReactsToHeavyWindupOnly()
    {
        var rule = PersonalityTable.Coward.Rules[0];
        Assert.That(TriggerEval.Matches(rule, Ctx(oppHeavyWindup: true)), Is.True);
        Assert.That(TriggerEval.Matches(rule, Ctx(oppHeavyWindup: false)), Is.False);
    }

    [Test]
    public void Bold_NeedsBothTimeAndDeficit()
    {
        var rule = PersonalityTable.Bold.Rules[0];
        Assert.That(TriggerEval.Matches(rule, Ctx(timeRemain: 0.15f, deficit: 0.10f)), Is.True);
        Assert.That(TriggerEval.Matches(rule, Ctx(timeRemain: 0.15f, deficit: -0.10f)), Is.False, "이기고 있으면 발동 안 함");
        Assert.That(TriggerEval.Matches(rule, Ctx(timeRemain: 0.50f, deficit: 0.10f)), Is.False, "시간 여유 있으면 발동 안 함");
    }

    [Test]
    public void Directive_OverrideSetAndDelta_ThenRollback()
    {
        var d = Directive.From(TacticsTable.Pressure);
        float origAggro = d.Aggression;
        d.Apply(ParamMod.Set(TParam.Aggression, 0.9f));
        d.Apply(ParamMod.Add(TParam.CommitThreshold, 0.2f));
        Assert.That(d.Aggression, Is.EqualTo(0.9f).Within(1e-5));
        Assert.That(d.CommitThreshold, Is.EqualTo(0.35f + 0.2f).Within(1e-5));

        // 롤백 = 재합성 (Override 만료 시 RebuildDirective가 처음부터 다시 쌓음)
        var fresh = Directive.From(TacticsTable.Pressure);
        Assert.That(fresh.Aggression, Is.EqualTo(origAggro).Within(1e-5));
    }
}

/// <summary>밸런스 구조 회귀 테스트 (정밀 승률은 M3, 여기선 구조적 결함만 차단).</summary>
[TestFixture]
public class FairnessRegressionTests
{
    [Test]
    public void MirrorMatch_NoFirstMoverDominance()
    {
        // 동일 선수 거울전 — 선공(인덱스 0) 고정 우위가 생기면 동시 해결 페이즈가 깨진 것.
        // (M2에서 순차 히트 적용이 100:0을 만든 전적이 있음)
        // M3-C: 현재 편향 ~4.8%p (선행 밸런스 작업으로 7.6%p→4.8%p). 밴드를 0.42~0.58로 강화.
        //   주의: 더 조이지 말 것 — 180초 판정 거울전은 카오스적 초민감이라 갱신/트레이드 순서를 손대면
        //   편향이 상쇄가 아니라 '재매핑'된다(검증: 순서교대·독립RNG·resolve-both 전부 편향 이동/악화).
        //   밸런스 데이터 무편향은 배치 도구의 시드별 코너 교대가 담당한다. n=1000 = 상한까지 통계적 여유 ~3.5σ.
        var f = new FighterDef("M", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM");
        int winA = 0, decided = 0;
        for (ulong seed = 1; seed <= 1000; seed++)
        {
            var r = new MatchSim().Run(f, f, seed);
            if (r.Winner == 0) { winA++; decided++; }
            else if (r.Winner == 1) decided++;
        }
        float rate = (float)winA / decided;
        Assert.That(rate, Is.InRange(0.42, 0.58), $"거울전 선공 승률 {rate:P1} — 구조적 편향 한계 초과");
    }

    [Test]
    public void BerserkerVsTactician_CounterLoopExists()
    {
        // 문서[4] 11장 그림의 구조 검증. M3에서 승리 메커니즘이 진화했다(README #2의 "Exhausted 활용" 레버):
        // 창은 이제 ①거리로 도끼 헛스윙을 유도하고 ②카운터로 처벌하며 ③도끼 난사로 지친 버서커를 추가 처벌한다.
        // 따라서 "카운터가 적중의 과반"이 아니라, 카이팅·카운터·가스아웃 세 메커니즘이 모두 살아있는지를 본다.
        int axeWhiffs = 0, axeSwings = 0, axeCleanHits = 0, spearHits = 0, spearCounters = 0, berserkerExhausts = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var events = new List<Morituri.Sim.Events.SimEvent>();
            new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, seed, events);
            var r = events.OfType<Morituri.Sim.Events.HitLanded>().Where(h => h.Attacker == 1).ToList();
            spearHits += r.Count;
            spearCounters += r.Count(h => h.IsCounter);
            berserkerExhausts += events.OfType<Morituri.Sim.Events.StaminaExhausted>().Count(e => e.FighterId == 0);
        }
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var res = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, seed);
            axeWhiffs += res.StatsA.Whiffs; axeSwings += res.StatsA.AttackAttempts;
            axeCleanHits += res.StatsA.CleanHits;
        }
        // M3-A: 카운터 반격이 도끼 선딜을 끊으면(HitStun) 헛스윙 대신 '스윙 무산'으로 기록된다 —
        // 헛스윙률 단독이 아니라 무효 스윙률(헛스윙+끊김 = 시도−클린히트)로 거리 운영을 검증.
        // B(2D) 임계 완화 0.4→0.33: 등속 카이팅 한계로 버서커:전술가 매치업은 2D 밸런스 재튜닝 대기(문서[8]).
        Assert.That(1f - (float)axeCleanHits / axeSwings, Is.GreaterThan(0.33), "도끼 스윙 무효화(헛스윙+끊김) 유도가 작동해야 함");
        Assert.That(spearHits, Is.GreaterThan(0));
        Assert.That((float)spearCounters / spearHits, Is.GreaterThan(0.25), "카운터 루프가 살아있어야 함 (적중의 유의미한 비중)");
        Assert.That(berserkerExhausts, Is.GreaterThan(0), "도끼 난사가 버서커를 가스아웃시켜야 함 (M3 처벌 레버)");
    }

    [Test]
    public void ReplayFrames_AreSampledMonotonically_WithinArena()
    {
        // M4-a: 뷰어가 막대기를 그릴 위치 트랙. 시간 단조 증가 + 위치는 아레나 안 + HP는 [0,1].
        var frames = new List<ReplayFrame>();
        new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 7, null, frames);

        Assert.That(frames.Count, Is.GreaterThan(10), "프레임이 수집돼야 함");
        float r = BalanceConstants.Default.ArenaRadius;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (i > 0) Assert.That(f.Time, Is.GreaterThan(frames[i - 1].Time - 0.001f), "시간 비감소");
            // 원형 핏 경계 안 (중심 0,0 기준 반지름 이내, 마진 여유)
            Assert.That(MathF.Sqrt(f.Ax * f.Ax + f.Ay * f.Ay), Is.LessThan(r + 0.1f));
            Assert.That(MathF.Sqrt(f.Bx * f.Bx + f.By * f.By), Is.LessThan(r + 0.1f));
            Assert.That(f.HpPctA, Is.InRange(0f, 1f));
            Assert.That(f.HpPctB, Is.InRange(0f, 1f));
        }
    }

    // ── 자율 전술 전환(AI) ──────────────────────────────────────────────
    // 라니스타 없는 선수는 경기 중 제 풀 안에서 전술을 갈아탄다. 풀 미지정이면 평가 자체가 없어야 한다.

    private static List<string> AdaptTags(List<SimEvent> ev, int fighter) =>
        ev.OfType<Decision>().Where(d => d.FighterId == fighter && d.ReasonTag.StartsWith("ADAPT_"))
          .Select(d => d.ReasonTag).ToList();

    [Test]
    public void Adapt_WithoutPool_NeverSwitches()
    {
        // 매트릭스·기존 시뮬 격리의 계약: AdaptPool을 안 주면 자율 전환은 존재하지 않는다.
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var ev = new List<SimEvent>();
            new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, seed, ev);
            Assert.That(AdaptTags(ev, 0).Count + AdaptTags(ev, 1).Count, Is.EqualTo(0), $"seed {seed}");
        }
    }

    [Test]
    public void Adapt_StaysInPool_AndCapsAtTwoPerMatch()
    {
        string[] pool = { "TAC_BRAWLER", "TAC_DEFENDER", "TAC_ZONER" };
        var a = new FighterDef("적응형", FighterStats.Baseline, "WPN_SWORD", "TAC_BRAWLER", "PER_CALM", AdaptPool: pool);
        int fired = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var ev = new List<SimEvent>();
            new MatchSim().Run(a, FighterDef.Tactician, seed, ev);
            var tags = AdaptTags(ev, 0);
            Assert.That(tags.Count, Is.LessThan(3), $"seed {seed}: 경기당 상한 2회");
            foreach (var t in tags)
                Assert.That(pool.Contains("TAC_" + t.Substring(6)), Is.True, $"seed {seed}: 풀 밖 전술 {t}");
            fired += tags.Count;
        }
        Assert.That(fired, Is.GreaterThan(0), "30판 중 한 번도 안 바꾸면 기능이 죽은 것");
    }

    [Test]
    public void Adapt_ReadsTheSituation_LosingGoesSafe_WinningGoesIn()
    {
        // 같은 풀·같은 성격인데 판이 다르면 반대쪽으로 간다 — 이게 '상황을 본다'의 조작적 정의.
        var weakStats = FighterStats.Baseline with { Atk = 45f, Def = 45f, HpMax = 480f, Spd = 60f, Rct = 55f };
        var strongStats = FighterStats.Baseline with { Atk = 95f, Def = 90f, HpMax = 900f, Spd = 85f, Rct = 85f };
        string[] pool = { "TAC_BRAWLER", "TAC_DEFENDER" };

        int safe = 0, aggro = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            // 밀리는 쪽: 난전으로 시작해 얻어맞는다 → 지키는 쪽으로 가야 한다.
            var underdog = new FighterDef("약자", weakStats, "WPN_SWORD", "TAC_BRAWLER", "PER_CALM", AdaptPool: pool);
            var bully = new FighterDef("강자", strongStats, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL");
            var e1 = new List<SimEvent>();
            new MatchSim().Run(underdog, bully, seed, e1);
            safe += AdaptTags(e1, 0).Count(t => t == "ADAPT_DEFENDER");

            // 압도하는 쪽: 지키기로 시작했지만 상대가 지치고 빈사다 → 들어가야 한다.
            var closer = new FighterDef("강자", strongStats, "WPN_SWORD", "TAC_DEFENDER", "PER_CALM", AdaptPool: pool);
            var prey = new FighterDef("약자", weakStats, "WPN_SWORD", "TAC_BRAWLER", "PER_RECKLESS");
            var e2 = new List<SimEvent>();
            new MatchSim().Run(closer, prey, seed, e2);
            aggro += AdaptTags(e2, 0).Count(t => t == "ADAPT_BRAWLER");
        }
        Assert.That(safe, Is.GreaterThan(0), "밀리는 쪽이 끝까지 난전을 고집하면 상황을 안 보는 것");
        Assert.That(aggro, Is.GreaterThan(0), "이기는 쪽이 끝까지 웅크리면 상황을 안 보는 것");
    }

    [Test]
    public void ReplayFrames_DoNotPerturbDeterminism()
    {
        // 프레임 수집(읽기 전용 투영)이 시뮬레이션 결과를 바꾸면 안 된다 (원칙 B).
        var bare = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 99);
        var withFrames = new MatchSim().Run(FighterDef.Berserker, FighterDef.Tactician, 99, null, new List<ReplayFrame>());
        Assert.That(withFrames.Winner, Is.EqualTo(bare.Winner));
        Assert.That(withFrames.DurationSec, Is.EqualTo(bare.DurationSec));
        Assert.That(withFrames.ScoreA, Is.EqualTo(bare.ScoreA));
    }
}

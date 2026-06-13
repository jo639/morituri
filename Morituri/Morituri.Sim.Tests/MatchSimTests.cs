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
        bool oppStaggered = false)
        => new(selfHp, oppHp, selfHp > oppHp + 0.05f, consecHits, oppHeavyWindup, oppDown,
               sinceCrit, timeRemain, oppGauge, stamina, reserve, sameWhiff, deficit,
               OppStaggeredPerceived: oppStaggered);

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
    public void Vengeful_IsDormantInPhase1()
    {
        var rule = PersonalityTable.Vengeful.Rules[0];
        Assert.That(TriggerEval.Matches(rule, Ctx(selfHp: 0.01f, deficit: 0.99f)), Is.False,
            "복수심은 Phase 2 관계 시스템 입력 전까지 항상 비활성");
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
        Assert.That(1f - (float)axeCleanHits / axeSwings, Is.GreaterThan(0.4), "도끼 스윙 무효화(헛스윙+끊김) 유도가 작동해야 함");
        Assert.That(spearHits, Is.GreaterThan(0));
        Assert.That((float)spearCounters / spearHits, Is.GreaterThan(0.25), "카운터 루프가 살아있어야 함 (적중의 유의미한 비중)");
        Assert.That(berserkerExhausts, Is.GreaterThan(0), "도끼 난사가 버서커를 가스아웃시켜야 함 (M3 처벌 레버)");
    }
}

using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Sim.Tests;

/// <summary>
/// Phase 2: 감정(T10) 엔진 검증.
/// 원칙: 감정은 의사선택(decision-layer)에만 영향 — 데미지·받피 배율에 손대지 않는다.
/// 같은 결과라도 성격이 다르게 해석한다(EmotionGen). 효과는 행동(공격 빈도·거리·도발)에서 나온다.
/// </summary>
[TestFixture]
public class EmotionTests
{
    // ── 생성: 같은 결과, 성격별 해석 ──
    [Test]
    public void EmotionGen_BranchesByOutcomeAndPersonality()
    {
        // 승리(self=0, winner=0)
        Assert.That(EmotionGen.Classify(0, 0, false, 0.9f, PersonalityTable.Arrogant), Is.EqualTo(EmotionTable.Hubris),  "압승 오만 → 자만");
        Assert.That(EmotionGen.Classify(0, 0, false, 0.5f, PersonalityTable.Coward),   Is.EqualTo(EmotionTable.Pressure), "승리 겁쟁이 → 부담감");
        Assert.That(EmotionGen.Classify(0, 0, false, 0.5f, PersonalityTable.Reckless), Is.EqualTo(EmotionTable.Confident),"승리 충동 → 자신감");

        // 판정/시간 패배(winner=1)
        Assert.That(EmotionGen.Classify(1, 0, false, 0.2f, PersonalityTable.Wary),     Is.EqualTo(EmotionTable.Inferior),  "패배 신중 → 열등감");
        Assert.That(EmotionGen.Classify(1, 0, false, 0.2f, PersonalityTable.Bold),     Is.EqualTo(EmotionTable.Motivated), "패배 대담 → 동기부여");
        Assert.That(EmotionGen.Classify(1, 0, false, 0.2f, PersonalityTable.Cruel),    Is.EqualTo(EmotionTable.Frustrated),"패배 잔혹 → 좌절");

        // KO 패배
        Assert.That(EmotionGen.Classify(1, 0, true, 0.0f, PersonalityTable.Reckless),  Is.EqualTo(EmotionTable.Grudge), "KO패 충동 → 원한");
        Assert.That(EmotionGen.Classify(1, 0, true, 0.0f, PersonalityTable.Coward),    Is.EqualTo(EmotionTable.Trauma), "KO패 겁쟁이 → 트라우마");

        // 무승부 → 중립
        Assert.That(EmotionGen.Classify(-1, 0, false, 0.5f, PersonalityTable.Calm) == null, Is.True, "무승부 → 감정 없음");
    }

    // ── 발생률: 감정은 매 경기 붙는 게 아니라 '가끔'(GenChance) 생기는 이벤트성 변화구 ──
    [Test]
    public void EmotionGen_Roll_RespectsGenChance_OccasionalNotAlways()
    {
        // KO패 + 충동 → 원한(GenChance 0.25). 굴려보면 ~1/4만 실제로 생기고, 생긴 건 전부 원한.
        var rng = new SimRandom(2026);
        int hit = 0, n = 4000;
        for (int i = 0; i < n; i++)
        {
            var id = EmotionGen.Roll(rng, 1, 0, true, 0f, PersonalityTable.Reckless);
            if (id != null) { hit++; Assert.That(id, Is.EqualTo(EmotionTable.Grudge)); }
        }
        float rate = 100f * hit / n;
        Assert.That(rate, Is.InRange(20f, 30f), $"원한 발생률 ≈ GenChance 25% (got {rate:F1})");

        // 평범한 승리(충동→자신감, GenChance 0.07)는 아주 드물게만 생긴다(대부분 무감정).
        var rng2 = new SimRandom(7);
        int conf = 0;
        for (int i = 0; i < n; i++)
            if (EmotionGen.Roll(rng2, 0, 0, false, 0.5f, PersonalityTable.Reckless) != null) conf++;
        float confRate = 100f * conf / n;
        Assert.That(confRate, Is.InRange(4f, 11f), $"자신감 발생률 ≈ 7% (got {confRate:F1})");
    }

    // ── 효과: 감정 종류로 행동·승률이 측정 가능하게 갈린다 (로드맵 합격기준) ──
    [Test]
    public void Emotion_ChangesBehaviorAndWinrate_Measurably()
    {
        const int games = 300;
        var (grudgeWin, grudgeAtk) = MirrorWithEmotionOnA(EmotionTable.Grudge, games);
        var (traumaWin, traumaAtk) = MirrorWithEmotionOnA(EmotionTable.Trauma, games);

        // 핵심(합격기준): 두 감정이 승률을 측정 가능하게 가른다 (방향은 창발 — 미러에선 과공격이 카운터에 당해
        // 오히려 위축 플레이가 이기는 흐름이 나온다. 중요한 건 "감정 유무·종류로 결과가 갈린다"는 사실).
        Assert.That(Math.Abs(grudgeWin - traumaWin), Is.GreaterThan(10f),
            $"원한({grudgeWin:F1}%) vs 트라우마({traumaWin:F1}%) 승률차가 측정 가능해야");
        // 결정 효과의 직접 증거: 원한(공격성↑)은 트라우마(거리유지·위축)보다 더 자주 공격한다.
        Assert.That(grudgeAtk, Is.GreaterThan(traumaAtk),
            $"원한 공격시도({grudgeAtk:F1}) > 트라우마({traumaAtk:F1})");
    }

    // ── 무감정 미러는 ~50/50 + 양쪽 동일 감정도 대칭 보존 (결정론 회귀) ──
    [Test]
    public void Emotion_MirrorSymmetryPreserved()
    {
        const int games = 300;
        float baseWin = MirrorWinrate(null, null, games);
        Assert.That(baseWin, Is.InRange(42f, 58f), $"무감정 미러 = 50/50 근방 (got {baseWin:F1})");

        // 양쪽에 같은 감정 → 여전히 대칭(50/50 근방). 감정이 대칭을 깨지 않음.
        float bothGrudge = MirrorWinrate(new[] { EmotionTable.Grudge }, new[] { EmotionTable.Grudge }, games);
        Assert.That(bothGrudge, Is.InRange(42f, 58f), $"양쪽 원한 = 대칭 보존 (got {bothGrudge:F1})");
    }

    [Test]
    public void Emotion_Determinism_SameSeedSameEmotion_Identical()
    {
        var a = Fighter("A", new[] { EmotionTable.Grudge });
        var b = Fighter("B", null);
        var r1 = new MatchSim().Run(a, b, 99);
        var r2 = new MatchSim().Run(a, b, 99);
        Assert.That(r2.Winner, Is.EqualTo(r1.Winner));
        Assert.That(r2.DurationSec, Is.EqualTo(r1.DurationSec).Within(1e-6));
        Assert.That(r2.ScoreA, Is.EqualTo(r1.ScoreA).Within(1e-4));
    }

    // ── 데이터 불변식: 모든 감정 효과는 '의사선택' 파라미터만 건드린다 (데미지·자원 배율 금지) ──
    [Test]
    public void Emotion_OnlyTouchesDecisionParams()
    {
        // 허용 = Directive 결정 가중치. 금지 = 자원/배율 계열(StamRegenMult)·행동봉쇄(NoAttack)는 감정이 쓰지 않는다.
        var allowed = new HashSet<TParam>
        {
            TParam.Aggression, TParam.CommitThreshold, TParam.GuardBias, TParam.PreferredDistance,
            TParam.DistanceTolerance, TParam.RiskTolerance, TParam.CounterWindow, TParam.FeintRate,
            TParam.StaminaReserve, TParam.HeavyBias, TParam.RepeatBias,
        };
        foreach (var e in EmotionTable.All)
            foreach (var m in e.Mods)
                Assert.That(allowed.Contains(m.Param), Is.True,
                    $"감정 {e.Name}이 비결정 파라미터 {m.Param}를 건드림 — 감정은 의사선택만 바꿔야 한다");
    }

    // ── helpers ──
    private static FighterDef Fighter(string name, string[]? emo) =>
        new(name, FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_RECKLESS", null, emo);

    private static float MirrorWinrate(string[]? emoA, string[]? emoB, int games)
    {
        var a = Fighter("A", emoA);
        var b = Fighter("B", emoB);
        int winA = 0;
        for (ulong s = 1; s <= (ulong)games; s++)
            if (new MatchSim().Run(a, b, s).Winner == 0) winA++;
        return 100f * winA / games;
    }

    private static (float winrate, float avgAtk) MirrorWithEmotionOnA(string emo, int games)
    {
        var a = Fighter("A", new[] { emo });
        var b = Fighter("B", null);
        int winA = 0; double atk = 0;
        for (ulong s = 1; s <= (ulong)games; s++)
        {
            var r = new MatchSim().Run(a, b, s);
            if (r.Winner == 0) winA++;
            atk += r.StatsA.AttackAttempts;
        }
        return (100f * winA / games, (float)(atk / games));
    }
}

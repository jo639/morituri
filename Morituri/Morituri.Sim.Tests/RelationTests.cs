using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Sim.Tests;

/// <summary>
/// Phase 2: 관계(T11) 시스템 검증.
/// 감정과의 차별: 특정 상대 전용 · 메타 누적(RelationLedger) · 트리거 게이트(OppIsNemesis 등). decision-only.
/// </summary>
[TestFixture]
public class RelationTests
{
    // ── 생성·누적: 반복 KO패 → affinity가 적대 밴드로, 성격이 원수/공포 분기 ──
    [Test]
    public void RelationLedger_AccumulatesAdversityFromRepeatedKoLosses()
    {
        var ledger = new RelationLedger();
        // A가 B에게 6번 KO패 (winner=1, KO).
        for (int i = 0; i < 6; i++)
            ledger.RecordMatch("A", "B", winner: 1, wasKo: true, aMinHp: 0f, bMinHp: 0.8f);

        var aToB = ledger.Get("A", "B");
        Assert.That(aToB.Affinity, Is.LessThan(-50f), "반복 KO패 → 강한 적대");
        Assert.That(aToB.Losses, Is.EqualTo(6));

        // 성격 분기: 공격형 → 원수, 소심형 → 공포 (같은 전적, 다른 해석).
        Assert.That(aToB.Classify(PersonalityTable.Reckless.Id), Is.EqualTo(RelationType.Nemesis));
        Assert.That(aToB.Classify(PersonalityTable.Coward.Id), Is.EqualTo(RelationType.Fear));

        // 반대 방향(B→A)은 6승 → 적대 아님(원수/공포 아님).
        var bToA = ledger.Get("B", "A");
        Assert.That(bToA.Classify(PersonalityTable.Reckless.Id), Is.Not.EqualTo(RelationType.Nemesis));
    }

    [Test]
    public void RelationLedger_CloseGames_FormRivalry()
    {
        var ledger = new RelationLedger();
        // 주고받는 접전 4번(둘 다 사선, 승패 번갈아) → 라이벌.
        for (int i = 0; i < 4; i++)
            ledger.RecordMatch("A", "B", winner: i % 2, wasKo: true, aMinHp: 0.1f, bMinHp: 0.1f);
        var aToB = ledger.Get("A", "B");
        Assert.That(aToB.CloseRatio, Is.GreaterThan(0.4f));
        Assert.That(aToB.Classify(PersonalityTable.Calm.Id), Is.EqualTo(RelationType.Rival));
    }

    // ── 메타 쿼리: 복수전 후보 ──
    [Test]
    public void RelationLedger_RevengeCandidates_FindsUnavengedNemesis()
    {
        var ledger = new RelationLedger();
        for (int i = 0; i < 6; i++)
            ledger.RecordMatch("학살자", "공포대상", winner: 1, wasKo: true, aMinHp: 0f, bMinHp: 0.9f);

        var revenge = ledger.RevengeCandidates(id => id == "학살자" ? PersonalityTable.Reckless.Id : PersonalityTable.Calm.Id).ToList();
        Assert.That(revenge.Any(r => r.Self == "학살자" && r.Opp == "공포대상"), Is.True, "갚지 못한 원수 = 복수전 후보");
    }

    // ── 인매치 게이트: 원수 관계 → OppIsNemesis 발동(복수 도발) + 행동 변화 ──
    [Test]
    public void Relation_Nemesis_FiresVengeTauntAndShiftsBehavior()
    {
        // A에게 B를 원수로 주입 → 복수 도발(VENGE) 게이트가 켜져야(평소 PER_WARY는 도발 거의 안 함).
        var a = new FighterDef("A", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_WARY",
            RelationToOpp: RelationType.Nemesis);
        var b = new FighterDef("B", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_WARY");

        int vengeMatches = 0;
        for (ulong s = 1; s <= 60; s++)
        {
            var ev = new List<Morituri.Sim.Events.SimEvent>();
            new MatchSim().Run(a, b, s, ev);
            if (ev.OfType<Morituri.Sim.Events.Decision>().Any(d => d.FighterId == 0 && d.ReasonTag == "VENGE"))
                vengeMatches++;
        }
        Assert.That(vengeMatches, Is.GreaterThan(0), "원수 관계 → 복수 도발(VENGE) 게이트가 발동해야");

        // 무관계 대조군: WARY 미러는 VENGE가 절대 안 나온다(게이트는 그 상대 한정).
        int controlVenge = 0;
        for (ulong s = 1; s <= 60; s++)
        {
            var ev = new List<Morituri.Sim.Events.SimEvent>();
            new MatchSim().Run(b, b, s, ev);
            if (ev.OfType<Morituri.Sim.Events.Decision>().Any(d => d.ReasonTag == "VENGE")) controlVenge++;
        }
        Assert.That(controlVenge, Is.EqualTo(0), "관계 없으면 VENGE 없음(특정 상대 전용 게이트)");
    }

    // ── 결정론: 같은 시드+관계 = 동일 결과 ──
    [Test]
    public void Relation_Determinism_SameSeedSameRelation_Identical()
    {
        var a = new FighterDef("A", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_RECKLESS",
            RelationToOpp: RelationType.Nemesis);
        var b = new FighterDef("B", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM");
        var r1 = new MatchSim().Run(a, b, 77);
        var r2 = new MatchSim().Run(a, b, 77);
        Assert.That(r2.Winner, Is.EqualTo(r1.Winner));
        Assert.That(r2.DurationSec, Is.EqualTo(r1.DurationSec).Within(1e-6));
        Assert.That(r2.ScoreA, Is.EqualTo(r1.ScoreA).Within(1e-4));
    }
}

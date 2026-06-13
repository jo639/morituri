using System.Linq;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Serialization;

namespace Morituri.Sim.Tests;

/// <summary>M3.5: 직렬화 — 결정론의 보상(같은 시드 = 같은 JSON)과 라운드트립 안정성(문서[1] 6장).</summary>
[TestFixture]
public class SerializationTests
{
    private static readonly FighterDef A = FighterDef.Berserker;
    private static readonly FighterDef B = FighterDef.Tactician;

    [Test]
    public void SameSeed_ProducesByteIdenticalJson()
    {
        // 합격 기준(로드맵 M3.5): 같은 시드 재실행 = 동일 JSON. 결정론 회귀 강제.
        string json1 = MatchSerializer.Serialize(MatchSerializer.Capture(A, B, 12345));
        string json2 = MatchSerializer.Serialize(MatchSerializer.Capture(A, B, 12345));
        Assert.That(json2, Is.EqualTo(json1));
    }

    [Test]
    public void RoundTrip_ReserializesIdentically()
    {
        var rec = MatchSerializer.Capture(A, B, 7);
        string json = MatchSerializer.Serialize(rec);
        var back = MatchSerializer.Deserialize(json);

        // 역직렬화 → 재직렬화가 원본과 같으면 모든 필드·이벤트 타입이 보존된 것.
        Assert.That(MatchSerializer.Serialize(back), Is.EqualTo(json));
        Assert.That(back.SchemaVer, Is.EqualTo(MatchSerializer.SchemaVersion));
        Assert.That(back.Seed, Is.EqualTo(7UL));
        Assert.That(back.Events.Count, Is.EqualTo(rec.Events.Count));
        Assert.That(back.Result.Winner, Is.EqualTo(rec.Result.Winner));
    }

    [Test]
    public void Json_CarriesSchemaVersion()
    {
        string json = MatchSerializer.Serialize(MatchSerializer.Capture(A, B, 1));
        Assert.That(json.Contains("\"SchemaVer\":1"), Is.True);
    }

    [Test]
    public void RoundTrip_PreservesPolymorphicEventFields()
    {
        var rec = MatchSerializer.Capture(A, B, 3);
        var hit = rec.Events.OfType<HitLanded>().First();   // 도끼:창 경기엔 반드시 적중이 있다
        var back = MatchSerializer.Deserialize(MatchSerializer.Serialize(rec));
        var hitBack = back.Events.OfType<HitLanded>().First();

        Assert.That(hitBack.Attacker, Is.EqualTo(hit.Attacker));
        Assert.That(hitBack.Damage, Is.EqualTo(hit.Damage));   // float 라운드트립 정확성
        Assert.That(hitBack.IsCounter, Is.EqualTo(hit.IsCounter));
    }
}

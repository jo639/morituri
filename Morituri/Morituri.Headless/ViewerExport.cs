using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;
using Morituri.Sim.Serialization;

namespace Morituri.Headless;

/// <summary>천부·잠재력 결과 — 뷰어 스탯 패널 표시용.</summary>
public sealed record ViewerEndowment(
    string Talent, string Potential,
    float TalentBudget, float PotentialBudget,
    float Atk, float Def, float HpMax, float Spd, float Aspd, float Rct);

/// <summary>한 선수의 정적 정보 — 뷰어가 HP바 라벨·사거리 표시에 쓴다.</summary>
public sealed record ViewerFighter(string Name, string Weapon, string Tactic, string Personality, float Range,
    ViewerEndowment? Endowment = null, string[]? Traits = null, float SizeScale = 1f);

public sealed record ViewerMeta(float ArenaRadius, ViewerFighter A, ViewerFighter B);

/// <summary>
/// 뷰어 봉투 = 정적 메타 + 연속 프레임(위치/HP/자세) + 이산 이벤트(판단·타격 아이콘) + 결과.
/// MatchRecord(schemaVer=1)와 별개의 프레젠테이션 투영 — 도메인 직렬화 스키마를 건드리지 않는다.
/// </summary>
public sealed record ViewerDoc(
    int SchemaVer, ulong Seed, ViewerMeta Meta,
    IReadOnlyList<ReplayFrame> Frames, IReadOnlyList<SimEvent> Events, MatchResult Result);

/// <summary>경기 한 판을 viewer.html이 읽는 JSON으로 내보낸다(로드맵 M4-a).</summary>
public static class ViewerExport
{
    // 천부 등급 한글명 (StatGenReport와 동일)
    private static readonly string[] TalentNames  = { "노예", "투사", "챔피언", "집정관", "불멸자", "카이사르" };
    private static readonly string[] PotNames     = { "잿불", "불씨", "횃불", "화염", "봉화", "태양" };

    public static void Run(FighterDef a, FighterDef b, ulong seed, string outPath,
                           ulong? talentSeedA = null, ulong? talentSeedB = null)
    {
        var events = new List<SimEvent>();
        var frames = new List<ReplayFrame>();
        var result = new MatchSim().Run(a, b, seed, events, frames);

        var endA = talentSeedA.HasValue ? RollEndowment(talentSeedA.Value) : null;
        var endB = talentSeedB.HasValue ? RollEndowment(talentSeedB.Value) : null;

        var doc = new ViewerDoc(MatchSerializer.SchemaVersion, seed,
            new ViewerMeta(BalanceConstants.Default.ArenaRadius, Describe(a, endA), Describe(b, endB)),
            frames, events, result);

        var opts = MatchSerializer.CreateEventAwareOptions(writeIndented: false);
        opts.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, opts));

        Console.WriteLine($"{a.Name} vs {b.Name} (시드 {seed}) → {outPath}  " +
                          $"(프레임 {frames.Count} / 이벤트 {events.Count} / {result.DurationSec:F1}초)");
        if (endA != null) Console.WriteLine($"   A 천부: {endA.Talent} / 잠재력: {endA.Potential}  ATK={endA.Atk:F0} DEF={endA.Def:F0} HP={endA.HpMax:F0} SPD={endA.Spd:F0} ASPD={endA.Aspd:F0} RCT={endA.Rct:F0}");
        if (endB != null) Console.WriteLine($"   B 천부: {endB.Talent} / 잠재력: {endB.Potential}  ATK={endB.Atk:F0} DEF={endB.Def:F0} HP={endB.HpMax:F0} SPD={endB.Spd:F0} ASPD={endB.Aspd:F0} RCT={endB.Rct:F0}");
        Console.WriteLine($"   viewer.html을 브라우저로 열고 이 파일을 끌어다 놓으세요.");
    }

    private static ViewerEndowment RollEndowment(ulong seed)
    {
        var e = StatGen.Roll(new SimRandom(seed));
        var s = e.Stats;
        return new ViewerEndowment(
            TalentNames[(int)e.Talent], PotNames[(int)e.Potential],
            e.TalentBudget, e.PotentialBudget,
            s.Atk, s.Def, s.HpMax, s.Spd, s.Aspd, s.Rct);
    }

    private static ViewerFighter Describe(FighterDef d, ViewerEndowment? end)
    {
        string[]? names = null; float size = 1f;
        if (d.TraitIds is { Length: > 0 })
        {
            var defs = d.TraitIds.Where(TraitTable.Exists).Select(TraitTable.Get).ToArray();
            names = defs.Select(t => t.Name).ToArray();
            foreach (var t in defs) size *= t.SizeScale;   // 거인 등 크기 배율 합성
        }
        return new(d.Name, d.WeaponId, d.TacticsId, d.PersonalityId, WeaponTable.Get(d.WeaponId).Range, end, names, size);
    }
}

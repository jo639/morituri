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

/// <summary>HUD 스킬 슬롯 — 아이콘 + 쿨타임 역산용 초([15]§10.26).</summary>
public sealed record ViewerSkill(string Tag, float Cd, bool Passive);

/// <summary>한 선수의 정적 정보 — 뷰어가 HP바 라벨·사거리 표시에 쓴다.</summary>
public sealed record ViewerFighter(string Name, string Weapon, string Tactic, string Personality, float Range,
    ViewerEndowment? Endowment = null, string[]? Traits = null, float SizeScale = 1f,
    ViewerSkill[]? Skills = null);

public sealed record ViewerMeta(float ArenaRadius, ViewerFighter A, ViewerFighter B,
    string? QuoteA = null, string? QuoteB = null,   // 경기 직전 대사(#5) — 경기장 인트로 줌인 연출용
    int EndFocusIdx = -1,   // 경기 종료 시 줌인할 대상(0=A/1=B, -1=없음) — 리플레이 극적 연출(#5)
    float MatchTimeSec = 180f);   // 제한 시간 — HUD 중앙 카운트다운([15]§10.26)

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

    public static string TalentName(TalentGrade t) => TalentNames[(int)t];
    public static string PotentialName(PotentialGrade p) => PotNames[(int)p];

    public static void Run(FighterDef a, FighterDef b, ulong seed, string outPath,
                           ulong? talentSeedA = null, ulong? talentSeedB = null)
    {
        var events = new List<SimEvent>();
        var frames = new List<ReplayFrame>();
        // 천부시드 주면 그 스탯으로 실제 전투(스탯 차별화 발현 — 2단계 RCT 판단주기 등). 미지정 시 기존 Baseline.
        var rawA = talentSeedA.HasValue ? StatGen.Roll(new SimRandom(talentSeedA.Value)) : (Endowment?)null;
        var rawB = talentSeedB.HasValue ? StatGen.Roll(new SimRandom(talentSeedB.Value)) : (Endowment?)null;
        var simA = rawA.HasValue ? a with { Stats = rawA.Value.Stats } : a;
        var simB = rawB.HasValue ? b with { Stats = rawB.Value.Stats } : b;
        var result = new MatchSim().Run(simA, simB, seed, events, frames);

        var endA = rawA.HasValue ? ToViewer(rawA.Value) : null;
        var endB = rawB.HasValue ? ToViewer(rawB.Value) : null;

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

    /// <summary>이미 실행된 경기(수집된 프레임·이벤트)를 viewer.json으로. Game.PlayNext가 재실행 없이 사용 (W1).
    /// endowA/B를 주면 관전 화면에 천부·실스탯 패널 표시 (감독 모드 — 성장한 현재 스탯).</summary>
    public static void WriteDoc(FighterDef a, FighterDef b, ulong seed, MatchResult result,
        IReadOnlyList<ReplayFrame> frames, IReadOnlyList<SimEvent> events, string outPath,
        ViewerEndowment? endowA = null, ViewerEndowment? endowB = null,
        string? quoteA = null, string? quoteB = null, int endFocusIdx = -1)
    {
        var doc = new ViewerDoc(MatchSerializer.SchemaVersion, seed,
            new ViewerMeta(BalanceConstants.Default.ArenaRadius, Describe(a, endowA), Describe(b, endowB), quoteA, quoteB, endFocusIdx),
            frames, events, result);
        var opts = MatchSerializer.CreateEventAwareOptions(writeIndented: false);
        opts.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, opts));
    }

    private static ViewerEndowment ToViewer(Endowment e)
    {
        var s = e.Stats;
        return new ViewerEndowment(
            TalentNames[(int)e.Talent], PotNames[(int)e.Potential],
            e.TalentBudget, e.PotentialBudget,
            s.Atk, s.Def, s.HpMax, s.Spd, s.Aspd, s.Rct);
    }

    private static ViewerFighter Describe(FighterDef d, ViewerEndowment? end)
    {
        string[]? names = null; float size = 1f, rmult = 1f, radd = 0f;
        float baseR = WeaponTable.Get(d.WeaponId).Range;
        if (d.TraitIds is { Length: > 0 })
        {
            var defs = d.TraitIds.Where(TraitTable.Exists).Select(TraitTable.Get).ToArray();
            names = defs.Select(t => t.Name).ToArray();
            foreach (var t in defs) { size *= t.SizeScale; rmult *= t.RangeMult; radd += t.RangeAdd; }   // 거인: 크기 ×1.3 + 사거리 ×1.4
        }
        return new(d.Name, d.WeaponId, d.TacticsId, d.PersonalityId, baseR * rmult + radd, end, names, size,
                   Loadout(d));
    }

    /// <summary>
    /// HUD 스킬 슬롯([15]§10.26). 액티브·패시브는 MatchSim.CreateRuntime과 **같은 출처**인
    /// `TraitIds`에서 나온다 — 런타임을 들여다볼 필요가 없어 WriteDoc 경로에서도 그대로 쓴다.
    /// 쿨타임 잔량은 뷰어가 Decision("SKILL_"/"PASV_") 이벤트 + 여기 Cd로 역산한다.
    /// **읽기 전용 투영이라 Sim 거동 무접촉.**
    /// </summary>
    private static ViewerSkill[]? Loadout(FighterDef d)
    {
        if (d.TraitIds is not { Length: > 0 }) return null;
        var list = new List<ViewerSkill>();
        foreach (var id in d.TraitIds)
        {
            if (!SkillTable.Exists(id)) continue;
            var s = SkillTable.Get(id);
            if (s.Active is { } a)  list.Add(new ViewerSkill(a.ReasonTag, a.CooldownSec, false));
            if (s.Passive is { } p) list.Add(new ViewerSkill(p.ReasonTag, p.ProcCdSec, true));
        }
        return list.Count > 0 ? list.ToArray() : null;
    }
}

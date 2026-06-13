using System.Text.Encodings.Web;
using System.Text.Json;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;
using Morituri.Sim.Serialization;

namespace Morituri.Headless;

/// <summary>한 선수의 정적 정보 — 뷰어가 HP바 라벨·사거리 표시에 쓴다.</summary>
public sealed record ViewerFighter(string Name, string Weapon, string Tactic, string Personality, float Range);

public sealed record ViewerMeta(float ArenaWidth, ViewerFighter A, ViewerFighter B);

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
    public static void Run(FighterDef a, FighterDef b, ulong seed, string outPath)
    {
        var events = new List<SimEvent>();
        var frames = new List<ReplayFrame>();
        var result = new MatchSim().Run(a, b, seed, events, frames);

        var doc = new ViewerDoc(MatchSerializer.SchemaVersion, seed,
            new ViewerMeta(BalanceConstants.Default.ArenaWidth, Describe(a), Describe(b)),
            frames, events, result);

        var opts = MatchSerializer.CreateEventAwareOptions(writeIndented: false);
        opts.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping; // 한글 이름/판단 태그 가독성
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, opts));

        Console.WriteLine($"{a.Name} vs {b.Name} (시드 {seed}) → {outPath}  " +
                          $"(프레임 {frames.Count} / 이벤트 {events.Count} / {result.DurationSec:F1}초)");
        Console.WriteLine($"   viewer.html을 브라우저로 열고 이 파일을 끌어다 놓으세요.");
    }

    private static ViewerFighter Describe(FighterDef d) =>
        new(d.Name, d.WeaponId, d.TacticsId, d.PersonalityId, WeaponTable.Get(d.WeaponId).Range);
}

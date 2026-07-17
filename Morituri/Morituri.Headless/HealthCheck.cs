using System.Text.Json;

namespace Morituri.Headless;

/// <summary>
/// 장기 헬스체크 — 다중 시드 × 다중 시즌 자동 시뮬로 세계의 불변식을 검증한다.
/// (리그 인원 유지 · 사망/각성 빈도 · 승강 작동 · 연령 분포 · 경제 궤적)
/// 사용: dotnet run -- health [시드수=5] [시즌수=50]
/// </summary>
internal static class HealthCheck
{
    /// <summary>배열 길이 — 빈 컬렉션은 문서에 null로 나온다(Champions·Hall). null/누락 = 0.</summary>
    private static int ArrayLen(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;

    /// <summary>이번 시즌 컵 우승자가 나왔는가 — LastSeason 자체가 null일 수 있다(시즌 미완).</summary>
    private static bool HasCupChampion(JsonElement st) =>
        st.TryGetProperty("LastSeason", out var ls) && ls.ValueKind == JsonValueKind.Object
        && ls.TryGetProperty("CupChampion", out var c) && c.ValueKind != JsonValueKind.Null;

    public static void Run(int seeds, int seasons)
    {
        Console.WriteLine($"=== MORITURI 헬스체크: {seeds}시드 × {seasons}시즌 ===\n");
        string baseDir = Path.Combine(Path.GetTempPath(), $"morituri_health_{Guid.NewGuid():N}");
        int fails = 0;

        // 1) 세계 불변식 (playerless — 순수 AI 리그 장기 구동)
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            string dir = Path.Combine(baseDir, $"s{seed}");
            Directory.CreateDirectory(dir); Directory.SetCurrentDirectory(dir);
            var g = new Game(1, seed * 101, fresh: true, interactive: false, playerless: true);
            int deaths = 0, awakenings = 0, promotes = 0, cupsMissing = 0;
            int expectCast = -1;   // 개막 시즌 인원 = 이 세계의 기준(W16 신규=12 · 구 세이브=6). 이후 유지되어야 한다
            for (int s = 0; s < seasons; s++)
            {
                g.PlayNext();   // 개막
                int guard = 0;
                while (g.SeasonActive && guard++ < 500) g.PlayNext();
                var st = JsonDocument.Parse(g.StateJson()).RootElement;
                var season = st.GetProperty("Season");
                foreach (var e in season.GetProperty("Story").EnumerateArray())
                {
                    string k = e.GetProperty("Kind").GetString()!;
                    if (k == "death") deaths++;
                    if (k == "awakening") awakenings++;
                    if (k == "promote") promotes++;
                }
                if (!HasCupChampion(st)) cupsMissing++;
                int cast = ArrayLen(season, "Fighters");
                if (expectCast < 0)
                {
                    expectCast = cast;
                    if (cast == 0) { Console.WriteLine($"  ✗ 시드{seed}: 리그가 비었다 — 캐스트 생성 실패?"); fails++; break; }
                }
                else if (cast != expectCast)
                { Console.WriteLine($"  ✗ 시드{seed} 시즌{s + 1}: 리그 인원 {cast} ≠ {expectCast}(개막 기준) — 공석 승계 정지?"); fails++; break; }
            }
            var final = JsonDocument.Parse(g.StateJson()).RootElement.GetProperty("Season");
            var ages = final.GetProperty("Fighters").EnumerateArray().Select(f => f.GetProperty("Age").GetInt32()).ToList();
            int hall = ArrayLen(final, "Hall");
            Console.WriteLine($"  시드{seed}: ⚰{deaths} 🌟{awakenings} ⬆{promotes} 컵누락{cupsMissing} " +
                              $"명전{hall} 나이{ages.DefaultIfEmpty(0).Min()}~{ages.DefaultIfEmpty(0).Max()} — {(deaths >= 0 && promotes > 0 ? "OK" : "확인 필요")}");
            if (promotes == 0) { Console.WriteLine($"  ✗ 시드{seed}: {seasons}시즌 동안 승격 0회 — 승강 정지?"); fails++; }
            // 사망 상한 = 선수-시즌의 10% (설계치 ⚰4%/시즌·격전 게이트의 ~2.5배 = 명백한 폭주만).
            // 구 기준 seasons/2는 6인 캐스트 시절 값 — W16 12인에선 기댓값에 걸터앉아 상시 오탐.
            int deathCap = (int)(expectCast * seasons * 0.10f);
            if (expectCast > 0 && deaths > deathCap)
            { Console.WriteLine($"  ✗ 시드{seed}: 사망 과다({deaths} > 상한 {deathCap} = {expectCast}인×{seasons}시즌×10%) — 확률 점검"); fails++; }
        }

        // 2) 경제 궤적 (플레이어 봇 — 영입 후 자동 진행. 인플레·파산 감시)
        {
            string dir = Path.Combine(baseDir, "econ");
            Directory.CreateDirectory(dir); Directory.SetCurrentDirectory(dir);
            var g = new Game(1, 777, fresh: true, interactive: false, playerless: false);
            g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
            Console.WriteLine("\n  경제 궤적(봇, 20시즌): 시즌 → 💰골드 ✨영광 💸빚 선수수");
            for (int s = 1; s <= 20; s++)
            {
                g.PlayNext();
                int guard = 0;
                while (g.SeasonActive && guard++ < 500)
                {
                    g.PlayNext();
                    var pe = JsonDocument.Parse(g.StateJson()).RootElement.GetProperty("PendingEvent");
                    if (pe.ValueKind != JsonValueKind.Null) g.ChooseEventJson(0);   // 이벤트는 항상 1번 선택
                }
                if (s % 5 == 0)
                {
                    var st = JsonDocument.Parse(g.StateJson()).RootElement;
                    float gold = st.GetProperty("Gold").GetSingle(), glory = st.GetProperty("Glory").GetSingle(),
                          debt = st.GetProperty("Debt").GetSingle();
                    int my = st.GetProperty("MyFighters").GetArrayLength();
                    Console.WriteLine($"    S{s,2} → 💰{gold,7:F0} ✨{glory,5:F0} 💸{debt,5:F0} 👥{my}");
                    if (float.IsNaN(gold) || gold < 0) { Console.WriteLine("  ✗ 경제: 골드 음수/NaN"); fails++; }
                }
            }
        }

        try { Directory.SetCurrentDirectory(Path.GetTempPath()); Directory.Delete(baseDir, true); } catch { }
        Console.WriteLine(fails == 0 ? "\n✅ 헬스체크 통과 — 세계는 건강하다" : $"\n❌ 헬스체크 실패 {fails}건");
        Environment.ExitCode = fails == 0 ? 0 : 1;
    }
}

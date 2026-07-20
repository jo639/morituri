using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// [13a] 프롤로그 「AUC 661」 — 오르쿠스의 마지막 경기.
///
/// **왜 시뮬이 아니라 안무인가.** 부록 E-4 게이트를 실증한 결과(orcusprobe),
/// 이 엔진에서 탑방패 방어형은 도끼에게 사실상 지지 않는다 — 도끼 승률 0~5%,
/// 이기는 경우에도 판정승이며 가드 붕괴는 0회였다. 살해 방식(시간을 끌어 숨이 차게 함)은
/// 완벽히 재현되지만 순교(이기고 죽는다)가 재현되지 않는다.
/// 프롤로그를 위해 전투 밸런스를 만지는 것은 매트릭스를 흔드는 일이므로 하지 않는다.
/// → 부록 E-4의 대안: **사전 기록 재생.** 기존 뷰어 포맷(ReplayFrame + SimEvent)을 그대로 쓰되,
///   프레임을 시뮬이 아니라 각본이 만든다. Sim 무접촉 · 매트릭스 무관 · 로스터 밖 1회성.
///
/// **안무가 지켜야 할 것(플레이어는 설명 없이 이것만 본다):**
///   ① 도끼가 처음부터 무리하게 돌진한다      — 그는 시간이 없었다
///   ② 방패는 한 번도 공격하지 않는다          — 죽이러 온 게 아니라 기다리러 왔다
///   ③ 도끼의 스태미나가 비정상적으로 마른다   — 폐
///   ④ 길어질수록 도끼가 느려진다              — 심장이 한계에 다다른다
///   ⑤ 마지막에 방패가 쪼개진다                — 사람이 죽기 직전에 얼마나 세지는지
///   ⑥ 이긴 쪽이 일어나지 않는다               — 환호 속에서 심장이 멈춘다
/// </summary>
internal static class PrologueReplay
{
    private const float Dur = 152f;          // 프롤로그 길이(초) — 3분은 길다
    private const float Step = 1f / 30f;     // 30Hz 투영(뷰어 보간 — 60Hz 시뮬 프레임이 아니어도 된다)
    private const float R = 7.0f;            // 아레나 반경 근사

    private static FighterDef Orcus() => new("오르쿠스",
        new FighterStats(128f, 96f, 620f, 96f, 104f, 100f), "WPN_AXE", "TAC_PRESSURE", "PER_CRUEL");
    private static FighterDef Scutatus() => new("스쿠타투스",
        new FighterStats(62f, 132f, 820f, 88f, 82f, 104f), "WPN_SHIELD", "TAC_DEFENDER", "PER_WARY");

    /// <summary>viewer.json에 프롤로그를 써넣는다. 클라이언트는 기존 관전 화면으로 그대로 재생한다.</summary>
    public static void Write(string outPath = "viewer.json")
    {
        var frames = new List<ReplayFrame>((int)(Dur / Step) + 4);
        var events = new List<SimEvent>(64);

        // 도끼가 숨이 차는 순간들 — 관중은 지루해했고, 그는 죽어가고 있었다
        foreach (float t in new[] { 46f, 74f, 96f, 112f, 126f, 138f, 146f })
            events.Add(new StaminaExhausted(t, 0));

        // 방패는 끝까지 치지 않는다. 도끼만 휘두른다 — 점점 느리게.
        float swing = 3f, gap = 2.4f;
        while (swing < 143f)
        {
            bool heavy = swing > 120f || (int)(swing / gap) % 3 == 0;
            events.Add(new AttackSwung(swing, 0, heavy ? "AXE_H" : "AXE_L", false));
            if (heavy && swing > 30f)
                events.Add(new HitLanded(swing + 0.35f, 0, 1, heavy ? 26f : 12f, false, false, IsGuarded: true));
            gap = 2.4f + 3.6f * Mathf01((swing - 20f) / 120f);   // 느려진다 = 간격이 벌어진다
            swing += gap;
        }

        // 클라이맥스 — 방패가 쪼개지고, 무너지고, 끝난다
        events.Add(new GuardBroken(144.2f, 1));
        events.Add(new AttackSwung(144.6f, 0, "AXE_H", false));
        events.Add(new HitLanded(145.1f, 0, 1, 210f, IsCrit: true, IsCounter: false, IsGuarded: false));
        events.Add(new KnockedDown(145.4f, 1));
        events.Add(new MatchEnded(146.0f, 0, "KO", 1f, 0f));

        for (float t = 0f; t <= Dur + 1e-4f; t += Step)
        {
            float p = t / Dur;

            // ── 거리: 도끼가 밀고, 방패가 물러난다. 원형 핏을 도는 추격.
            float ang = 0.55f * t * (1f - 0.45f * Mathf01(p * 1.4f));      // 추격이 느려진다
            float sep = t < 144f ? 2.3f + 0.9f * MathF.Sin(t * 0.8f) : 1.1f;
            float rad = t < 144f ? 4.6f + 1.0f * MathF.Sin(t * 0.21f) : 3.2f;
            float ax = MathF.Cos(ang) * rad, ay = MathF.Sin(ang) * rad;
            float bx = MathF.Cos(ang + sep / rad) * rad, by = MathF.Sin(ang + sep / rad) * rad;

            // ── 스태미나: 도끼는 마른다(폐). 방패는 거의 쓰지 않는다(기다리기만 하므로).
            float stamA = t < 144f
                ? MathF.Max(0.04f, 0.92f - 0.86f * Mathf01(p / 0.95f) + 0.05f * MathF.Sin(t * 1.7f))
                : 0.03f;
            float stamB = 1.0f - 0.14f * p;

            // ── HP: 방패는 가드로 거의 다 막는다. 마지막 일격에만 무너진다.
            float hpA = 1.0f - 0.06f * p;                       // 방패는 치지 않으므로 도끼는 안 다친다
            float hpB = t < 144.2f ? 1.0f - 0.34f * Mathf01(p / 0.95f)
                      : t < 145.1f ? 0.66f
                      : MathF.Max(0f, 0.66f - 0.66f * Mathf01((t - 145.1f) / 0.6f));

            // ── 자세
            var stA = t >= 146f ? FighterState.Down                       // ⑥ 이긴 쪽이 일어나지 않는다
                    : NearSwing(events, t) ? FighterState.Active
                    : stamA < 0.18f ? FighterState.Idle                    // ④ 느려진다
                    : FighterState.Move;
            var stB = t >= 145.4f ? FighterState.Down
                    : t >= 144.2f ? FighterState.Stagger                   // ⑤ 방패가 쪼개진다
                    : FighterState.Guard;                                  // ② 끝까지 막기만 한다

            // ── 군중: 처음엔 뜨겁다가, 지루해하다가, 마지막에 폭발한다
            float crowd = t < 30f ? 25f + 20f * p
                        : t < 120f ? 30f - 45f * Mathf01((t - 30f) / 90f)  // 술렁임 — 그는 죽어가고 있었다
                        : t < 144f ? -15f
                        : 100f;
            frames.Add(new ReplayFrame(t, ax, ay, bx, by, hpA, hpB, stamA, stamB,
                stA, stB, MotionKind.Heavy, MotionKind.Light, crowd));
        }

        var result = new MatchResult(0, "KO", 146.0f, 1f, 0f,
            new MatchFighterStats("오르쿠스", 980f, 41, 1, 58, 17, 0f, 0.94f, 0.94f, false, 0, 0),
            new MatchFighterStats("스쿠타투스", 0f, 0, 0, 0, 0, 0f, 0f, 0f, false, 44, 0));

        ViewerExport.WriteDoc(Orcus(), Scutatus(), 661UL, result, frames, events, outPath,
            quoteA: "…", quoteB: "…", endFocusIdx: 0);
    }

    private static float Mathf01(float v) => Math.Clamp(v, 0f, 1f);
    private static bool NearSwing(List<SimEvent> ev, float t) =>
        ev.Any(e => e is AttackSwung && MathF.Abs(e.Time - t) < 0.25f);
}

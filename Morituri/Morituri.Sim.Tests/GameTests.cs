using System.Text.Json;
using Morituri.Headless;

namespace Morituri.Sim.Tests;

/// <summary>
/// 감독 모드 Game(Meta층) 회귀 — 결정론·미드시즌 세이브 재개·영입 마스킹·방출·세대교체.
/// Game은 cwd에 world.json을 쓰므로 각 테스트는 고유 temp 디렉터리에서 실행(격리).
/// </summary>
[TestFixture]
public class GameTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"morituri_test_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
        return dir;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static void RunFullSeason(Game g)
    {
        g.PlayNext();                       // 개막
        int guard = 0;
        while (g.SeasonActive && guard++ < 400) g.PlayNext();
    }

    [Test]
    public void Game_Determinism_SameWorldSeed_SameOutcome()
    {
        TempDir("detA");
        var g1 = new Game(1, 42, fresh: true, interactive: false, playerless: true);
        RunFullSeason(g1);
        string s1 = g1.StateJson();

        TempDir("detB");
        var g2 = new Game(1, 42, fresh: true, interactive: false, playerless: true);
        RunFullSeason(g2);
        string s2 = g2.StateJson();

        Assert.That(s2, Is.EqualTo(s1));   // 같은 worldSeed = 같은 캐스트·경기·챔피언·역사
    }

    [Test]
    public void Game_MidseasonSaveLoad_ResumesIdentically()
    {
        // A: 5경기 후 앱 종료 → 재로드 → 완주. B: 논스톱 완주. 결과 동일해야(미드시즌 재개 결정론).
        TempDir("resumeA");
        var g1 = new Game(1, 7, fresh: true, interactive: false, playerless: true);
        g1.PlayNext();                              // 개막
        for (int i = 0; i < 5; i++) g1.PlayNext();  // 5경기 (매 경기 저장됨)
        var g2 = new Game(1, 7, fresh: false, interactive: false, playerless: true);  // "재시작"
        Assert.That(g2.SeasonActive, Is.True);
        int guard = 0;
        while (g2.SeasonActive && guard++ < 400) g2.PlayNext();
        string resumed = g2.StateJson();

        TempDir("resumeB");
        var g3 = new Game(1, 7, fresh: true, interactive: false, playerless: true);
        RunFullSeason(g3);

        Assert.That(resumed, Is.EqualTo(g3.StateJson()));
    }

    [Test]
    public void Game_Gacha_MasksCandidates_RecruitReveals()
    {
        TempDir("gacha");
        var g = new Game(1, 5, fresh: true, interactive: false, playerless: false);
        var st = Parse(g.GachaJson());
        Assert.That(st.GetProperty("FreeGachas").GetInt32(), Is.EqualTo(1), "무료 뽑기 1회 소모");
        var cands = st.GetProperty("Candidates");
        Assert.That(cands.GetArrayLength(), Is.EqualTo(3), "후보 3명");
        // 마스킹: 후보 카드엔 무기·성격·전술1만 — 천부/특성 필드 자체가 없다
        var c0 = cands[0];
        Assert.That(c0.TryGetProperty("Talent", out _), Is.False, "천부 비공개");
        Assert.That(c0.TryGetProperty("Traits", out _), Is.False, "특성 비공개");
        Assert.That(c0.GetProperty("RevealedTactic").GetString()!.Length > 0, Is.True);

        var after = Parse(g.RecruitJson(0));
        var mine = after.GetProperty("MyFighters");
        Assert.That(mine.GetArrayLength(), Is.EqualTo(1));
        var f = mine[0];
        Assert.That(f.GetProperty("Talent").GetString()!.Length > 0, Is.True, "영입 후 천부 공개");
        Assert.That(f.GetProperty("TacticPool").GetArrayLength(), Is.EqualTo(3), "전술풀 3종 공개");
        Assert.That(after.GetProperty("Candidates").GetArrayLength(), Is.EqualTo(0), "나머지 후보는 떠남");
    }

    [Test]
    public void Game_Release_PreseasonOnly()
    {
        TempDir("release");
        var g = new Game(1, 5, fresh: true, interactive: false, playerless: false);
        g.GachaJson();
        var st = Parse(g.RecruitJson(0));
        string id = st.GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;

        // 프리시즌: 방출 가능
        var rel = Parse(g.ReleaseJson(id));
        Assert.That(rel.TryGetProperty("error", out _), Is.False);
        Assert.That(rel.GetProperty("MyFighters").GetArrayLength(), Is.EqualTo(0));

        // 시즌 중: 방출 불가
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        string id2 = Parse(g.StateJson()).GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;
        Assert.That(Parse(g.ReleaseJson(id2)).TryGetProperty("error", out _), Is.True, "시즌 중 방출 금지");
    }

    [Test]
    public void Game_Cup_ChampionCrownedEachSeason()
    {
        TempDir("cup");
        var g = new Game(1, 9, fresh: true, interactive: true, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        RunFullSeason(g);
        var st = Parse(g.StateJson());
        // 시즌 종료 → 컵 대진 8개... 최소 리그 챔피언 + 컵 챔피언이 요약에 존재
        var last = st.GetProperty("LastSeason");
        Assert.That(last.ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
        Assert.That(last.GetProperty("CupChampion").ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "컵 우승자 확정");
        var cup = st.GetProperty("Cup");
        Assert.That(cup.ValueKind, Is.EqualTo(JsonValueKind.Array), "컵 대진 노출");
        Assert.That(cup.GetArrayLength(), Is.EqualTo(3), "4강 2 + 결승 1");
        Assert.That(cup[2].GetProperty("Winner").ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "결승 승자");
    }

    [Test]
    public void Game_LudusRep_RisesWithMyWins()
    {
        TempDir("rep");
        var g = new Game(1, 11, fresh: true, interactive: true, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        float rep0 = Parse(g.StateJson()).GetProperty("Ludus").GetProperty("Rep").GetSingle();
        for (int s = 0; s < 3; s++) RunFullSeason(g);
        float rep1 = Parse(g.StateJson()).GetProperty("Ludus").GetProperty("Rep").GetSingle();
        Assert.That(rep1, Is.GreaterThan(rep0), "승리·활동으로 루두스 명성 상승");
    }

    [Test]
    public void Game_TextEvent_Spawns_Persists_AndResolves()
    {
        TempDir("event");
        var g = new Game(1, 5, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        // 내 경기를 진행하며 이벤트 스폰을 기다림(최대 2시즌)
        bool spawned = false; int guard = 0;
        while (guard++ < 800)
        {
            g.PlayNext();
            if (Parse(g.StateJson()).GetProperty("PendingEvent").ValueKind != JsonValueKind.Null) { spawned = true; break; }
        }
        Assert.That(spawned, Is.True, "플레이어 경기 후 텍스트 이벤트 스폰");

        // 앱 재시작 재현 — 세이브에서 재로드해도 대기 이벤트 유지
        var g2 = new Game(1, 5, fresh: false, interactive: false, playerless: false);
        var ev = Parse(g2.StateJson()).GetProperty("PendingEvent");
        Assert.That(ev.ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "이벤트가 세이브에 영속(미드시즌 재개)");
        int nChoices = ev.GetProperty("Choices").GetArrayLength();
        Assert.That(nChoices, Is.GreaterThan(1));

        // 선택 → 결과 + 이벤트 소거
        var res = Parse(g2.ChooseEventJson(0));
        Assert.That(res.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(Parse(g2.StateJson()).GetProperty("PendingEvent").ValueKind, Is.EqualTo(JsonValueKind.Null), "선택 후 이벤트 소거");
        // 이미 소거된 이벤트 재선택 → 오류
        Assert.That(Parse(g2.ChooseEventJson(0)).TryGetProperty("error", out _), Is.True);
    }

    [Test]
    public void Game_AutoFinish_CompletesSeason_ResolvingEvents()
    {
        TempDir("auto");
        var g = new Game(1, 3, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        int guard = 0;
        while (g.SeasonActive && guard++ < 50)
        {
            var r = Parse(g.AutoFinishJson());
            if (r.TryGetProperty("eventPending", out var ep) && ep.GetBoolean()) g.ChooseEventJson(0);
        }
        Assert.That(g.SeasonActive, Is.False, "자동완주로 시즌 종료");
        Assert.That(Parse(g.StateJson()).GetProperty("LastSeason").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void Game_WorldVariance_DifferentSeeds_DifferentCastAndLudi()
    {
        TempDir("varA");
        var g1 = new Game(1, 100, fresh: true, interactive: false, playerless: true);
        var f1 = Parse(g1.StateJson()).GetProperty("Season").GetProperty("Fighters")
            .EnumerateArray().Select(f => f.GetProperty("Name").GetString()).OrderBy(x => x).ToList();

        TempDir("varB");
        var g2 = new Game(1, 200, fresh: true, interactive: false, playerless: true);
        var f2 = Parse(g2.StateJson()).GetProperty("Season").GetProperty("Fighters")
            .EnumerateArray().Select(f => f.GetProperty("Name").GetString()).OrderBy(x => x).ToList();

        Assert.That(f1.Count, Is.EqualTo(6)); Assert.That(f2.Count, Is.EqualTo(6));
        Assert.That(string.Join(",", f1), Is.Not.EqualTo(string.Join(",", f2)), "worldSeed마다 다른 캐스트(12인 풀 선발)");

        var l1 = Parse(g1.StateJson()).GetProperty("LudusTable").EnumerateArray().Select(x => x.GetProperty("Name").GetString()).OrderBy(x => x).ToList();
        var l2 = Parse(g2.StateJson()).GetProperty("LudusTable").EnumerateArray().Select(x => x.GetProperty("Name").GetString()).OrderBy(x => x).ToList();
        Assert.That(string.Join(",", l1), Is.Not.EqualTo(string.Join(",", l2)), "worldSeed마다 다른 라이벌 루두스(6종 풀 선발)");
    }

    [Test]
    public void Game_Rename_LudusAndFighter_PersistsAndValidates()
    {
        TempDir("rename");
        var g = new Game(1, 29, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);

        var st = Parse(g.RenameJson("ludus", "", "카푸아의 늑대들"));
        Assert.That(st.GetProperty("LudusName").GetString(), Is.EqualTo("카푸아의 늑대들"));

        string id = st.GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;
        var st2 = Parse(g.RenameJson("fighter", id, "무쇠이빨"));
        Assert.That(st2.GetProperty("MyFighters")[0].GetProperty("Name").GetString(), Is.EqualTo("무쇠이빨"));
        // 중복·길이 검증
        Assert.That(Parse(g.RenameJson("fighter", id, "막시무스")).TryGetProperty("error", out _), Is.True, "AI와 중복 금지");
        Assert.That(Parse(g.RenameJson("fighter", id, "")).TryGetProperty("error", out _), Is.True);

        // 재시작 후에도 유지(영속)
        var g2 = new Game(1, 29, fresh: false, interactive: false, playerless: false);
        var st3 = Parse(g2.StateJson());
        Assert.That(st3.GetProperty("LudusName").GetString(), Is.EqualTo("카푸아의 늑대들"));
        Assert.That(st3.GetProperty("MyFighters")[0].GetProperty("Name").GetString(), Is.EqualTo("무쇠이빨"));
    }

    [Test]
    public void Game_BigMatchProposal_OffersPick_AndSchedulesCard()
    {
        TempDir("proposal");
        var g = new Game(1, 23, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
        // 제안(빅매치)이 뜨는 시즌 개막까지(결정론 60%/시즌)
        JsonElement prop = default; bool found = false; int guard = 0;
        while (guard++ < 20 && !found)
        {
            g.PlayNext();   // 개막
            var st = Parse(g.StateJson());
            var pp = st.GetProperty("PendingProposal");
            if (pp.ValueKind != JsonValueKind.Null) { prop = pp; found = true; break; }
            while (g.SeasonActive) g.PlayNext();   // 시즌 소화 후 다음 개막
        }
        Assert.That(found, Is.True, "빅매치 제안 발생");
        Assert.That(prop.GetProperty("Roster").GetArrayLength(), Is.GreaterThan(1), "로스터 선택지");

        int before = Parse(g.StateJson()).GetProperty("Season").GetProperty("TotalMatches").GetInt32();
        string pick = prop.GetProperty("Roster")[0].GetProperty("Id").GetString()!;
        var after = Parse(g.PickProposalJson(pick));
        Assert.That(after.GetProperty("PendingProposal").ValueKind, Is.EqualTo(JsonValueKind.Null), "선택 후 제안 소거");
        int afterN = after.GetProperty("Season").GetProperty("TotalMatches").GetInt32();
        Assert.That(afterN, Is.EqualTo(before + 1), "전시 카드 1장 편성");
        // 거절 경로: 다시 제안 상태가 아니면 오류
        Assert.That(Parse(g.PickProposalJson("")).TryGetProperty("error", out _), Is.True);
    }

    [Test]
    public void Game_Glory_AccruesFromFeats_AndBreakthroughRaisesCap()
    {
        TempDir("glory");
        var g = new Game(1, 19, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
        // 업적(첫 승 등)으로 영광이 쌓일 때까지 여러 시즌
        for (int s = 0; s < 4; s++) RunFullSeason(g);
        var st = Parse(g.StateJson());
        float glory = st.GetProperty("Glory").GetSingle();
        Assert.That(glory, Is.GreaterThan(0f), "위신 업적/타이틀로 영광 획득");

        // 잠재력 돌파 — 영광 소모, 상한 상승
        string id = st.GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;
        float budBefore = st.GetProperty("MyFighters")[0].GetProperty("PotentialBudget").GetSingle();
        var after = Parse(g.BreakthroughJson(id));
        Assert.That(after.TryGetProperty("error", out _), Is.False, "영광 충분 → 돌파 성공");
        float budAfter = after.GetProperty("MyFighters").EnumerateArray().First(f => f.GetProperty("Id").GetString() == id).GetProperty("PotentialBudget").GetSingle();
        Assert.That(budAfter, Is.GreaterThan(budBefore), "잠재력 상한 상승");
        Assert.That(after.GetProperty("Glory").GetSingle(), Is.LessThan(glory), "영광 소모");
    }

    [Test]
    public void Game_Divisions_SplitByFame_AndMatchWithinDivision()
    {
        TempDir("div");
        var g = new Game(1, 17, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 2; s++) RunFullSeason(g);   // 명성 벌어지도록
        g.PlayNext();   // 다음 시즌 개막(디비전 재산정 반영)
        var fs = Parse(g.StateJson()).GetProperty("Season").GetProperty("Fighters");
        int d1 = 0, d2 = 0; float maxD2Fame = 0, minD1Fame = float.MaxValue;
        foreach (var f in fs.EnumerateArray())
        {
            int div = f.GetProperty("Division").GetInt32();
            float fame = f.GetProperty("Fame").GetSingle();
            if (div == 1) { d1++; minD1Fame = MathF.Min(minD1Fame, fame); }
            else { d2++; maxD2Fame = MathF.Max(maxD2Fame, fame); }
        }
        Assert.That(d1, Is.GreaterThan(0)); Assert.That(d2, Is.GreaterThan(0), "두 디비전 모두 존재");
        Assert.That(d1 - d2, Is.LessThan(2), "상위 절반 배치(균등)");
        // 1부 최저 명성 >= 2부 최고 명성 (명성 랭킹 배치)
        Assert.That(minD1Fame >= maxD2Fame, Is.True, "명성 랭킹으로 부 배치");
    }

    [Test]
    public void Game_RivalLudi_CompeteRankAndPersist()
    {
        TempDir("rival");
        var g = new Game(1, 7, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 3; s++) RunFullSeason(g);
        var lt = Parse(g.StateJson()).GetProperty("LudusTable");
        Assert.That(lt.GetArrayLength(), Is.EqualTo(3), "playerless = 라이벌 루두스 3개");
        float prev = float.MaxValue; bool anyRep = false;
        foreach (var l in lt.EnumerateArray())
        {
            float rep = l.GetProperty("Rep").GetSingle();
            Assert.That(rep <= prev, Is.True, "명성 내림차순 정렬");
            if (rep > 0f) anyRep = true;
            prev = rep;
        }
        Assert.That(anyRep, Is.True, "경기·우승으로 라이벌 루두스 명성 누적");

        // 세이브 재개 후에도 순위·명성 유지
        var g2 = new Game(1, 7, fresh: false, interactive: false, playerless: true);
        var lt2 = Parse(g2.StateJson()).GetProperty("LudusTable");
        Assert.That(lt2[0].GetProperty("Rep").GetSingle(), Is.EqualTo(lt[0].GetProperty("Rep").GetSingle()), "재개 후 명성 보존");
    }

    [Test]
    public void Game_Odds_ExposedForPlayerMatch_Consistent()
    {
        TempDir("odds");
        var g = new Game(1, 15, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        JsonElement nm = default; bool found = false; int guard = 0;
        while (guard++ < 60)
        {
            nm = Parse(g.StateJson()).GetProperty("NextMatch");
            if (nm.ValueKind != JsonValueKind.Null && nm.GetProperty("IsPlayerMatch").GetBoolean()) { found = true; break; }
            g.PlayNext();
        }
        Assert.That(found, Is.True, "내 경기 프리뷰 도달");
        float pct = nm.GetProperty("MyWinPct").GetSingle();
        float myOdds = nm.GetProperty("MyOdds").GetSingle();
        Assert.That(pct, Is.GreaterThan(14f)); Assert.That(pct, Is.LessThan(86f));   // 극단 클램프
        Assert.That(myOdds, Is.GreaterThan(1f), "배당 > 1");
        // 배당 ≈ 100/승률 (표시용 근사) — ±0.15 허용
        Assert.That(MathF.Abs(myOdds - 100f / pct), Is.LessThan(0.15f), "배당·승률 정합");
        Assert.That(nm.GetProperty("Hype").GetSingle(), Is.GreaterThan(-1f));
    }

    [Test]
    public void Game_Fatigue_RisesWithMatches_AndInjuriesOccur()
    {
        TempDir("fatigue");
        var g = new Game(1, 21, fresh: true, interactive: false, playerless: true);
        g.PlayNext();   // 개막
        // 경기를 진행하며 피로도 상승·부상 발생 관찰(여러 시즌)
        bool fatigueRose = false, injurySeen = false; int guard = 0;
        while (guard++ < 300 && !(fatigueRose && injurySeen))
        {
            g.PlayNext();
            var fs = Parse(g.StateJson()).GetProperty("Season").GetProperty("Fighters");
            foreach (var f in fs.EnumerateArray())
            {
                if (f.GetProperty("Fatigue").GetInt32() > 0) fatigueRose = true;
                if (f.GetProperty("Injured").GetBoolean()) injurySeen = true;
            }
        }
        Assert.That(fatigueRose, Is.True, "경기 소화 → 피로도 상승");
        Assert.That(injurySeen, Is.True, "격전에서 부상 발생");
    }

    [Test]
    public void Game_Profile_ExposesEpithetsAndRelations()
    {
        TempDir("profile");
        var g = new Game(1, 13, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 3; s++) RunFullSeason(g);   // 전적·관계·이명이 쌓이도록 3시즌
        var fs = Parse(g.StateJson()).GetProperty("Season").GetProperty("Fighters");
        string id = fs[0].GetProperty("Id").GetString()!;
        var prof = Parse(g.ProfileJson(id));
        Assert.That(prof.GetProperty("Name").GetString()!.Length, Is.GreaterThan(0));
        Assert.That(prof.GetProperty("Epithets").ValueKind, Is.EqualTo(JsonValueKind.Array), "이명 배열 노출");
        Assert.That(prof.GetProperty("Relations").ValueKind, Is.EqualTo(JsonValueKind.Array), "관계 배열 노출");
        Assert.That(prof.GetProperty("Stats").GetProperty("Atk").GetSingle(), Is.GreaterThan(0), "실스탯 노출");
        // 없는 id → 오류
        Assert.That(Parse(g.ProfileJson("NOPE")).TryGetProperty("error", out _), Is.True);
    }

    [Test]
    public void Game_ManySeasons_AgingRotation_KeepsLeagueAlive()
    {
        // 25시즌 연속 — 노쇠 AI(노화+6시즌 = 36~42세)는 은퇴(명전)하고 신인이 와 리그는 6명 유지.
        TempDir("aging");
        var g = new Game(1, 11, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 25; s++) RunFullSeason(g);

        var season = Parse(g.StateJson()).GetProperty("Season");
        Assert.That(season.GetProperty("Fighters").GetArrayLength(), Is.EqualTo(6), "세대교체로 리그 6명 유지");
        Assert.That(season.GetProperty("Champions").GetArrayLength(), Is.EqualTo(25), "역대 챔피언 25명 기록");
        Assert.That(season.GetProperty("Hall").GetArrayLength(), Is.GreaterThan(0), "은퇴자(명예의 전당) 발생");
    }
}

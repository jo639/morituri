using System.Text.Json;
using System.Text.Json.Nodes;
using Morituri.Headless;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

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

        // 시즌 중에도 방출 가능(#3) + 잔여 일정 정리 → 이후 진행이 깨지지 않음
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        string id2 = Parse(g.StateJson()).GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;
        Assert.That(Parse(g.ReleaseJson(id2)).TryGetProperty("error", out _), Is.False, "시즌 중 방출 허용");
        while (g.SeasonActive) g.PlayNext();   // 방출된 선수가 잔여 일정에서 빠져 예외 없이 완주
        Assert.That(g.SeasonActive, Is.False, "방출 후 시즌 정상 완주");
    }

    [Test]
    public void Game_News_NoDuplicateIssuesAfterSeasonEnds()
    {
        // 프리시즌(시즌 종료 후): 방금 끝난 시즌이 아카이브에 박제됨 → 현행분과 겹쳐 각 월이 두 번 뜨면 안 된다.
        TempDir("news");
        var g = new Game(1, 71, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        RunFullSeason(g);                 // 시즌 완주 → SeasonActive=false(프리시즌)
        Assert.That(g.SeasonActive, Is.False, "시즌 종료(프리시즌)");

        var issues = Parse(g.NewsJson()).GetProperty("issues");
        var seen = new HashSet<(int, int)>();
        foreach (var iss in issues.EnumerateArray())
        {
            var key = (iss.GetProperty("Season").GetInt32(), iss.GetProperty("Month").GetInt32());
            Assert.That(seen.Add(key), Is.True, $"월보 중복 발행 (시즌 {key.Item1} · {key.Item2}월)");
        }
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
        // 내 경기를 진행하며 이벤트 스폰을 기다림(최대 2시즌).
        // [13a] 캠페인 씬(story_*)은 이 테스트의 대상이 아니다 — 마주치면 해소하고 랜덤 이벤트를 계속 기다린다.
        bool spawned = false; int guard = 0;
        while (guard++ < 800)
        {
            var pe = Parse(g.StateJson()).GetProperty("PendingEvent");
            if (pe.ValueKind != JsonValueKind.Null)
            {
                if (!pe.GetProperty("Id").GetString()!.StartsWith("story_")) { spawned = true; break; }
                g.ChooseEventJson(1);   // 스토리 씬은 소극 선택으로 흘려보낸다
                continue;
            }
            g.PlayNext();
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
    public void Game_EmpirePerks_SpendGlory_ApplyDiscount_Persist()
    {
        TempDir("perk");
        var g = new Game(1, 19, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0); g.GachaJson(); g.RecruitJson(0);
        int guard = 0; float glory = 0;
        while (guard++ < 12 && glory < 6) { RunFullSeason(g); glory = Parse(g.StateJson()).GetProperty("Glory").GetSingle(); }
        Assert.That(glory >= 6, Is.True, "특전 살 영광 확보");

        var st = Parse(g.PerkJson("senate"));   // 원로원 인맥 Lv1 = 뽑기 −15%
        Assert.That(st.TryGetProperty("error", out _), Is.False);
        Assert.That(st.GetProperty("GachaCost").GetSingle(), Is.EqualTo(85f), "뽑기 비용 100→85");
        Assert.That(st.GetProperty("Glory").GetSingle(), Is.EqualTo(glory - 6), "영광 소모");
        Assert.That(Parse(g.PerkJson("nope")).TryGetProperty("error", out _), Is.True);

        var g2 = new Game(1, 19, fresh: false, interactive: false, playerless: false);   // 재시작
        var p = Parse(g2.StateJson()).GetProperty("Perks").EnumerateArray().First(x => x.GetProperty("Id").GetString() == "senate");
        Assert.That(p.GetProperty("Lv").GetInt32(), Is.EqualTo(1), "특전 영속");
    }

    [Test]
    public void Game_Retire_HallEntry_PreseasonOnly()
    {
        TempDir("retire");
        var g = new Game(1, 43, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        string id = Parse(g.StateJson()).GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;

        // 신인(명성0·KO0)은 진로 자격 미달 → 자격 진로 택하면 거부
        Assert.That(Parse(g.RetireJson(id, "scout")).TryGetProperty("error", out _), Is.True, "스카우터 자격 미달 거부");
        Assert.That(Parse(g.RetireJson(id, "instructor")).TryGetProperty("error", out _), Is.True, "교관 자격 미달 거부");

        // 진로 없는 단순 은퇴는 폐지 — 방출(ReleaseJson)·해방(ManumitJson)으로 안내(에러 반환)
        Assert.That(Parse(g.RetireJson(id)).TryGetProperty("error", out _), Is.True, "진로 없는 은퇴는 거부");

        // 자격 충족(명성 40+) → 교관 진로 은퇴 성공 + 명전 등재(#11)
        DoctorWorld(j => { foreach (var f in j["Gladiators"]!.AsArray()) if ((bool)f!["IsPlayer"]!) f["Fame"] = 50f; });
        var g2 = new Game(1, 43, fresh: false, interactive: false, playerless: false);
        string id2 = Parse(g2.StateJson()).GetProperty("MyFighters")[0].GetProperty("Id").GetString()!;
        var st = Parse(g2.RetireJson(id2, "instructor"));
        Assert.That(st.TryGetProperty("error", out _), Is.False, "자격 충족 진로 은퇴 허용");
        Assert.That(st.GetProperty("MyFighters").GetArrayLength(), Is.EqualTo(0), "진로 은퇴로 로스터 제거");
        var hallEl = st.GetProperty("Season").GetProperty("Hall");
        int hallN = hallEl.ValueKind == JsonValueKind.Array ? hallEl.GetArrayLength() : 0;   // 비면 null 직렬화
        Assert.That(hallN, Is.EqualTo(1), "진로 은퇴자는 명전 등재(#11)");
    }

    [Test]
    public void Game_Bet_OnAiMatch_SettlesAndGuards()
    {
        TempDir("bet");
        var g = new Game(1, 65, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        // 다음이 AI 경기인 지점 찾기
        int guard = 0; JsonElement nm = default;
        while (guard++ < 40)
        {
            nm = Parse(g.StateJson()).GetProperty("NextMatch");
            if (nm.ValueKind != JsonValueKind.Null && !nm.GetProperty("IsPlayerMatch").GetBoolean()) break;
            g.PlayNext();
        }
        float gold0 = Parse(g.StateJson()).GetProperty("Gold").GetSingle();
        var r = Parse(g.BetJson(0, 20f));
        Assert.That(r.TryGetProperty("error", out _), Is.False, "AI 경기 베팅 성공");
        Assert.That(r.GetProperty("Gold").GetSingle(), Is.EqualTo(gold0 - 20f), "베팅금 차감");
        Assert.That(r.GetProperty("PendingBet").ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "베팅 상태 노출");
        Assert.That(Parse(g.BetJson(1, 20f)).TryGetProperty("error", out _), Is.True, "경기당 1회");

        g.PlayNext();   // 경기 진행 → 정산(적중/빗나감 스토리)
        var st = Parse(g.StateJson());
        Assert.That(st.GetProperty("PendingBet").ValueKind, Is.EqualTo(JsonValueKind.Null), "정산 후 소거");
        bool betStory = st.GetProperty("Season").GetProperty("Story").EnumerateArray()
            .Any(e => e.GetProperty("Kind").GetString() == "bet" &&
                 (e.GetProperty("Text").GetString()!.Contains("적중") || e.GetProperty("Text").GetString()!.Contains("빗나감")));
        Assert.That(betStory, Is.True, "정산 서사");
    }

    [Test]
    public void Game_SaveSlots_IsolatedWorlds()
    {
        TempDir("slots");
        var g1 = new Game(1, 61, fresh: true, interactive: false, playerless: false, worldPath: "world1.json");
        g1.RenameJson("ludus", "", "일번 검투소");
        var g2 = new Game(1, 62, fresh: true, interactive: false, playerless: false, worldPath: "world2.json");
        g2.RenameJson("ludus", "", "이번 검투소");

        Assert.That(File.Exists("world1.json"), Is.True);
        Assert.That(File.Exists("world2.json"), Is.True);
        // 슬롯 간 격리: 각자 다른 세계·이름 유지
        var r1 = new Game(1, 61, fresh: false, interactive: false, playerless: false, worldPath: "world1.json");
        var r2 = new Game(1, 62, fresh: false, interactive: false, playerless: false, worldPath: "world2.json");
        Assert.That(Parse(r1.StateJson()).GetProperty("LudusName").GetString(), Is.EqualTo("일번 검투소"));
        Assert.That(Parse(r2.StateJson()).GetProperty("LudusName").GetString(), Is.EqualTo("이번 검투소"));
    }

    [Test]
    public void Game_Calendar_ExposesFullSchedule()
    {
        TempDir("cal");
        var g = new Game(1, 63, fresh: true, interactive: false, playerless: true);
        g.PlayNext();   // 개막
        g.PlayNext(); g.PlayNext();   // 2경기
        var season = Parse(g.StateJson()).GetProperty("Season");
        var cal = season.GetProperty("Calendar");
        Assert.That(cal.GetArrayLength(), Is.GreaterThan(2), "전 일정(과거+미래) 노출");
        int played = 0, next = 0;
        foreach (var c in cal.EnumerateArray())
        {
            if (c.GetProperty("Winner").ValueKind != JsonValueKind.Null) played++;
            if (c.GetProperty("IsNext").GetBoolean()) next++;
            Assert.That(c.GetProperty("Month").GetString()!.Length, Is.GreaterThan(0), "로마 월 표기");
        }
        Assert.That(played, Is.EqualTo(2), "치른 경기 승자 표기");
        Assert.That(next, Is.EqualTo(1), "다음 경기 마커 1개");
        Assert.That(season.GetProperty("Auc").GetInt32(), Is.EqualTo(681), "AUC 연도");
    }

    [Test]
    public void Game_Transfers_ListAndGuards()
    {
        TempDir("transfer");
        var g = new Game(1, 57, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);

        var tr = Parse(g.TransfersJson());
        Assert.That(tr.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(tr.GetProperty("Buyables").GetArrayLength(), Is.EqualTo(3), "매물 3명");
        string bid = tr.GetProperty("Buyables")[0].GetProperty("Id").GetString()!;
        int price = tr.GetProperty("Buyables")[0].GetProperty("Price").GetInt32();
        Assert.That(price, Is.GreaterThan(100), "이적료는 뽑기보다 비싸다");
        Assert.That(Parse(g.TransferBuyJson(bid)).TryGetProperty("error", out _), Is.True, "잔고 부족 거부(시작 50)");

        g.PlayNext();   // 개막
        Assert.That(Parse(g.TransfersJson()).TryGetProperty("error", out _), Is.True, "시즌 중 시장 폐쇄");
        Assert.That(Parse(g.TransferBuyJson(bid)).TryGetProperty("error", out _), Is.True, "시즌 중 인수 금지");
    }

    [Test]
    public void Game_Edict_Rolls_Persists_ResolvesAtSeasonEnd()
    {
        TempDir("edict");
        var g = new Game(1, 55, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        // 특명이 뜨는 시즌 개막까지(75%/시즌)
        JsonElement ed = default; bool found = false; int guard = 0;
        while (guard++ < 8 && !found)
        {
            g.PlayNext();   // 개막
            ed = Parse(g.StateJson()).GetProperty("Edict");
            if (ed.ValueKind != JsonValueKind.Null) { found = true; break; }
            while (g.SeasonActive) g.PlayNext();
        }
        Assert.That(found, Is.True, "특명 발부");
        Assert.That(ed.GetProperty("Desc").GetString()!.Length, Is.GreaterThan(0));

        // 미드시즌 재시작에도 특명 유지(영속)
        var g2 = new Game(1, 55, fresh: false, interactive: false, playerless: false);
        Assert.That(Parse(g2.StateJson()).GetProperty("Edict").ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "특명 영속");

        // 시즌 종료 → 특명 해소(달성 보상 or 실패 벌) 후 소거
        while (g2.SeasonActive) g2.PlayNext();
        var st = Parse(g2.StateJson());
        Assert.That(st.GetProperty("Edict").ValueKind, Is.EqualTo(JsonValueKind.Null), "시즌말 특명 정산·소거");
        bool resolved = st.GetProperty("Season").GetProperty("Story").EnumerateArray()
            .Any(e => e.GetProperty("Kind").GetString() == "edict" &&
                 (e.GetProperty("Text").GetString()!.Contains("달성") || e.GetProperty("Text").GetString()!.Contains("실패")));
        Assert.That(resolved, Is.True, "달성/실패 서사 기록");
    }

    [Test]
    public void Game_Sparring_PreseasonOnly_NoRecord()
    {
        TempDir("spar");
        var g = new Game(1, 53, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        var f0 = Parse(g.StateJson()).GetProperty("MyFighters")[0];
        string id = f0.GetProperty("Id").GetString()!;

        var r = Parse(g.SparringJson(id));
        Assert.That(r.GetProperty("ok").GetBoolean(), Is.True, "프리시즌 스파링 가능");
        var f1 = Parse(g.StateJson()).GetProperty("MyFighters")[0];
        Assert.That(f1.GetProperty("CW").GetInt32() + f1.GetProperty("CL").GetInt32() + f1.GetProperty("CD").GetInt32(),
            Is.EqualTo(0), "무기록(통산 불변)");
        Assert.That(f1.GetProperty("Fatigue").GetInt32(), Is.EqualTo(3), "가벼운 피로만");

        g.PlayNext();   // 개막
        Assert.That(Parse(g.SparringJson(id)).GetProperty("ok").GetBoolean(), Is.True, "시즌 중에도 스파링 가능(#3)");
    }

    [Test]
    public void Game_LiveMatch_SwitchTactic_SettleMatchesWatched()
    {
        TempDir("live");
        var g = new Game(1, 51, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        // 내 경기까지 전진
        JsonElement nm = default; int guard = 0;
        while (guard++ < 60)
        {
            nm = Parse(g.StateJson()).GetProperty("NextMatch");
            if (nm.ValueKind != JsonValueKind.Null && nm.GetProperty("IsPlayerMatch").GetBoolean()) break;
            g.PlayNext();
        }
        int before = Parse(g.StateJson()).GetProperty("Season").GetProperty("Matches").GetInt32();

        var live = Parse(g.LiveBeginJson(null));
        Assert.That(live.GetProperty("ok").GetBoolean(), Is.True, "라이브 시작(잠정 시뮬)");
        Assert.That(Parse(g.StateJson()).GetProperty("Season").GetProperty("Matches").GetInt32(),
            Is.EqualTo(before), "라이브 중 커서 무전진(세이브 안전)");

        var alts = nm.GetProperty("MyPool").EnumerateArray().Select(x => x.GetString()!)
            .Where(t => t != nm.GetProperty("MyTactic").GetString()).ToArray();
        string alt = alts[0], alt2 = alts.Length > 1 ? alts[1] : alts[0];
        // 같은 전술 재선택은 기회 미차감(#12): 현재 전술을 다시 골라도 remaining 불변
        Assert.That(Parse(g.LiveSwitchJson(5f, nm.GetProperty("MyTactic").GetString()!)).GetProperty("remaining").GetInt32(), Is.EqualTo(2), "현재 전술 재선택 미차감");
        Assert.That(Parse(g.LiveSwitchJson(10f, alt)).GetProperty("remaining").GetInt32(), Is.EqualTo(1), "전환 1회 소모");
        Assert.That(Parse(g.LiveSwitchJson(15f, alt)).GetProperty("remaining").GetInt32(), Is.EqualTo(1), "같은 전술 재선택 미차감");
        Assert.That(Parse(g.LiveSwitchJson(30f, alt2)).GetProperty("remaining").GetInt32(), Is.EqualTo(0), "다른 전술 전환 2회 소모");
        Assert.That(Parse(g.LiveSwitchJson(50f, alt)).TryGetProperty("error", out _), Is.True, "3회째 거부");

        var settle = Parse(g.LiveSettleJson());
        Assert.That(settle.GetProperty("Winner").GetString()!.Length, Is.GreaterThan(0), "정산 완료");
        Assert.That(Parse(g.StateJson()).GetProperty("Season").GetProperty("Matches").GetInt32(),
            Is.EqualTo(before + 1), "정산 시 커서 전진");
        Assert.That(Parse(g.LiveSettleJson()).TryGetProperty("error", out _), Is.True, "중복 정산 거부");
    }

    [Test]
    public void Game_MidseasonRecruit_JoinsRemainingRounds()
    {
        TempDir("midjoin");
        var g = new Game(1, 47, fresh: true, interactive: false, playerless: false);
        g.PlayNext();   // 개막 (선수 0명이어도 AI 리그 진행)
        g.PlayNext();   // 1경기 소화
        int before = Parse(g.StateJson()).GetProperty("Season").GetProperty("TotalMatches").GetInt32();
        g.GachaJson();
        var st = Parse(g.RecruitJson(0));   // 시즌 중 영입 → 중도 투입
        int after = st.GetProperty("Season").GetProperty("TotalMatches").GetInt32();
        Assert.That(after, Is.GreaterThan(before), "잔여 라운드에 합류전 편성");
        bool joinStory = st.GetProperty("Season").GetProperty("Story").EnumerateArray()
            .Any(e => e.GetProperty("Text").GetString()!.Contains("중도 투입"));
        Assert.That(joinStory, Is.True, "중도 투입 서사");
    }

    [Test]
    public void Game_MatchFixing_ResolvesOnFixedFighterMatch()
    {
        // 여러 시드 × 여러 시즌에 걸쳐 '승부조작' 텍스트 이벤트가 뜨는 세계에서 상태머신을 검증한다(스폰 22% × 1/N).
        bool covered = false;
        for (int seed = 1; seed <= 12 && !covered; seed++)
        {
            TempDir("fix" + seed);
            var g = new Game(1, (ulong)seed, fresh: true, interactive: false, playerless: false);
            g.GachaJson(); g.RecruitJson(0);

            int guard = 0;
            while (guard++ < 400 && !covered)
            {
                var st = Parse(g.StateJson());
                if (st.GetProperty("MyFighters").GetArrayLength() == 0) break;   // 선수 전멸 → 다음 시드
                // 대기 이벤트 처리: 승부조작이면 가담·검증, 아니면 소극 선택으로 치워 새 이벤트가 스폰되게(안 치우면 잠김)
                if (st.TryGetProperty("PendingEvent", out var ev) && ev.ValueKind != JsonValueKind.Null)
                {
                    if (ev.GetProperty("Id").GetString() == "fix")
                    {
                        string myName = st.GetProperty("MyFighters")[0].GetProperty("Name").GetString()!;
                        g.ChooseEventJson(0);   // 가담 — 선입금 없이 예약
                        Assert.That(Parse(g.StateJson()).GetProperty("FixTarget").GetString(), Is.EqualTo(myName),
                            "가담 시 승부조작 대상 노출");
                        int g2 = 0;
                        while (g2++ < 300)
                        {
                            var s2 = Parse(g.StateJson());
                            bool pending = s2.TryGetProperty("FixTarget", out var ft) && ft.ValueKind != JsonValueKind.Null;
                            if (!pending) break;
                            if (s2.GetProperty("MyFighters").GetArrayLength() == 0) break;
                            if (s2.TryGetProperty("PendingEvent", out var ev2) && ev2.ValueKind != JsonValueKind.Null)
                                g.ChooseEventJson(1);   // 정산 진행 중 다른 이벤트가 겹치면 치운다
                            g.PlayNext();
                        }
                        bool cleared = !Parse(g.StateJson()).TryGetProperty("FixTarget", out var ftf) || ftf.ValueKind == JsonValueKind.Null;
                        Assert.That(cleared, Is.True, "가담 선수 경기 후(또는 시즌말) 예약 해제");
                        covered = true; break;
                    }
                    g.ChooseEventJson(1);   // 다른 이벤트 = 소극 선택으로 해소(잠금 방지)
                    continue;
                }
                g.PlayNext();   // 개막·경기·다음 시즌 개막까지 자동 진행
            }
        }
        Assert.That(covered, Is.True, "시드×시즌 순회 중 승부조작 이벤트가 최소 한 번은 떠 상태머신을 검증");
    }

    [Test]
    public void SkillGen_GrantsInnateSkills_RespectingGates()
    {
        // 스킬은 수련이 아니라 생성 시 부여된다([7] 개정). 게이트는 유지 —
        // 액티브=무기 일치 / 패시브=성격 일치 / Ⅱ급=집정관 이상.
        var rng = new Morituri.Sim.Core.SimRandom(20260719);
        int total = 0, tier2BelowConsul = 0, maxCount = 0;
        var counts = new Dictionary<int, int>();

        foreach (var talent in new[] { TalentGrade.Slave, TalentGrade.Fighter, TalentGrade.Champion,
                                       TalentGrade.Consul, TalentGrade.Immortal, TalentGrade.Caesar })
            for (int i = 0; i < 500; i++)
            {
                var ids = SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", talent);
                total += ids.Length;
                maxCount = Math.Max(maxCount, ids.Length);
                counts[ids.Length] = counts.GetValueOrDefault(ids.Length) + 1;
                foreach (var id in ids)
                {
                    var sk = SkillTable.Get(id);
                    // 자격: 액티브는 그 무기, 패시브는 그 성격
                    if (sk.GateWeapon != null)
                        Assert.That(sk.GateWeapon, Is.EqualTo("WPN_SWORD"), "액티브 무기 게이트");
                    else
                        Assert.That(sk.GatePersonality, Is.EqualTo("PER_CALM"), "패시브 성격 게이트");
                    if (sk.RankTier >= 2 && (int)talent < SkillTable.Tier2MinTalent) tier2BelowConsul++;
                }
                Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length), "중복 부여 없음");
            }

        Assert.That(tier2BelowConsul, Is.EqualTo(0), "집정관 미만은 Ⅱ급을 타고나지 않는다");
        Assert.That(total, Is.GreaterThan(0), "부여가 일어난다");
        Assert.That(counts.ContainsKey(0), Is.True, "0개인 자도 나온다(슬롯 강제 아님)");
        Assert.That(maxCount, Is.GreaterThan(1), "여럿 지닌 자도 나온다");
    }

    [Test]
    public void Passives_TwoOwned_BothEvaluated()
    {
        // 패시브 다중화: 냉철의 Ⅰ급(침착)·Ⅱ급(전장 분석)을 함께 지니면 둘 다 살아 있어야 한다.
        // 전장 분석은 Periodic(8s마다 proc) — 두 개를 다 얹어도 proc 라벨이 방출되는지로 검증.
        var both = new[] { "SKL_COMPOSE", "SKL_READ" };
        var a = new FighterDef("둘다", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM")
                { TraitIds = both };
        var b = new FighterDef("상대", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_RECKLESS");
        var ev = new List<Morituri.Sim.Events.SimEvent>();
        new MatchSim().Run(a, b, 4242, ev, null);
        bool readProc = ev.OfType<Morituri.Sim.Events.Decision>()
                          .Any(d => d.FighterId == 0 && d.ReasonTag == "PASV_READ");
        Assert.That(readProc, Is.True, "Ⅱ급 패시브가 함께 있어도 평가된다");

        // 하나만 지녔을 때와 비교 — 단일 보유도 여전히 동작(회귀 방지)
        var onlyRead = a with { TraitIds = new[] { "SKL_READ" } };
        var ev2 = new List<Morituri.Sim.Events.SimEvent>();
        new MatchSim().Run(onlyRead, b, 4242, ev2, null);
        Assert.That(ev2.OfType<Morituri.Sim.Events.Decision>()
                       .Any(d => d.FighterId == 0 && d.ReasonTag == "PASV_READ"), Is.True, "단일 보유 회귀 없음");
    }

    [Test]
    public void SkillGen_CountDistribution_ScalesWithTalent()
    {
        // 라니스타 확정 규칙: 개수 상한 없음, 확률만 천부 비례.
        // 노예는 최대 2(Ⅰ급 액티브·패시브) / 집정관+는 최대 4까지 나오되 0개도 가능해야 한다.
        var rng = new Morituri.Sim.Core.SimRandom(31337);
        int slaveMax = 0, consulMax = 0; bool consulZero = false, slaveTwo = false;
        double slaveAvg = 0, caesarAvg = 0;
        for (int i = 0; i < 3000; i++)
        {
            int sn = SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", TalentGrade.Slave).Length;
            int cn = SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", TalentGrade.Consul).Length;
            caesarAvg += SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", TalentGrade.Caesar).Length;
            slaveAvg += sn;
            slaveMax = Math.Max(slaveMax, sn); consulMax = Math.Max(consulMax, cn);
            if (cn == 0) consulZero = true;
            if (sn == 2) slaveTwo = true;
        }
        Assert.That(slaveMax, Is.EqualTo(2), "노예는 Ⅰ급 둘까지(액티브·패시브)");
        Assert.That(slaveTwo, Is.True, "노예도 2개를 타고날 수 있다");
        Assert.That(consulMax, Is.EqualTo(4), "집정관은 최대 4개(액티브 2 + 패시브 2)");
        Assert.That(consulZero, Is.True, "집정관도 0개일 수 있다");
        Assert.That(caesarAvg / 3000.0, Is.GreaterThan(slaveAvg / 3000.0), "천부가 높을수록 평균 개수가 많다");
    }

    [Test]
    public void ConditionalTraits_OnlyGrantedWhenMeaningful()
    {
        // 라니스타 규칙: 의미 있을 때만 부여한다.
        var rng = new Morituri.Sim.Core.SimRandom(555);
        // ① 집정관 이상에게는 사생아가 붙지 않는다(이미 Ⅱ급을 담을 수 있어 무의미)
        foreach (var hi in new[] { TalentGrade.Consul, TalentGrade.Immortal, TalentGrade.Caesar })
            for (int i = 0; i < 800; i++)
                Assert.That(TraitGen.Roll(rng, hi).Contains(TraitTable.Bastard), Is.False, $"{hi}에 사생아 없음");
        // 집정관 미만에는 여전히 나온다
        bool sawLow = false;
        for (int i = 0; i < 2000 && !sawLow; i++)
            sawLow = TraitGen.Roll(rng, TalentGrade.Slave).Contains(TraitTable.Bastard);
        Assert.That(sawLow, Is.True, "노예에겐 사생아가 나온다");

        // ② 사생아를 지니면 반드시 Ⅱ급 스킬을 하나 이상 타고난다
        for (int i = 0; i < 500; i++)
        {
            var sk = SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", TalentGrade.Slave, bastard: true);
            Assert.That(sk.Any(id => SkillTable.Get(id).RankTier >= 2), Is.True, "사생아 = Ⅱ급 보장");
        }

        // ③ 액티브가 없으면 빠른손은 떨어져 나간다
        var swiftOnly = new[] { "TRT_SWIFT", "TRT_FLEET" };
        Assert.That(SkillGen.ReconcileTraits(swiftOnly, new[] { "SKL_COMPOSE" }).Contains("TRT_SWIFT"),
                    Is.False, "패시브만 있으면 빠른손 제거");
        Assert.That(SkillGen.ReconcileTraits(swiftOnly, System.Array.Empty<string>()).Contains("TRT_SWIFT"),
                    Is.False, "스킬이 없으면 빠른손 제거");
        Assert.That(SkillGen.ReconcileTraits(swiftOnly, new[] { "SKL_COMBO" }).Contains("TRT_SWIFT"),
                    Is.True, "액티브가 있으면 빠른손 유지");
        Assert.That(SkillGen.ReconcileTraits(swiftOnly, new[] { "SKL_COMBO" }).Contains("TRT_FLEET"),
                    Is.True, "다른 특성은 건드리지 않는다");
    }

    [Test]
    public void SkillGen_Bastard_BreaksTier2Ceiling()
    {
        // 사생아([7]§6.2): 천부 등급을 넘는 Ⅱ급 스킬을 지닌다 — 계급 천장 예외
        var rng = new Morituri.Sim.Core.SimRandom(777);
        bool sawTier2 = false;
        for (int i = 0; i < 2000 && !sawTier2; i++)
            sawTier2 = SkillGen.Roll(rng, "WPN_SWORD", "PER_CALM", TalentGrade.Slave, bastard: true)
                               .Any(id => SkillTable.Get(id).RankTier >= 2);
        Assert.That(sawTier2, Is.True, "사생아 노예도 Ⅱ급을 타고날 수 있다");

        // 사생아가 아니면 노예는 절대 Ⅱ급을 못 받는다
        var rng2 = new Morituri.Sim.Core.SimRandom(777);
        for (int i = 0; i < 2000; i++)
            Assert.That(SkillGen.Roll(rng2, "WPN_SWORD", "PER_CALM", TalentGrade.Slave)
                                .Any(id => SkillTable.Get(id).RankTier >= 2), Is.False, "천장 유지");
    }

    [Test]
    public void Game_Mastery_SpendsTrainingPoints_AndPersists()
    {
        TempDir("mastery");
        var g = new Game(1, 41, fresh: true, interactive: false, playerless: false);
        g.GachaJson(); g.RecruitJson(0);
        g.PlayNext();   // 개막
        // 훈련 포인트가 쌓일 때까지 진행(3경기 주기)
        string id = ""; int pts = 0, guard = 0;
        while (guard++ < 200 && pts < 1)
        {
            g.PlayNext();
            var f = Parse(g.StateJson()).GetProperty("MyFighters")[0];
            id = f.GetProperty("Id").GetString()!; pts = f.GetProperty("TrainingPoints").GetInt32();
        }
        Assert.That(pts, Is.GreaterThan(0), "훈련 포인트 획득");

        var after = Parse(g.MasteryJson(id, "grit"));
        var f2 = after.GetProperty("MyFighters")[0];
        Assert.That(f2.GetProperty("MGrit").GetInt32(), Is.EqualTo(1), "투혼 Lv1");
        Assert.That(f2.GetProperty("TrainingPoints").GetInt32(), Is.EqualTo(pts - 1), "포인트 1 소모(Lv0→1)");
        Assert.That(Parse(g.MasteryJson(id, "nope")).TryGetProperty("error", out _), Is.True);

        var g2 = new Game(1, 41, fresh: false, interactive: false, playerless: false);   // 재시작
        Assert.That(Parse(g2.StateJson()).GetProperty("MyFighters")[0].GetProperty("MGrit").GetInt32(), Is.EqualTo(1), "마스터리 영속");
    }

    [Test]
    public void Game_DramaticFates_OccurRarely_LeagueSurvives()
    {
        TempDir("fates");
        var g = new Game(1, 33, fresh: true, interactive: false, playerless: true);
        var fateKinds = new HashSet<string>();
        for (int s = 0; s < 40; s++)
        {
            RunFullSeason(g);
            var story = Parse(g.StateJson()).GetProperty("Season").GetProperty("Story");
            foreach (var e in story.EnumerateArray())
            {
                string k = e.GetProperty("Kind").GetString()!;
                if (k is "death" or "grave_injury" or "awakening" or "persona" or "tradeoff") fateKinds.Add(k);
            }
        }
        Assert.That(fateKinds.Count, Is.GreaterThan(1), $"40시즌 동안 극적 운명 다종 발생 (발생: {string.Join(",", fateKinds)})");
        // 사망·교체에도 리그는 12명 유지(공석 승계)
        var fs = Parse(g.StateJson()).GetProperty("Season").GetProperty("Fighters");
        Assert.That(fs.GetArrayLength(), Is.EqualTo(12), "사망·방출·은퇴에도 리그 인원 유지");
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

        Assert.That(f1.Count, Is.EqualTo(12)); Assert.That(f2.Count, Is.EqualTo(12));
        Assert.That(string.Join(",", f1), Is.Not.EqualTo(string.Join(",", f2)), "worldSeed마다 다른 캐스트(24인 풀 선발)");

        // 루두스는 이제 6종 전부 활성(대항전 구도) — 세계마다 캐스트 소속만 달라진다
        var l1 = Parse(g1.StateJson()).GetProperty("LudusTable").EnumerateArray().Select(x => x.GetProperty("Name").GetString()).OrderBy(x => x).ToList();
        Assert.That(l1.Count, Is.EqualTo(6), "라이벌 루두스 6곳 전부 활성");
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
        // 극적 운명(사망 등)으로 로스터가 빌 수 있다 — 재영입 후 시즌을 더 돌려 시드 무관하게 지속
        int rguard = 0;
        while (st.GetProperty("MyFighters").GetArrayLength() == 0 && rguard++ < 6)
        {
            g.GachaJson(); g.RecruitJson(0);
            RunFullSeason(g);
            st = Parse(g.StateJson());
        }
        float glory = st.GetProperty("Glory").GetSingle();
        Assert.That(glory, Is.GreaterThan(0f), "위신 업적/타이틀로 영광 획득");

        // 돌파 비용(상한/40)만큼 모일 때까지 시즌을 더 돈다 — 시드에 따라 적립 속도가 달라 고정 시즌 수는 취약
        int gguard = 0;
        while (gguard++ < 8)
        {
            var cur = Parse(g.StateJson());
            if (cur.GetProperty("MyFighters").GetArrayLength() == 0) { g.GachaJson(); g.RecruitJson(0); RunFullSeason(g); continue; }
            float need = MathF.Ceiling(cur.GetProperty("MyFighters")[0].GetProperty("PotentialBudget").GetSingle() / 40f);
            if (cur.GetProperty("Glory").GetSingle() >= need) break;
            RunFullSeason(g);
        }
        st = Parse(g.StateJson());
        glory = st.GetProperty("Glory").GetSingle();

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
    public void Game_Divisions_ChampionNeverRelegated_SwapByStandings()
    {
        TempDir("div");
        var g = new Game(1, 17, fresh: true, interactive: false, playerless: true);
        bool sawPromote = false;
        for (int s = 0; s < 5; s++)
        {
            RunFullSeason(g);
            var st = Parse(g.StateJson());
            string champ = st.GetProperty("LastSeason").GetProperty("Champion").GetString()!;
            foreach (var e in st.GetProperty("Season").GetProperty("Story").EnumerateArray())
            {
                if (e.GetProperty("Kind").GetString() == "promote") sawPromote = true;
                // 챔피언은 강등 서사에 절대 등장하지 않는다 (성적 스왑 — 1부 1위)
                if (e.GetProperty("Kind").GetString() == "relegate")
                    Assert.That(e.GetProperty("Text").GetString()!.Contains(champ), Is.False, "챔피언 강등 금지");
            }
            g.PlayNext();   // 다음 시즌 개막 — 챔피언(생존 시)은 여전히 1부
            var fs = Parse(g.StateJson()).GetProperty("Season").GetProperty("Fighters");
            foreach (var f in fs.EnumerateArray())
                if (f.GetProperty("Name").GetString() == champ)
                    Assert.That(f.GetProperty("Division").GetInt32(), Is.EqualTo(1), "전 시즌 챔피언은 1부 유지");
        }
        Assert.That(sawPromote, Is.True, "성적 기반 승격 발생");
    }

    [Test]
    public void Game_RivalLudi_CompeteRankAndPersist()
    {
        TempDir("rival");
        var g = new Game(1, 7, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 3; s++) RunFullSeason(g);
        var lt = Parse(g.StateJson()).GetProperty("LudusTable");
        Assert.That(lt.GetArrayLength(), Is.EqualTo(6), "playerless = 라이벌 루두스 6곳 전부 활성");
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
        // 25시즌 연속 — 노쇠 AI(노화+6시즌 = 36~42세)는 은퇴(명전)하고 신인이 와 리그는 12명 유지.
        TempDir("aging");
        var g = new Game(1, 11, fresh: true, interactive: false, playerless: true);
        for (int s = 0; s < 25; s++) RunFullSeason(g);

        var season = Parse(g.StateJson()).GetProperty("Season");
        Assert.That(season.GetProperty("Fighters").GetArrayLength(), Is.EqualTo(12), "세대교체로 리그 12명 유지");
        Assert.That(season.GetProperty("Champions").GetArrayLength(), Is.EqualTo(25), "역대 챔피언 25명 기록");
        Assert.That(season.GetProperty("Hall").GetArrayLength(), Is.GreaterThan(0), "은퇴자(명예의 전당) 발생");
    }

    [Test]
    public void Game_Story_Campaign_ProloguesToChronicle()
    {
        // [13a] 캠페인 상태머신: 장례(S0) → 빈 막사(S1) → [첫 영입] → 첫 훈련(S3) → 의무실(S4) → 개막 전야(S5)
        //   → 1막 비트 → 종막(승격 or 시즌3 소프트 종료) = chronicle.
        // 무레나는 서막에 오지 않는다(v0.3) — 서막의 압박은 얼굴이 아니라 숫자(장부)가 담당한다.
        TempDir("story");
        var g = new Game(1, 77, fresh: true, interactive: false, playerless: false);
        var st0 = Parse(g.StateJson());
        Assert.That(st0.GetProperty("Campaign").GetProperty("Stage").GetString(), Is.EqualTo("prologue"), "새 커리어 = 서막");
        Assert.That(st0.GetProperty("PendingEvent").GetProperty("Id").GetString(), Is.EqualTo("story_s0"), "장례(S0)로 개막");
        Assert.That(st0.GetProperty("Legends").GetArrayLength(), Is.EqualTo(4), "창세 전설 4명 시드");
        Assert.That(st0.GetProperty("Campaign").GetProperty("Hint").ValueKind, Is.EqualTo(JsonValueKind.String), "카토의 조언(튜토리얼 힌트)");
        g.ChooseEventJson(1);   // S0 해소
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").GetProperty("Id").GetString(),
            Is.EqualTo("story_s1"), "장례 → 빈 막사(S1) · 영입 전에도 발화");
        g.ChooseEventJson(0);   // S1 「도끼는 누구 것인지 묻는다」 — 조각 1
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").GetProperty("Id").GetString(),
            Is.EqualTo("story_s2"), "빈 막사 → 궤(S2) · 도끼와 장부는 택일이 아니다");
        g.ChooseEventJson(0);   // S2 「장부를 연다」 — 조각 2
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").ValueKind, Is.EqualTo(JsonValueKind.Null),
            "영입 전에는 S3가 오지 않는다(서막은 영입을 기다린다)");
        g.GachaJson(); g.RecruitJson(0);
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").GetProperty("Id").GetString(),
            Is.EqualTo("story_s3"), "첫 영입 → 첫 훈련(S3)");
        g.ChooseEventJson(1);
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").GetProperty("Id").GetString(),
            Is.EqualTo("story_s4"), "첫 훈련 → 의무실(S4) · 테아 등장");
        g.ChooseEventJson(1);
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").GetProperty("Id").GetString(),
            Is.EqualTo("story_s5"), "의무실 → 개막 전야(S5)");
        g.ChooseEventJson(1);   // 조각 7(카토의 회상 ①)
        // 서막 전체에 무레나가 등장하지 않았는지 — 조작 예약이 걸려 있으면 안 된다
        Assert.That(Parse(g.StateJson()).GetProperty("Keepsakes").ValueKind, Is.EqualTo(JsonValueKind.Array),
            "서막에서 보관함에 유품이 편철된다");

        // 카토 코멘터리: 내 경기는 항상 한 줄 평
        var m = g.PlayNext();   // 개막
        int mg = 0;
        while (mg++ < 120 && !m.WasPlayerMatch) m = g.PlayNext();
        Assert.That(m.WasPlayerMatch, Is.True, "내 경기 도달");
        Assert.That(string.IsNullOrEmpty(m.Cato), Is.False, "내 경기 = 카토의 한 줄 평");

        // 캠페인 완주(이벤트는 소극 선택으로 해소) — 늦어도 시즌 3 종료 시 소프트 종막
        var seen = new HashSet<string>(); int guard = 0;
        while (guard++ < 900)
        {
            var st = Parse(g.StateJson());
            if (st.GetProperty("PendingEvent").ValueKind != JsonValueKind.Null)
            {
                string id = st.GetProperty("PendingEvent").GetProperty("Id").GetString()!;
                if (id.StartsWith("story_")) seen.Add(id);
                g.ChooseEventJson(1);
                continue;
            }
            if (st.GetProperty("Campaign").GetProperty("Stage").GetString() == "chronicle") break;
            g.PlayNext();
        }
        var fin = Parse(g.StateJson());
        Assert.That(fin.GetProperty("Campaign").GetProperty("Stage").GetString(), Is.EqualTo("chronicle"), "종막 도달");
        Assert.That(seen.Contains("story_finale"), Is.True, "라니스타가 되는 의식(종막)");
        Assert.That(seen.Any(x => x.StartsWith("story_house_")), Is.True, "세 가문 비트 발화");
        // [13a] 1막 「빚」 — 무레나 2단 등장: 채권자(A3) → 유혹자(A5). 개막(A0)이 그 앞에 선다.
        Assert.That(seen.Contains("story_a0"), Is.True, "1막 A0 개막 발화");
        Assert.That(seen.Contains("story_a3"), Is.True, "1막 A3 무레나 첫 방문(돈만)");
        Assert.That(seen.Contains("story_a5"), Is.True, "1막 A5 조작 최초 제안");
        Assert.That(fin.GetProperty("Campaign").GetProperty("Clues").ValueKind, Is.EqualTo(JsonValueKind.Array),
            "기억의 벽 — 조각 배열 노출");
        Assert.That(fin.GetProperty("Legends").EnumerateArray().Any(l => l.GetProperty("Name").GetString() == "오르쿠스"),
            Is.True, "캠페인 커리어에는 오르쿠스가 반드시 창세 전설로 존재(척추 고정 시드)");
        int unrest = fin.GetProperty("Unrest").GetProperty("Level").GetInt32();
        Assert.That(unrest is >= 0 and <= 100, Is.True, "반란 지수 0~100 클램프");
        // 재로드 영속
        var g2 = new Game(1, 77, fresh: false, interactive: false, playerless: false);
        Assert.That(Parse(g2.StateJson()).GetProperty("Campaign").GetProperty("Stage").GetString(),
            Is.EqualTo("chronicle"), "스토리 상태 영속");
        Assert.That(Parse(g2.StateJson()).GetProperty("Legends").GetArrayLength(), Is.GreaterThan(3), "전설 카탈로그 영속");
    }

    [Test]
    public void Game_Story_SkipCampaign_UnlocksImmediately()
    {
        // [13] 각본 없이 시작(뉴게임 옵션) — 스토리 이벤트 제거·chronicle 즉시, 이후 정상 진행.
        TempDir("storyskip");
        var g = new Game(1, 88, fresh: true, interactive: false, playerless: false);
        var st = Parse(g.SkipCampaignJson());
        Assert.That(st.GetProperty("Campaign").GetProperty("Stage").GetString(), Is.EqualTo("chronicle"), "즉시 chronicle");
        Assert.That(st.GetProperty("PendingEvent").ValueKind, Is.EqualTo(JsonValueKind.Null), "대기 스토리 이벤트 제거");
        g.GachaJson(); g.RecruitJson(0);
        Assert.That(Parse(g.StateJson()).GetProperty("PendingEvent").ValueKind, Is.EqualTo(JsonValueKind.Null), "S5 미발화(각본 꺼짐)");
        RunFullSeason(g);
        Assert.That(Parse(g.StateJson()).GetProperty("LastSeason").ValueKind, Is.Not.EqualTo(JsonValueKind.Null), "캠페인 없이 시즌 정상 완주");
    }

    /// <summary>세계 파일을 직접 교정해 후일담 게이트 조건을 만든다(명성·비트·총애·컵은 정상 플레이로는 수십 시즌).</summary>
    private static void DoctorWorld(Action<JsonNode> mutate)
    {
        var json = JsonNode.Parse(File.ReadAllText("world.json"))!;
        mutate(json);
        File.WriteAllText("world.json", json.ToJsonString());
    }

    /// <summary>내 경기를 진행하며 특정 스토리 이벤트를 기다린다. 다른 이벤트는 소극(1) 선택으로 치움.
    /// 선수가 전멸하면(극적 운명 사망) 재영입해 내 경기 페이싱을 유지한다(골드는 테스트가 넉넉히 교정).</summary>
    private static bool PlayUntilStory(Game g, string wantId, int maxSteps = 200)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            var st = Parse(g.StateJson());
            if (st.GetProperty("PendingEvent").ValueKind != JsonValueKind.Null)
            {
                if (st.GetProperty("PendingEvent").GetProperty("Id").GetString() == wantId) return true;
                g.ChooseEventJson(1);
                continue;
            }
            if (st.GetProperty("MyFighters").GetArrayLength() == 0) { g.GachaJson(); g.RecruitJson(0); }
            g.PlayNext();   // 개막·경기·다음 시즌 개막 자동
        }
        return false;
    }

    /// <summary>[13a] 서막~1막을 지정한 성향으로 통과시킨다. warm=인간적 선택 / cold=냉혹한 선택.
    /// 자백(B7)까지 끌고 가서 그 본문을 돌려준다.</summary>
    private static string? RunToConfession(Game g, bool warm)
    {
        var pick = new Dictionary<string, int> {
            ["story_s3"] = warm ? 1 : 0,          // 아이의 말을 듣는다 / 카토의 방식
            ["story_s4"] = warm ? 0 : 1,          // 의무실 유지 / 폐쇄
            ["story_a5"] = warm ? 1 : 0,          // 증서를 밀어낸다 / 조작 수락
            ["story_a6"] = warm ? 0 : 2,          // 진실을 말한다 / 침묵
            ["story_b_blood"] = warm ? 0 : 1,     // 쉬게 한다 / 내보낸다
            ["story_b_monster"] = warm ? 0 : 1,   // 쉬게 한다 / 버틴다
            ["story_b_wall"] = warm ? 2 : 0,      // 왜 그만뒀는지 묻는다 / 본보기로 벌한다
            ["story_b_exec"] = warm ? 1 : 0,      // 처형전 거절 / 수락
        };
        for (int i = 0; i < 400; i++)
        {
            var st = Parse(g.StateJson());
            var pe = st.GetProperty("PendingEvent");
            if (pe.ValueKind != JsonValueKind.Null)
            {
                string id = pe.GetProperty("Id").GetString()!;
                if (id == "story_b_confess") return pe.GetProperty("Body").GetString();
                g.ChooseEventJson(pick.TryGetValue(id, out int c) ? c : 0);
                continue;
            }
            if (st.GetProperty("MyFighters").GetArrayLength() < 2) { g.GachaJson(); g.RecruitJson(0); }
            if (st.GetProperty("Campaign").GetProperty("Stage").GetString() == "chronicle") return null;
            g.PlayNext();
        }
        return null;
    }

    [Test]
    public void Game_Story_Confession_EthosSplitsTone_AndExileSwapsVoice()
    {
        // [13a] B7 「20년」 — 사실관계는 하나지만 태도가 셋으로 갈린다(자수/보고/고백).
        // 그리고 카토를 내치면 경기평의 화자가 바뀐다 — 페널티는 수치가 아니라 어휘의 상실이다.
        TempDir("confess_warm");
        var gw = new Game(1, 401, fresh: true, interactive: false, playerless: false);
        string? warm = RunToConfession(gw, warm: true);
        Assert.That(warm != null, Is.True, "인간 루트에서 자백 도달");
        Assert.That(warm!.Contains("앉자마자 고개를 숙였다"), Is.True, "인간 = 고백(용서받을까 봐 겁내며 말한다)");
        Assert.That(warm.Contains("쇳소리"), Is.True, "폐 천공 — 카토가 눈치챈 계기");
        Assert.That(warm.Contains("탑방패"), Is.True, "안 죽일 자를 붙이는 대진 = 살해 방식");

        TempDir("confess_cold");
        var gc = new Game(1, 401, fresh: true, interactive: false, playerless: false);
        string? cold = RunToConfession(gc, warm: false);
        Assert.That(cold != null, Is.True, "냉혹 루트에서 자백 도달");
        Assert.That(cold!.Contains("나갈 수 있는 거리를 재고"), Is.True, "냉혹 = 자수(처분받는 게 마땅하다는 확신)");
        Assert.That(cold.Contains("그를 죽인 자들이 떠오릅니다"), Is.True, "냉혹 전용 결말부");
        // 사실관계는 태도와 무관하게 동일해야 한다
        foreach (var fact in new[] { "오르쿠스", "무레나", "132승" })
            Assert.That(warm.Contains(fact) && cold.Contains(fact), Is.True, $"사실 「{fact}」는 3태 공통");

        // 처분 — 「나가라」 = 화자 교체
        gc.ChooseEventJson(0);
        var after = Parse(gc.StateJson());
        Assert.That(after.GetProperty("Campaign").GetProperty("Voice").GetString(), Is.EqualTo("exiled"),
            "카토를 내치면 경기평 화자가 바뀐다");
        Assert.That(after.GetProperty("Keepsakes").EnumerateArray()
            .Any(k => k.GetProperty("Title").GetString()!.Contains("자백")), Is.True,
            "처분과 무관하게 진실은 보관함에 남는다");
    }

    [Test]
    public void Game_Story_EmperorArc_GatesAndOrder()
    {
        // [13] 후일담 「황제의 게임」: 종막(finale)을 본 커리어 한정, E1(4단계) → E2(특명/총애/컵) → E3(6단계/컵).
        TempDir("earc");
        var g = new Game(1, 91, fresh: true, interactive: false, playerless: false);
        g.ChooseEventJson(1);                       // S0 해소
        g.GachaJson(); g.RecruitJson(0);
        g.ChooseEventJson(1);                       // S5 해소(거절)

        // 0) 명성 4단계여도 종막 없인 후일담 없음(구세이브·스킵 보호). 골드는 재영입용으로 넉넉히.
        DoctorWorld(j => { j["LudusRep"] = 900f; j["Gold"] = 5000f; j["StoryStage"] = "chronicle";
                           j["StoryBeats"] = new JsonArray("s0", "s5", "skipped"); });
        var g0 = new Game(1, 91, fresh: false, interactive: false, playerless: false);
        Assert.That(PlayUntilStory(g0, "story_e1", 60), Is.False, "종막 없는 커리어엔 E1이 안 뜬다");

        // 1) 종막 이수 + 명성 4단계 → E1
        DoctorWorld(j => j["StoryBeats"] = new JsonArray("s0", "s5", "finale"));
        var g1 = new Game(1, 91, fresh: false, interactive: false, playerless: false);
        Assert.That(PlayUntilStory(g1, "story_e1"), Is.True, "명문 루두스(4단계) → E1 총애의 초대");
        g1.ChooseEventJson(0);                      // 단서 획득
        Assert.That(Parse(g1.StateJson()).GetProperty("Keepsakes").EnumerateArray()
            .Any(k => k.GetProperty("Body").GetString()!.Contains("콜로세움 꼭대기")), Is.True, "E1 단서가 유품함에");
        // E2 게이트 미충족(총애 정체) — 아직 안 뜬다
        Assert.That(PlayUntilStory(g1, "story_e2", 40), Is.False, "특명 완수 전엔 E2가 안 뜬다");

        // 2) 총애 상승(E1 이후 특명 완수 재현) → E2
        DoctorWorld(j => j["Favor"] = 3);
        var g2 = new Game(1, 91, fresh: false, interactive: false, playerless: false);
        Assert.That(PlayUntilStory(g2, "story_e2"), Is.True, "총애 상승 → E2 특명 뒤의 손");
        g2.ChooseEventJson(1);

        // 3) 컵 우승 → E3 (3지선다·선택 효과)
        DoctorWorld(j => j["MyCupTitles"] = 1);
        var g3 = new Game(1, 91, fresh: false, interactive: false, playerless: false);
        Assert.That(PlayUntilStory(g3, "story_e3"), Is.True, "컵 우승 → E3 콜로세움의 귀빈석");
        var ev = Parse(g3.StateJson()).GetProperty("PendingEvent");
        Assert.That(ev.GetProperty("Choices").GetArrayLength(), Is.EqualTo(3), "E3 = 폭로/침묵/가담 3지선다");
        float gold0 = Parse(g3.StateJson()).GetProperty("Gold").GetSingle();
        g3.ChooseEventJson(1);                      // 침묵을 판다(+250)
        Assert.That(Parse(g3.StateJson()).GetProperty("Gold").GetSingle(), Is.GreaterThan(gold0 + 200f), "침묵의 값 지급");
        // 진실 단서 + 아크 종결(재발화 없음)
        var clues = Parse(g3.StateJson()).GetProperty("Keepsakes");
        Assert.That(clues.EnumerateArray().Any(c => c.GetProperty("Title").GetString()!.Contains("진실")), Is.True, "가이우스의 진실이 유품함에");
        Assert.That(PlayUntilStory(g3, "story_e3", 40), Is.False, "E3는 한 번뿐");
    }
}

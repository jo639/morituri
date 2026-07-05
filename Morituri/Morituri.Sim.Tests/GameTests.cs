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

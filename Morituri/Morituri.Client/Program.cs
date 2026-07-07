using System.Text.Json;
using Morituri.Headless;
using Photino.NET;

namespace Morituri.Client;

// W2 (배포 로드맵[12]) — 감독(루두스) 모드 게임 셸.
// Game(상태 기계)이 이 프로세스 안에 살고, 웹 UI(index.html)가 로컬 API로 조작한다:
//   GET  /api/state       루두스·시즌·다음경기 상태
//   POST /api/next        다음 경기 (body {tacticId} = 내 선수 전술)
//   POST /api/gacha       뽑기 → 마스킹된 후보 3명
//   POST /api/recruit     영입 (body {idx})
//   POST /api/train       훈련 분배 (body {id, axis})
//   POST /api/build       시설 구매 (body {facility})
//   POST /api/newcareer   세계 초기화
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        // 세이브 슬롯 3개: world1~3.json. 구버전 world.json은 슬롯 1로 승격.
        static string SlotPath(int n) => $"world{n}.json";
        if (File.Exists("world.json") && !File.Exists(SlotPath(1)))
            try { File.Move("world.json", SlotPath(1)); } catch { }
        int slot = 1;

        // 시즌당 정규 1라운드로빈 — 클릭 진행 페이스. 슬롯 world가 있으면 미드시즌 그대로 재개.
        var game = new Game(roundsPerSeason: 1, worldPath: SlotPath(slot));

        int port = ViewerServer.StartBackground(AppContext.BaseDirectory, 5173, (method, path, body) => path switch
        {
            "/api/state" => game.StateJson(),
            "/api/next" when method == "POST" => game.PlayNextJson(body),
            "/api/simto" when method == "POST" => game.PlayUntilMineJson(),
            "/api/autofinish" when method == "POST" => game.AutoFinishJson(),
            "/api/watch" when method == "POST" => game.WatchJson(IntOf(body ?? "", "idx")),
            "/api/watchgreat" when method == "POST" => game.WatchGreatJson(IntOf(body ?? "", "idx")),
            "/api/tactic" when method == "POST" => game.TacticJson(StrOf(body ?? "", "id"), StrOf(body ?? "", "tacticId")),
            "/api/gacha" when method == "POST" => game.GachaJson(),
            "/api/recruit" when method == "POST" => game.RecruitJson(IntOf(body ?? "", "idx")),
            "/api/train" when method == "POST" => game.TrainJson(StrOf(body ?? "", "id"), StrOf(body ?? "", "axis")),
            "/api/breakthrough" when method == "POST" => game.BreakthroughJson(StrOf(body ?? "", "id")),
            "/api/pick" when method == "POST" => game.PickProposalJson(StrOf(body ?? "", "id")),
            "/api/rename" when method == "POST" => game.RenameJson(StrOf(body ?? "", "kind"), StrOf(body ?? "", "id"), StrOf(body ?? "", "name")),
            "/api/mastery" when method == "POST" => game.MasteryJson(StrOf(body ?? "", "id"), StrOf(body ?? "", "track")),
            "/api/retire" when method == "POST" => game.RetireJson(StrOf(body ?? "", "id")),
            "/api/perk" when method == "POST" => game.PerkJson(StrOf(body ?? "", "id")),
            "/api/live" when method == "POST" => game.LiveBeginJson(StrOf(body ?? "", "tacticId") is { Length: > 0 } lt ? lt : null),
            "/api/liveswitch" when method == "POST" => game.LiveSwitchJson(FloatOf(body ?? "", "time"), StrOf(body ?? "", "tacticId")),
            "/api/settle" when method == "POST" => game.LiveSettleJson(),
            "/api/sparring" when method == "POST" => game.SparringJson(StrOf(body ?? "", "id")),
            "/api/transfers" when method == "POST" => game.TransfersJson(),
            "/api/buy" when method == "POST" => game.TransferBuyJson(StrOf(body ?? "", "id")),
            "/api/sell" when method == "POST" => game.TransferSellJson(StrOf(body ?? "", "id")),
            "/api/build" when method == "POST" => game.BuildJson(StrOf(body ?? "", "facility")),
            "/api/release" when method == "POST" => game.ReleaseJson(StrOf(body ?? "", "id")),
            "/api/fighter" when method == "POST" => game.ProfileJson(StrOf(body ?? "", "id")),
            "/api/choose" when method == "POST" => game.ChooseEventJson(IntOf(body ?? "", "choice")),
            "/api/newcareer" when method == "POST" => NewCareer(),
            "/api/slots" when method == "POST" => SlotsJson(),
            "/api/loadslot" when method == "POST" => LoadSlot(IntOf(body ?? "", "n")),
            _ => null,
        });

        string NewCareer()
        {
            try { File.Delete(SlotPath(slot)); } catch { }
            game = new Game(roundsPerSeason: 1, fresh: true, worldPath: SlotPath(slot));
            return game.StateJson();
        }

        // 슬롯 요약: 각 world{n}.json의 헤더만 읽어 표시(루두스명·시즌·잔고)
        string SlotsJson()
        {
            var list = new List<object>();
            for (int n = 1; n <= 3; n++)
            {
                object entry;
                try
                {
                    var root = JsonDocument.Parse(File.ReadAllText(SlotPath(n))).RootElement;
                    entry = new { n, exists = true, active = n == slot,
                        ludus = root.TryGetProperty("LudusName", out var ln) && ln.ValueKind == JsonValueKind.String ? ln.GetString() : "내 루두스",
                        season = root.TryGetProperty("SeasonNo", out var sn) ? sn.GetInt32() : 0,
                        gold = root.TryGetProperty("Gold", out var gd) ? (int)gd.GetSingle() : 0 };
                }
                catch { entry = new { n, exists = false, active = n == slot, ludus = (string?)null, season = 0, gold = 0 }; }
                list.Add(entry);
            }
            return JsonSerializer.Serialize(new { ok = true, slots = list });
        }

        string LoadSlot(int n)
        {
            if (n is < 1 or > 3) return """{"error":"슬롯은 1~3"}""";
            slot = n;
            game = new Game(roundsPerSeason: 1, worldPath: SlotPath(slot));   // 있으면 재개, 없으면 새 세계
            return game.StateJson();
        }

        static int IntOf(string body, string key)
        {
            try { return JsonDocument.Parse(body).RootElement.GetProperty(key).GetInt32(); }
            catch { return -1; }
        }
        static float FloatOf(string body, string key)
        {
            try { return JsonDocument.Parse(body).RootElement.GetProperty(key).GetSingle(); }
            catch { return 0f; }
        }
        static string StrOf(string body, string key)
        {
            try { return JsonDocument.Parse(body).RootElement.GetProperty(key).GetString() ?? ""; }
            catch { return ""; }
        }

        new PhotinoWindow()
            .SetTitle("MORITURI · 검투장")
            .SetUseOsDefaultSize(false)
            .SetSize(1180, 860)
            .Center()
            .SetContextMenuEnabled(false)
            .Load($"http://localhost:{port}/index.html")
            .WaitForClose();
    }
}

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

        // 시즌당 정규 1라운드로빈 — 클릭 진행 페이스. world.json v2가 있으면 미드시즌 그대로 재개.
        var game = new Game(roundsPerSeason: 1);

        int port = ViewerServer.StartBackground(AppContext.BaseDirectory, 5173, (method, path, body) => path switch
        {
            "/api/state" => game.StateJson(),
            "/api/next" when method == "POST" => game.PlayNextJson(body),
            "/api/simto" when method == "POST" => game.PlayUntilMineJson(),
            "/api/autofinish" when method == "POST" => game.AutoFinishJson(),
            "/api/watch" when method == "POST" => game.WatchJson(IntOf(body ?? "", "idx")),
            "/api/tactic" when method == "POST" => game.TacticJson(StrOf(body ?? "", "id"), StrOf(body ?? "", "tacticId")),
            "/api/gacha" when method == "POST" => game.GachaJson(),
            "/api/recruit" when method == "POST" => game.RecruitJson(IntOf(body ?? "", "idx")),
            "/api/train" when method == "POST" => game.TrainJson(StrOf(body ?? "", "id"), StrOf(body ?? "", "axis")),
            "/api/breakthrough" when method == "POST" => game.BreakthroughJson(StrOf(body ?? "", "id")),
            "/api/build" when method == "POST" => game.BuildJson(StrOf(body ?? "", "facility")),
            "/api/release" when method == "POST" => game.ReleaseJson(StrOf(body ?? "", "id")),
            "/api/fighter" when method == "POST" => game.ProfileJson(StrOf(body ?? "", "id")),
            "/api/choose" when method == "POST" => game.ChooseEventJson(IntOf(body ?? "", "choice")),
            "/api/newcareer" when method == "POST" => NewCareer(),
            _ => null,
        });

        string NewCareer()
        {
            try { File.Delete("world.json"); } catch { }
            game = new Game(roundsPerSeason: 1, fresh: true);
            return game.StateJson();
        }

        static int IntOf(string body, string key)
        {
            try { return JsonDocument.Parse(body).RootElement.GetProperty(key).GetInt32(); }
            catch { return -1; }
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

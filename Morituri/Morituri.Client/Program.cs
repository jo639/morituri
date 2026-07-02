using Morituri.Headless;
using Photino.NET;

namespace Morituri.Client;

// W1 (배포 로드맵[12]) — 게임 셸.
// Game(상태 기계)이 이 프로세스 안에 살고, 웹 UI(index.html)가 로컬 API로 조작한다:
//   GET  /api/state      현재 시즌 상태 (season.json과 동일 스키마)
//   POST /api/next       다음 경기 1판 실행 → viewer.json/season.json 갱신, 요약 반환
//   POST /api/newcareer  세계 초기화 후 새 커리어
// 화면: 메인메뉴 오버레이 → 리그(league.html) / 로스터 / 관전(viewer.html) 탭 전환.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 출력 폴더 = 작업 디렉터리 (웹 UI 파일 + world/season/viewer.json이 여기 모임).
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        // 시즌당 정규 1라운드로빈(28경기) + 이벤트 4경기 — 클릭 진행에 맞춘 페이스.
        var game = new Game(roundsPerSeason: 1, seasonSeed: null, fresh: false, interactive: true);

        int port = ViewerServer.StartBackground(AppContext.BaseDirectory, 5173, (method, path) => path switch
        {
            "/api/state" => game.StateJson(),
            "/api/next" when method == "POST" => game.PlayNextJson(),
            "/api/newcareer" when method == "POST" => NewCareer(),
            _ => null,
        });

        string NewCareer()
        {
            try { File.Delete("world.json"); } catch { }   // 새 커리어 = 영속 세계 삭제
            game = new Game(roundsPerSeason: 1, seasonSeed: null, fresh: true, interactive: true);
            return game.StateJson();
        }

        new PhotinoWindow()
            .SetTitle("MORITURI · 검투장")
            .SetUseOsDefaultSize(false)
            .SetSize(1120, 840)
            .Center()
            .SetContextMenuEnabled(false)
            .Load($"http://localhost:{port}/index.html")
            .WaitForClose();
    }
}

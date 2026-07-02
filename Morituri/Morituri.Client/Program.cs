using Morituri.Headless;
using Photino.NET;

namespace Morituri.Client;

// W0 (배포 로드맵[12]) — Photino 셸 스파이크.
// 목적: Sim이 이 프로세스 안에서 돌고(시즌 생성), 네이티브 창으로 게임 UI(대시보드)를 띄운다.
//       콘솔 없이 더블클릭 실행되는 "프로그램". 웹 래핑 개발 트랙의 뼈대.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 출력 폴더를 작업 디렉터리로 (복사된 league.html + 생성될 season/world.json이 여기 모임).
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        // ── Sim in-process: 시즌 한 판을 이 프로세스에서 실행 → season.json 생성 ──
        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);              // WinExe: 콘솔 없음, 출력 무시
        try { Season.Run(rounds: 4, seasonSeed: 1, fresh: true); }
        finally { Console.SetOut(prevOut); }

        // ── 내장 서버 + 네이티브 웹뷰 창 ──
        int port = ViewerServer.StartBackground(AppContext.BaseDirectory);

        new PhotinoWindow()
            .SetTitle("MORITURI · 검투장")
            .SetUseOsDefaultSize(false)
            .SetSize(1120, 820)
            .Center()
            .SetContextMenuEnabled(false)
            .Load($"http://localhost:{port}/league.html")
            .WaitForClose();
    }
}

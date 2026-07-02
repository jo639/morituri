using System.Net;

namespace Morituri.Headless;

/// <summary>viewer.html / league.html / sprites 를 HTTP로 서빙 (file:// 에선 fetch 차단).</summary>
public static class ViewerServer
{
    /// <summary>블로킹 서빙 + 기본 브라우저 자동 열기 (CLI viewer/season serve 용).</summary>
    public static void Serve(string dir, int port = 5173, string openPage = "viewer.html")
    {
        var listener = Bind(ref port);
        var url = $"http://localhost:{port}/{openPage}";
        Console.WriteLine($"\n브라우저에서 열기: {url}");
        Console.WriteLine("종료: Ctrl+C\n");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
        Loop(listener, dir);
    }

    /// <summary>백그라운드 스레드 서빙 (브라우저 자동열기·콘솔출력 없음). Photino 클라이언트 in-process 용.</summary>
    public static int StartBackground(string dir, int port = 5173)
    {
        var listener = Bind(ref port);
        new Thread(() => Loop(listener, dir)) { IsBackground = true }.Start();
        return port;
    }

    private static HttpListener Bind(ref int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch
        {
            port = 5174;
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
        }
        return listener;
    }

    private static void Loop(HttpListener listener, string dir)
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }

            var res = ctx.Response;
            var path = ctx.Request.Url?.LocalPath.TrimStart('/') ?? "";
            if (path == "") path = "viewer.html";

            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                res.ContentType = Path.GetExtension(full).ToLower() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                };
                var data = File.ReadAllBytes(full);
                res.ContentLength64 = data.Length;
                res.OutputStream.Write(data);
            }
            else res.StatusCode = 404;
            res.OutputStream.Close();
        }
    }
}

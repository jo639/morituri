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

    /// <summary>백그라운드 스레드 서빙 (브라우저 자동열기·콘솔출력 없음). Photino 클라이언트 in-process 용.
    /// api: "/api/*" 경로 처리기 (method, path, body) → JSON 응답 (null=404). 게임 셸의 액션(/api/next 등).</summary>
    public static int StartBackground(string dir, int port = 5173, Func<string, string, string?, string?>? api = null)
    {
        var listener = Bind(ref port);
        new Thread(() => Loop(listener, dir, api)) { IsBackground = true }.Start();
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

    private static void Loop(HttpListener listener, string dir, Func<string, string, string?, string?>? api = null)
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }

            var res = ctx.Response;
            var path = ctx.Request.Url?.LocalPath.TrimStart('/') ?? "";
            if (path == "") path = "viewer.html";

            // 게임 API (클라이언트 셸의 액션 경로 — 파일보다 우선)
            if (api != null && path.StartsWith("api/"))
            {
                string? json = null;
                try
                {
                    string body = ctx.Request.HasEntityBody
                        ? new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEnd() : "";
                    json = api(ctx.Request.HttpMethod, "/" + path, body);
                }
                catch (Exception ex)   // 처리기 예외 = 500 + 메시지(디버그 가능하게)
                {
                    res.StatusCode = 500;
                    json = "{\"error\":\"" + ex.Message.Replace("\\", "/").Replace("\"", "'") + "\"}";
                }
                if (json != null)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    var body = System.Text.Encoding.UTF8.GetBytes(json);
                    res.ContentLength64 = body.Length;
                    res.OutputStream.Write(body);
                }
                else if (res.StatusCode != 500) res.StatusCode = 404;
                res.OutputStream.Close();
                continue;
            }

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

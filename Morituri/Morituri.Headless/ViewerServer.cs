using System.Net;

namespace Morituri.Headless;

/// <summary>viewer.html + sprites/ 를 HTTP로 서빙 (file:// 에선 fetch 차단돼 스프라이트 못 뜸).</summary>
static class ViewerServer
{
    public static void Serve(string dir, int port = 5173)
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

        var url = $"http://localhost:{port}/viewer.html";
        Console.WriteLine($"\n브라우저에서 열기: {url}");
        Console.WriteLine("종료: Ctrl+C\n");

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }

            var req = ctx.Request;
            var res = ctx.Response;
            var path = req.Url?.LocalPath.TrimStart('/') ?? "";
            if (path == "") path = "viewer.html";

            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                res.ContentType = Path.GetExtension(full).ToLower() switch {
                    ".html" => "text/html; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png"  => "image/png",
                    ".jpg"  or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                };
                var data = File.ReadAllBytes(full);
                res.ContentLength64 = data.Length;
                res.OutputStream.Write(data);
            }
            else
            {
                res.StatusCode = 404;
            }
            res.OutputStream.Close();
        }
    }
}

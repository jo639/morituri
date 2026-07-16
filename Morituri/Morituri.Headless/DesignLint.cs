using System.Text;
using System.Text.RegularExpressions;

namespace Morituri.Headless;

// 디자인 금지 목록 스캐너 (No Prototype Rule 게이트)
// 사용: dotnet run -- designlint [gate]
//   - 유니코드 이모지(콘텐츠·UI 불문), alert()/confirm()/prompt(), 기본 폼 요소, 하이퍼링크를 스캔
//   - gate 인자: 위반 1건 이상이면 종료코드 1 (테스트/CI 편입용)
// 화이트리스트: ★☆✓✗ 등 모노크롬 타이포그래픽 글리프는 허용
public static class DesignLint
{
    // 모노크롬 텍스트 글리프 — 이모지 아님, 허용
    static readonly HashSet<int> Allowed = new()
    {
        0x2605, 0x2606, // ★ ☆
        0x2713, 0x2717, // ✓ ✗
    };

    static bool IsForbiddenEmoji(int cp)
    {
        if (Allowed.Contains(cp)) return false;
        if (cp >= 0x1F000 && cp <= 0x1FFFF) return true;   // 보조평면 이모지 전역
        if (cp >= 0x2600 && cp <= 0x27BF) return true;      // 기타 기호·딩뱃 (⚔☠⚰✨✉…)
        if (cp >= 0x2B00 && cp <= 0x2BFF) return true;      // ⬆⭐⬛…
        if (cp == 0xFE0F) return true;                       // 이모지 표시 셀렉터
        if (cp == 0x231A || cp == 0x231B) return true;       // ⌚⌛
        if (cp >= 0x23E9 && cp <= 0x23FA) return true;       // ⏩⏸⏺ 미디어 컨트롤
        return false;
    }

    // 개발자 전용(콘솔 출력·자기 자신) — 플레이어에게 닿지 않는 파일
    static readonly string[] DevOnly = { "DesignLint.cs", "HealthCheck.cs", "MatrixReport.cs", "Replay.cs" };

    // HTML/JS 한정 패턴 (규칙명, 정규식)
    static readonly (string Rule, Regex Rx)[] HtmlRules =
    {
        ("native-dialog", new Regex(@"(?<![\w$])(alert|confirm|prompt)\s*\(")),
        ("native-select", new Regex(@"<select[\s>]", RegexOptions.IgnoreCase)),
        ("native-check",  new Regex(@"type\s*=\s*""(checkbox|radio)""", RegexOptions.IgnoreCase)),
        ("hyperlink",     new Regex(@"<a\s[^>]*href\s*=\s*""http", RegexOptions.IgnoreCase)),
    };

    public static int Run(string[] args)
    {
        bool gate = args.Contains("gate");
        string root = FindRoot();
        var targets = new List<(string Path, bool Html)>();
        foreach (string f in Directory.GetFiles(Path.Combine(root, "Morituri.Headless"), "*.html")) targets.Add((f, true));
        targets.Add((Path.Combine(root, "Morituri.Headless", "theme.css"), true));
        targets.Add((Path.Combine(root, "Morituri.Client", "index.html"), true));
        foreach (string dir in new[] { "Morituri.Headless", "Morituri.Client", "Morituri.Sim" })
            foreach (string f in Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                if (!f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !DevOnly.Any(f.EndsWith)) // 자기 자신(패턴 정의)·개발자 CLI 콘솔 출력은 게임 표면 아님
                    targets.Add((f, false));

        int total = 0;
        var summary = new StringBuilder();
        foreach (var (path, html) in targets)
        {
            if (!File.Exists(path)) continue;
            string[] lines = File.ReadAllLines(path);
            var hits = new List<string>();
            var emojiCount = new Dictionary<string, int>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!html) // C# 주석의 이모지는 플레이어에게 닿지 않음 — 문자열만 문제
                {
                    int cm = line.IndexOf("//", StringComparison.Ordinal);
                    if (cm >= 0) line = line[..cm];
                }
                for (int c = 0; c < line.Length; c++)
                {
                    int cp = line[c];
                    if (char.IsHighSurrogate(line[c]) && c + 1 < line.Length && char.IsLowSurrogate(line[c + 1]))
                    { cp = char.ConvertToUtf32(line[c], line[c + 1]); c++; }
                    if (IsForbiddenEmoji(cp))
                    {
                        string ch = char.ConvertFromUtf32(cp);
                        emojiCount[ch] = emojiCount.GetValueOrDefault(ch) + 1;
                        total++;
                    }
                }
                if (html)
                    foreach (var (rule, rx) in HtmlRules)
                        foreach (Match m in rx.Matches(line))
                        { hits.Add($"  L{i + 1} [{rule}] {Snippet(line, m.Index)}"); total++; }
            }
            if (emojiCount.Count > 0 || hits.Count > 0)
            {
                summary.AppendLine($"{Path.GetRelativePath(root, path)}");
                if (emojiCount.Count > 0)
                    summary.AppendLine($"  [emoji] {emojiCount.Values.Sum()}건: " +
                        string.Join(" ", emojiCount.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
                foreach (string h in hits) summary.AppendLine(h);
            }
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== design-lint (No Prototype Rule) ===");
        Console.WriteLine(summary.Length == 0 ? "위반 없음." : summary.ToString());
        Console.WriteLine($"총 위반: {total}건" + (gate && total > 0 ? " — GATE FAIL" : ""));
        return gate && total > 0 ? 1 : 0;
    }

    static string Snippet(string line, int at)
    {
        int s = Math.Max(0, at - 20);
        string t = line.Substring(s, Math.Min(60, line.Length - s)).Trim();
        return t.Length > 58 ? t[..58] + "…" : t;
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "Morituri.Headless")) &&
                Directory.Exists(Path.Combine(d.FullName, "Morituri.Client"))) return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException("Morituri 솔루션 루트를 찾을 수 없음");
    }
}

using System.Text.RegularExpressions;

namespace Pusher.Core.Splitting;

/// <summary>
/// Minimal glob matcher for manifest `files` patterns (plan/05-pushscript-dsl.md §6.2):
/// `**` matches recursively across path segments, `*` matches within one segment.
/// Paths use forward slashes and are matched relative to the project root.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string relativePath, string glob)
        => ToRegex(glob).IsMatch(Normalize(relativePath));

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static readonly Dictionary<string, Regex> Cache = new();
    private static readonly object CacheLock = new();

    public static Regex ToRegex(string glob)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(glob, out var cached)) return cached;

            string g = Normalize(glob);
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < g.Length; i++)
            {
                char c = g[i];
                if (c == '*')
                {
                    bool isDouble = i + 1 < g.Length && g[i + 1] == '*';
                    if (isDouble)
                    {
                        // `**/` or trailing `**` — match any depth (including none)
                        bool slashAfter = i + 2 < g.Length && g[i + 2] == '/';
                        sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                        i += slashAfter ? 2 : 1;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');

            var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            Cache[glob] = regex;
            return regex;
        }
    }
}

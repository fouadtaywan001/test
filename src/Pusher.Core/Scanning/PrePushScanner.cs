using System.Text.RegularExpressions;
using Pusher.Core.Scripting;

namespace Pusher.Core.Scanning;

public sealed record ScanFinding(string Rule, DiagnosticSeverity Severity, string File, int Line, string Message);

/// <summary>
/// Activation gate (plan/05-pushscript-dsl.md §8.4, 03-architecture.md §2.2).
/// Runs over exactly the files the manifest will publish:
///  - Secret scan: gitleaks-style regex ruleset
///  - Large files: > 95 MB error (GitHub hard limit 100 MB), > 45 MB warning
///  - Ignore sanity: warns when build-output folders match manifest globs
/// In `strict` mode any secret/oversize finding blocks activation; in `warn`
/// mode the UI may accept a confirmed override.
/// </summary>
public sealed partial class PrePushScanner
{
    public const long HardLimitBytes = 95L * 1024 * 1024;
    public const long WarnLimitBytes = 45L * 1024 * 1024;
    private const long MaxScanBytes = 5L * 1024 * 1024;   // don't regex-scan huge files

    /// <summary>Build-output / VCS folders that should never be published. Shared with the
    /// wizard's file enumeration so these files are excluded up front, not just warned about.</summary>
    public static readonly string[] IgnoredFolderNames = ["bin", "obj", "node_modules", ".vs", ".git", "packages", "dist", ".next"];

    private sealed record SecretRule(string Name, Regex Pattern);

    private static readonly SecretRule[] Rules =
    [
        new("AWS access key",        AwsKeyRegex()),
        new("GitHub token",          GitHubTokenRegex()),
        new("Private key block",     PrivateKeyRegex()),
        new("Connection string",     ConnectionStringRegex()),
        new("Generic API key/secret", GenericSecretRegex()),
        new("JWT",                   JwtRegex()),
        new("Slack token",           SlackTokenRegex()),
    ];

    [GeneratedRegex(@"\b(A3T[A-Z0-9]|AKIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\b(ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,255}\b")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKeyRegex();

    // Only literal secrets: a password assigned a quoted string value, or a
    // password field inside a quoted connection string. Assigning a plain
    // variable (code, no literal value) must NOT match.
    [GeneratedRegex(@"(?i)(\b(Password|Pwd)\s*[:=]\s*[""'][^""']{4,}[""'])|([""][^""]*\b(Password|Pwd)\s*=\s*[^;""\s]{4,})")] // scan:ignore — this IS the detection pattern
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex("""(?i)\b(api[_-]?key|api[_-]?secret|client[_-]?secret|access[_-]?token|auth[_-]?token|secret[_-]?key)\b\s*[:=]\s*["']?[A-Za-z0-9_\-/+=]{16,}["']?""")]
    private static partial Regex GenericSecretRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackTokenRegex();

    /// <summary>
    /// Scans the given files (relative paths) under <paramref name="rootPath"/>.
    /// Only the listed files are read — exactly what the manifest publishes.
    /// </summary>
    public IReadOnlyList<ScanFinding> Scan(string rootPath, IEnumerable<string> relativeFiles)
    {
        var findings = new List<ScanFinding>();

        foreach (var rel in relativeFiles)
        {
            string full = Path.Combine(rootPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            // ---- ignore-folder sanity (warning) ----
            var segments = rel.Replace('\\', '/').Split('/');
            var badSegment = segments.Take(segments.Length - 1)
                .FirstOrDefault(s => IgnoredFolderNames.Contains(s, StringComparer.OrdinalIgnoreCase));
            if (badSegment is not null)
                findings.Add(new ScanFinding("ignore-sanity", DiagnosticSeverity.Warning, rel, 0,
                    $"'{rel}' is inside '{badSegment}/' — build output is usually excluded from publishing."));

            // ---- large files ----
            long size = new FileInfo(full).Length;
            if (size > HardLimitBytes)
            {
                findings.Add(new ScanFinding("large-file", DiagnosticSeverity.Error, rel, 0,
                    $"'{rel}' is {size / (1024 * 1024)} MB — GitHub rejects files over 100 MB. Use Git LFS or exclude it."));
                continue; // don't try to content-scan it
            }
            if (size > WarnLimitBytes)
                findings.Add(new ScanFinding("large-file", DiagnosticSeverity.Warning, rel, 0,
                    $"'{rel}' is {size / (1024 * 1024)} MB — consider Git LFS."));

            // ---- secret scan (text files only, bounded size) ----
            if (size == 0 || size > MaxScanBytes || LooksBinary(full)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(full); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                // Inline suppression for deliberate fixtures (tests, scanner rules, docs):
                // any line containing "scan:ignore" is skipped.
                if (lines[i].Contains("scan:ignore", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var rule in Rules)
                {
                    if (rule.Pattern.IsMatch(lines[i]))
                    {
                        findings.Add(new ScanFinding(rule.Name, DiagnosticSeverity.Error, rel, i + 1,
                            $"Possible {rule.Name} in '{rel}' line {i + 1}. Remove it or exclude the file before publishing."));
                        break; // one finding per line is enough
                    }
                }
            }
        }

        return findings;
    }

    private static bool LooksBinary(string path)
    {
        Span<byte> buffer = stackalloc byte[512];
        try
        {
            using var fs = File.OpenRead(path);
            int read = fs.Read(buffer);
            for (int i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch
        {
            return true;
        }
    }
}

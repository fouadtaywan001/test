using Pusher.Core.Models;
using Pusher.Core.Scripting;

namespace Pusher.Core.Splitting;

/// <summary>A manifest commit with its globs resolved to concrete project files.</summary>
public sealed record ResolvedCommit(int Order, string Message, string Body, IReadOnlyList<string> Globs, IReadOnlyList<string> Files);

/// <summary>
/// Resolves a SplitConfig against the actual project file list and enforces
/// the file-level split rules (plan/05-pushscript-dsl.md §8.2):
/// V-SPLIT-1 (every glob matches ≥ 1 file), V-SPLIT-2 (first-match-wins:
/// a file claimed by an earlier commit is skipped by later globs, which is
/// what makes a trailing catch-all `["**"]` commit work; a commit left with
/// zero files after claiming is dropped with a warning),
/// V-SPLIT-3 (unmatched files → warning).
/// </summary>
public static class SplitResolver
{
    /// <param name="projectFiles">All publishable files, relative paths with forward slashes.</param>
    public static IReadOnlyList<ResolvedCommit> Resolve(SplitConfig split, IReadOnlyList<string> projectFiles, DiagnosticBag diags)
    {
        var normalized = projectFiles.Select(GlobMatcher.Normalize).ToList();
        return split.Strategy switch
        {
            SplitStrategy.Folders => ResolveFolders(normalized, split.Order),
            SplitStrategy.Groups => ResolveGroups(normalized, split.GroupSize),
            _ => ResolveManifest(split.Commits, normalized, diags),
        };
    }

    private static List<ResolvedCommit> ResolveManifest(IReadOnlyList<ManifestCommit> commits, List<string> files, DiagnosticBag diags)
    {
        var claimedBy = new Dictionary<string, (int CommitIndex, string Message)>();
        var result = new List<ResolvedCommit>();

        for (int i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            var matched = new List<string>();

            foreach (var glob in c.Globs)
            {
                var hits = files.Where(f => GlobMatcher.IsMatch(f, glob)).ToList();
                if (hits.Count == 0)
                    diags.Error("V-SPLIT-1", $"Glob '{glob}' in commit \"{c.Message}\" matches no project file.", c.Line, c.Column);

                // First-match-wins: files claimed by an earlier commit are simply
                // skipped here — this is what makes a trailing catch-all "**" work.
                foreach (var f in hits)
                {
                    if (!claimedBy.ContainsKey(f))
                    {
                        claimedBy[f] = (i, c.Message);
                        matched.Add(f);
                    }
                }
            }

            if (matched.Count == 0 && c.Globs.Count > 0)
            {
                diags.Warn("V-SPLIT-2", $"Commit \"{c.Message}\" is empty — every file it matches was already claimed by an earlier commit. It will be dropped.", c.Line, c.Column);
                continue;
            }

            result.Add(new ResolvedCommit(result.Count + 1, c.Message, c.Body, c.Globs, matched.OrderBy(f => f, StringComparer.Ordinal).ToList()));
        }

        var unmatched = files.Where(f => !claimedBy.ContainsKey(f)).ToList();
        if (unmatched.Count > 0)
        {
            string sample = string.Join(", ", unmatched.Take(5));
            if (unmatched.Count > 5) sample += $", … ({unmatched.Count} total)";
            diags.Warn("V-SPLIT-3", $"Files matched by no commit (they will NOT be published): {sample}", 1, 1);
        }

        return result;
    }

    private static List<ResolvedCommit> ResolveFolders(List<string> files, SplitOrder order)
    {
        var groups = files
            .GroupBy(f => f.Contains('/') ? f[..f.IndexOf('/')] : "")
            .Select(g => (Folder: g.Key, Files: g.OrderBy(f => f, StringComparer.Ordinal).ToList()))
            .ToList();

        // root files first, then folders
        groups = groups
            .OrderBy(g => g.Folder == "" ? 0 : 1)
            .ThenBy(g => g.Folder, StringComparer.Ordinal)
            .ToList();

        if (order == SplitOrder.Alphabetical)
            groups = groups.OrderBy(g => g.Folder, StringComparer.Ordinal).ToList();

        return groups.Select((g, i) => new ResolvedCommit(
            i + 1,
            g.Folder == "" ? "Add project root files" : $"Add {g.Folder}",
            "",
            [g.Folder == "" ? "*" : $"{g.Folder}/**"],
            g.Files)).ToList();
    }

    private static List<ResolvedCommit> ResolveGroups(List<string> files, int size)
    {
        var ordered = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        var result = new List<ResolvedCommit>();
        for (int i = 0; i < ordered.Count; i += size)
        {
            var chunk = ordered.Skip(i).Take(size).ToList();
            result.Add(new ResolvedCommit(result.Count + 1, $"Add files (part {result.Count + 1})", "", ["<auto>"], chunk));
        }
        return result;
    }
}

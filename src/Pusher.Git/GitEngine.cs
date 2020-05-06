using LibGit2Sharp;
using Pusher.Core.Abstractions;

namespace Pusher.Git;

/// <summary>
/// LibGit2Sharp implementation of <see cref="IGitEngine"/>.
/// Works exclusively on the internal staging clone; pins core.autocrlf=false so
/// committed bytes are exactly the snapshot bytes (plan/03-architecture.md section 2.3).
/// </summary>
public sealed class GitEngine : IGitEngine
{
    static GitEngine()
    {
        // Staging repos live under C:\ProgramData and are shared between the desktop
        // user (UI creates them) and the service account (worker commits/pushes them).
        // libgit2's ownership check ("repository path ... is not owned by current user")
        // would reject that cross-account access, so disable it for this process.
        // Safe here: the engine only ever opens our own staging paths, never arbitrary repos.
        GlobalSettings.SetOwnerValidation(false);
    }

    public string CreateStagingSnapshot(string projectPath, string stagingRoot, string projectId, IReadOnlyList<string> ignoreFolders)
    {
        var stagingPath = Path.Combine(stagingRoot, projectId);
        if (Directory.Exists(stagingPath))
            Directory.Delete(stagingPath, recursive: true);
        Directory.CreateDirectory(stagingPath);

        CopyTree(projectPath, stagingPath, ignoreFolders);

        if (!Repository.IsValid(stagingPath))
            Repository.Init(stagingPath);

        using var repo = new Repository(stagingPath);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        return stagingPath;
    }

    public void EnsureStagingRepo(string stagingPath, string branch)
    {
        if (!Repository.IsValid(stagingPath))
            Repository.Init(stagingPath);

        using var repo = new Repository(stagingPath);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);

        // A fresh init leaves HEAD unborn on libgit2's default branch (usually "master").
        // Point it at the script's branch BEFORE the first commit so the push refspec
        // refs/heads/<branch> matches where the commits actually land.
        // NOTE: UpdateTarget(ref, string) resolves the string as a git OBJECT — impossible
        // in an empty repo (no objects yet) and the cause of the "No valid git object
        // identified by 'refs/heads/...'" crash. Rewriting HEAD as a symbolic reference
        // is the correct operation for an unborn branch.
        if (repo.Info.IsHeadUnborn && repo.Head.FriendlyName != branch)
        {
            repo.Refs.Add("HEAD", $"refs/heads/{branch}", allowOverwrite: true);
        }
        else if (!repo.Info.IsHeadUnborn && repo.Head.FriendlyName != branch)
        {
            // Self-heal: commits already exist but on the wrong branch name
            // (e.g. created before this fix). Move/create the wanted branch at the
            // current tip and switch HEAD to it — history is preserved.
            var target = repo.Branches[branch] ?? repo.CreateBranch(branch, repo.Head.Tip);
            Commands.Checkout(repo, target);
        }
    }

    public string CreateCommit(string stagingPath, IReadOnlyList<string> files, string message,
        string authorName, string authorEmail, DateTimeOffset when)
    {
        using var repo = new Repository(stagingPath);
        foreach (var file in files)
            Commands.Stage(repo, file);

        var sig = new Signature(authorName, authorEmail, when);
        var commit = repo.Commit(message, sig, sig, new CommitOptions { AllowEmptyCommit = false });
        return commit.Sha;
    }

    public void Push(string stagingPath, string remoteUrl, string branch, string username, string token)
    {
        using var repo = new Repository(stagingPath);
        var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.Add("origin", remoteUrl);
        if (remote.Url != remoteUrl)
        {
            repo.Network.Remotes.Update("origin", r => r.Url = remoteUrl);
            remote = repo.Network.Remotes["origin"];
        }

        // TRAP: libgit2sharp's Push does NOT throw when the REMOTE rejects the ref
        // update (branch rulesets, GitHub secret-scanning push protection, hooks...).
        // Rejections only surface through OnPushStatusError — without it the push
        // "succeeds" silently and commits get falsely marked as pushed.
        string? rejection = null;
        var options = new PushOptions
        {
            CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
            {
                Username = username,
                Password = token,
            },
            OnPushStatusError = err => rejection = $"{err.Reference}: {err.Message}",
        };

        // Never force-push: plain refspec.
        repo.Network.Push(remote, $"refs/heads/{branch}:refs/heads/{branch}", options);

        if (rejection is not null)
            throw new InvalidOperationException($"Remote rejected push — {rejection}");

        // Belt and braces: confirm the remote branch tip actually equals our local tip.
        // Catches any silent-failure mode the callback misses.
        var localTip = repo.Branches[branch]?.Tip?.Sha
            ?? throw new InvalidOperationException($"Local branch '{branch}' has no tip after push.");
        var remoteTip = GetRemoteBranchTip(remoteUrl, branch, username, token);
        if (!string.Equals(remoteTip, localTip, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Push verification failed for '{branch}': remote tip is " +
                $"{(remoteTip is null ? "MISSING (branch does not exist on remote)" : remoteTip[..8])} " +
                $"but local tip is {localTip[..8]}. The remote likely rejected the update " +
                "(check branch rulesets and GitHub push protection).");
    }

    public bool LocalCommitExists(string stagingPath, string sha)
    {
        using var repo = new Repository(stagingPath);
        return repo.Lookup<Commit>(sha) is not null;
    }

    public string? GetRemoteBranchTip(string remoteUrl, string branch, string username, string token)
    {
        var refs = Repository.ListRemoteReferences(remoteUrl, (_, _, _) => new UsernamePasswordCredentials
        {
            Username = username,
            Password = token,
        });
        return refs.FirstOrDefault(r => r.CanonicalName == $"refs/heads/{branch}")?.TargetIdentifier;
    }

    private static void CopyTree(string source, string destination, IReadOnlyList<string> ignoreFolders)
    {
        var ignore = new HashSet<string>(ignoreFolders, StringComparer.OrdinalIgnoreCase) { ".git" };

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            if (ignore.Contains(name)) continue;
            var target = Path.Combine(destination, name);
            Directory.CreateDirectory(target);
            CopyTree(dir, target, ignoreFolders);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }
}

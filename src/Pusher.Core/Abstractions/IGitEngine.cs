using Pusher.Core.Models;

namespace Pusher.Core.Abstractions;

/// <summary>
/// Abstraction over the git layer (implemented with LibGit2Sharp in Pusher.Git).
/// All operations act on the internal STAGING clone, never the user's live folder
/// (see plan/03-architecture.md section 2.3).
/// </summary>
public interface IGitEngine
{
    /// <summary>Snapshot the user's project folder into the staging area and init/open a repo there
    /// with core.autocrlf=false. Returns the staging path.</summary>
    string CreateStagingSnapshot(string projectPath, string stagingRoot, string projectId, IReadOnlyList<string> ignoreFolders);

    /// <summary>Make sure the staging folder is a usable git repository on the given branch:
    /// init when missing, pin core.autocrlf=false, and point an unborn HEAD at the branch.
    /// Idempotent — safe to call before every commit cycle (self-heals older projects
    /// whose staging snapshot was created without a repo).</summary>
    void EnsureStagingRepo(string stagingPath, string branch);

    /// <summary>Create a commit in the staging repo containing exactly <paramref name="files"/>,
    /// with the given author/committer date (backdating supported). Returns the commit SHA.
    /// Phase 1 of the two-phase execution: the caller records the SHA before pushing.</summary>
    string CreateCommit(string stagingPath, IReadOnlyList<string> files, string message,
        string authorName, string authorEmail, DateTimeOffset when);

    /// <summary>Push the current branch to the remote (git smart-HTTP; PAT as password).
    /// Phase 2 of the two-phase execution. Never force-pushes.</summary>
    void Push(string stagingPath, string remoteUrl, string branch, string username, string token);

    /// <summary>True if the local HEAD chain contains a commit SHA that has not been pushed
    /// (crash recovery: Committed-but-not-Pushed detection).</summary>
    bool LocalCommitExists(string stagingPath, string sha);

    /// <summary>Inspect the remote target branch. Returns the tip SHA, or null when the branch
    /// does not exist. Used by the remote-state guard (foreign-commit check).</summary>
    string? GetRemoteBranchTip(string remoteUrl, string branch, string username, string token);
}

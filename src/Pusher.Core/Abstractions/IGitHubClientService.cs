namespace Pusher.Core.Abstractions;

public sealed record GitHubRepoInfo(string Owner, string Name, string CloneUrl, string DefaultBranch, bool Private);

/// <summary>The signed-in account's identity, used to auto-fill the commit author
/// (plan/02-github-api.md section 8: author email MUST be a verified GitHub email).
/// <paramref name="Name"/> may be null when the profile has no display name set.</summary>
public sealed record GitHubIdentity(string Login, string? Name, IReadOnlyList<string> VerifiedEmails);

/// <summary>
/// Abstraction over the GitHub REST API (implemented with Octokit in Pusher.GitHub).
/// Only account/repo management goes through REST; pushing is git smart-HTTP and
/// consumes zero REST quota (plan/02-github-api.md section 7).
/// </summary>
public interface IGitHubClientService
{
    Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default);

    /// <summary>Verified emails on the account — commit author email MUST match one of
    /// these for contribution-graph squares to count (plan/02-github-api.md section 8).</summary>
    Task<IReadOnlyList<string>> GetVerifiedEmailsAsync(CancellationToken ct = default);

    /// <summary>One-shot fetch of login + display name + verified emails, used by the
    /// Settings "Fetch from GitHub" button to auto-fill the commit author identity.</summary>
    Task<GitHubIdentity> GetIdentityAsync(CancellationToken ct = default);

    Task<GitHubRepoInfo?> GetRepoAsync(string owner, string name, CancellationToken ct = default);

    Task<GitHubRepoInfo> CreateRepoAsync(string name, string? description, bool isPrivate, CancellationToken ct = default);

    // ---- after_last_commit hooks (best-effort; failures logged, never fail the plan) ----
    Task SetTopicsAsync(string owner, string name, IReadOnlyList<string> topics, CancellationToken ct = default);
    Task SetHomepageAsync(string owner, string name, string url, CancellationToken ct = default);
    Task SetVisibilityAsync(string owner, string name, bool isPrivate, CancellationToken ct = default);
}

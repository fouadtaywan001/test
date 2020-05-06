using Octokit;
using Pusher.Core.Abstractions;

namespace Pusher.GitHub;

/// <summary>Octokit implementation of <see cref="IGitHubClientService"/> (PAT auth).</summary>
public sealed class GitHubClientService : IGitHubClientService
{
    private readonly GitHubClient _client;

    public GitHubClientService(string personalAccessToken)
    {
        _client = new GitHubClient(new ProductHeaderValue("GitHubSchedulePusher"))
        {
            Credentials = new Credentials(personalAccessToken),
        };
    }

    public async Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default)
    {
        var user = await _client.User.Current();
        return user.Login;
    }

    public async Task<IReadOnlyList<string>> GetVerifiedEmailsAsync(CancellationToken ct = default)
    {
        var emails = await _client.User.Email.GetAll();
        return emails.Where(e => e.Verified).Select(e => e.Email).ToList();
    }

    public async Task<GitHubIdentity> GetIdentityAsync(CancellationToken ct = default)
    {
        var user = await _client.User.Current();
        var emails = await _client.User.Email.GetAll();
        // Primary verified email first — it's the best default for the commit author.
        var verified = emails.Where(e => e.Verified)
            .OrderByDescending(e => e.Primary)
            .Select(e => e.Email)
            .ToList();
        return new GitHubIdentity(user.Login, user.Name, verified);
    }

    public async Task<GitHubRepoInfo?> GetRepoAsync(string owner, string name, CancellationToken ct = default)
    {
        try
        {
            var repo = await _client.Repository.Get(owner, name);
            return new GitHubRepoInfo(repo.Owner.Login, repo.Name, repo.CloneUrl, repo.DefaultBranch, repo.Private);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<GitHubRepoInfo> CreateRepoAsync(string name, string? description, bool isPrivate, CancellationToken ct = default)
    {
        var repo = await _client.Repository.Create(new NewRepository(name)
        {
            Description = description,
            Private = isPrivate,
            AutoInit = false, // critical: an auto-created README would put a foreign commit on the target branch
        });
        return new GitHubRepoInfo(repo.Owner.Login, repo.Name, repo.CloneUrl, repo.DefaultBranch, repo.Private);
    }

    public Task SetTopicsAsync(string owner, string name, IReadOnlyList<string> topics, CancellationToken ct = default)
        => _client.Repository.ReplaceAllTopics(owner, name, new RepositoryTopics(topics));

    public Task SetHomepageAsync(string owner, string name, string url, CancellationToken ct = default)
        => _client.Repository.Edit(owner, name, new RepositoryUpdate { Homepage = url });

    public Task SetVisibilityAsync(string owner, string name, bool isPrivate, CancellationToken ct = default)
        => _client.Repository.Edit(owner, name, new RepositoryUpdate { Private = isPrivate });
}

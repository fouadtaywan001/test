using Pusher.Core.Abstractions;
using Pusher.GitHub;
using Pusher.Storage;

namespace Pusher.App.Services;

/// <summary>Builds an authenticated GitHub client from the DPAPI-protected PAT in
/// settings. Returns null when no token is stored so callers can show a friendly
/// "add your PAT in Settings first" message instead of a 401.</summary>
public sealed class GitHubClientFactory
{
    private readonly AppSettingsStore _settings;
    private readonly ITokenProtector _protector;

    public GitHubClientFactory(AppSettingsStore settings, ITokenProtector protector)
    {
        _settings = settings;
        _protector = protector;
    }

    public bool HasToken => !string.IsNullOrEmpty(_settings.Load().ProtectedToken);

    /// <summary>Creates a client. <paramref name="overrideToken"/> lets Settings test a
    /// freshly typed PAT before it has been saved.</summary>
    public IGitHubClientService? Create(string? overrideToken = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideToken))
            return new GitHubClientService(overrideToken.Trim());

        var stored = _settings.Load().ProtectedToken;
        if (string.IsNullOrEmpty(stored)) return null;
        return new GitHubClientService(_protector.Unprotect(stored));
    }
}

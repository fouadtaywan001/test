using System.Text.Json;
using Pusher.Core.Models;

namespace Pusher.Storage;

/// <summary>Global app settings persisted as JSON in ProgramData. The PAT is stored
/// DPAPI-protected (see <see cref="DpapiTokenProtector"/>), never plaintext.</summary>
public sealed class AppSettings
{
    public string? ProtectedToken { get; set; }          // DPAPI-encrypted PAT (base64)
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";        // must be a GitHub-verified email
    public CatchUpPolicy CatchUp { get; set; } = CatchUpPolicy.Immediate;
    public int CatchUpMaxPerDay { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 60;

    // ---- application behavior ----------------------------------------------
    public bool CloseToTray { get; set; } = true;        // X hides to tray instead of exiting
    public bool MinimizeToTray { get; set; } = true;     // minimize hides to tray
    public bool TrayNotifications { get; set; } = true;  // balloon tips from the tray icon
    public bool StartWithWindows { get; set; }           // HKCU Run key, applied on save
    public bool ConfirmBeforeDelete { get; set; } = true; // dashboard Delete asks first
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettingsStore(string? basePath = null)
    {
        var root = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GitHubSchedulePusher");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public string BaseDirectory => Path.GetDirectoryName(_path)!;
    public string StagingRoot => Path.Combine(BaseDirectory, "staging");
    public string DatabasePath => Path.Combine(BaseDirectory, "state.db");

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
        => File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
}

using Microsoft.Win32;

namespace Pusher.App.Services;

/// <summary>Registers/unregisters the app in the current user's Run key so it starts
/// with Windows. HKCU needs no elevation and only affects this user.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GitHubSchedulePusher";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <returns>Error message, or null on success.</returns>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return "Could not determine the app's exe path.";
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not update Windows startup: {ex.Message}";
        }
    }
}

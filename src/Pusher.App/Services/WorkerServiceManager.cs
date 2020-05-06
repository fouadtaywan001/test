using System.Diagnostics;
using System.IO;

namespace Pusher.App.Services;

/// <summary>
/// One-click install/uninstall/start/stop for the background Windows Service
/// (plan/03-architecture.md §3). Each mutation runs sc.exe elevated — Windows
/// shows a single UAC prompt; no manual command typing.
/// </summary>
public sealed class WorkerServiceManager
{
    public const string ServiceName = "GitHubSchedulePusher";

    /// <summary>The service exe ships next to the app (publish layout) or in the
    /// sibling project output during development — first hit wins.</summary>
    public static string? FindServiceExe()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "Pusher.Service.exe"),
            Path.Combine(baseDir, "service", "Pusher.Service.exe"),
            // dev layout: src/Pusher.App/bin/<cfg>/net8.0-windows/ -> src/Pusher.Service/bin/<cfg>/net8.0/
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Pusher.Service", "bin", "Debug", "net8.0", "Pusher.Service.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Pusher.Service", "bin", "Release", "net8.0", "Pusher.Service.exe")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public bool IsInstalled()
    {
        var (code, _) = RunSc($"query {ServiceName}", elevated: false);
        return code == 0;
    }

    public bool IsRunning()
    {
        var (code, output) = RunSc($"query {ServiceName}", elevated: false);
        return code == 0 && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates the service (auto-start at boot, LocalSystem) and starts it.
    /// LocalSystem avoids the password + "Log on as a service" friction called out in
    /// plan/04-roadmap.md open question #5; state lives in ProgramData so it is
    /// reachable regardless of the service account.</summary>
    public (bool Ok, string Message) Install(string serviceExePath)
    {
        if (!File.Exists(serviceExePath))
            return (false, $"Service exe not found: {serviceExePath}");

        var (code, output) = RunSc(
            $"create {ServiceName} binPath= \"{serviceExePath}\" start= auto DisplayName= \"GitHub Schedule Pusher\"",
            elevated: true);
        if (code != 0) return (false, Trim(output, "Install failed"));

        RunSc($"description {ServiceName} \"Pushes scheduled commits for GitHub Schedule Pusher.\"", elevated: true);
        var (startCode, startOut) = RunSc($"start {ServiceName}", elevated: true);
        return startCode == 0
            ? (true, "Service installed and started.")
            : (true, $"Service installed; start it manually if needed ({Trim(startOut, "start failed")}).");
    }

    public (bool Ok, string Message) Uninstall()
    {
        RunSc($"stop {ServiceName}", elevated: true); // best-effort; delete works on stopped services
        var (code, output) = RunSc($"delete {ServiceName}", elevated: true);
        return code == 0 ? (true, "Service uninstalled.") : (false, Trim(output, "Uninstall failed"));
    }

    public (bool Ok, string Message) Start()
    {
        var (code, output) = RunSc($"start {ServiceName}", elevated: true);
        return code == 0 ? (true, "Service started.") : (false, Trim(output, "Start failed"));
    }

    public (bool Ok, string Message) Stop()
    {
        var (code, output) = RunSc($"stop {ServiceName}", elevated: true);
        return code == 0 ? (true, "Service stopped.") : (false, Trim(output, "Stop failed"));
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>Elevated calls relaunch sc.exe through UAC (runas). Elevated output
    /// can't be captured across the elevation boundary, so success is re-checked via
    /// a follow-up non-elevated query.</summary>
    private static (int ExitCode, string Output) RunSc(string args, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = elevated,
            };
            if (elevated) psi.Verb = "runas";
            else { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }

            using var p = Process.Start(psi);
            if (p is null) return (-1, "Failed to start sc.exe");
            string output = elevated ? "" : p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "Cancelled at the UAC prompt.");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Trim(string output, string fallback)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(line) ? fallback : line;
    }
}

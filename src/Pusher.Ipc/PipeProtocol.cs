using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pusher.Ipc;

/// <summary>Named-pipe control protocol between the WPF UI and the worker service.
/// Line-delimited JSON over pipe "GitHubSchedulePusher.Control" (plan/03-architecture.md section 2.5).</summary>
public static class PipeProtocol
{
    public const string PipeName = "GitHubSchedulePusher.Control";

    /// <summary>Bumped whenever worker behavior changes in a way the UI must know about.
    /// The worker answers "ping" with "pong:{Version}"; the UI compares against its own
    /// compiled-in value and warns when the installed service is a stale build.</summary>
    public const string Version = "3";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record PipeRequest(
    string Command,          // "ping" | "wake" | "status" | "pause" | "resume"
    long? ProjectId = null);

public sealed record PipeResponse(
    bool Ok,
    string? Message = null,
    string? PayloadJson = null);

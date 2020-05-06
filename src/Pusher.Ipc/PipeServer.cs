using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Pusher.Ipc;

/// <summary>Async named-pipe server hosted inside the worker service. One JSON request line in,
/// one JSON response line out, per connection.</summary>
public sealed class PipeServer
{
    private readonly Func<PipeRequest, Task<PipeResponse>> _handler;

    public PipeServer(Func<PipeRequest, Task<PipeResponse>> handler) => _handler = handler;

    /// <summary>Creates the listening stream. The worker usually runs as a Windows service
    /// (LocalSystem), while the UI runs as the logged-in user — the default pipe DACL would
    /// deny that user with UnauthorizedAccessException. On Windows we therefore attach an
    /// explicit ACL granting Authenticated Users read/write (connect), keeping full control
    /// with SYSTEM and Administrators.</summary>
    private static NamedPipeServerStream CreateServerStream()
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeProtocol.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, security);
        }

        return new NamedPipeServerStream(
            PipeProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = CreateServerStream();

            try
            {
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, leaveOpen: true);
                await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct);
                if (line is null) continue;

                PipeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<PipeRequest>(line, PipeProtocol.JsonOptions)
                                  ?? new PipeRequest("invalid");
                    response = await _handler(request);
                }
                catch (Exception ex)
                {
                    response = new PipeResponse(false, ex.Message);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeProtocol.JsonOptions));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Broken pipe / client vanished — keep serving.
            }
        }
    }
}

/// <summary>Client used by the WPF UI. Short-lived: connect, send one command, read one response.</summary>
public static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await using var client = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));

        try
        {
            await client.ConnectAsync(cts.Token);
            await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeProtocol.JsonOptions));
            var line = await reader.ReadLineAsync(cts.Token);
            return line is null
                ? new PipeResponse(false, "No response from service.")
                : JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.JsonOptions)!;
        }
        catch (OperationCanceledException)
        {
            return new PipeResponse(false, "Service is not running or did not respond.");
        }
        catch (UnauthorizedAccessException)
        {
            // Pipe exists but its DACL rejects this user — an old worker build without
            // the permissive ACL is still running. Reinstalling/restarting the worker fixes it.
            return new PipeResponse(false,
                "The running worker is an older build that this user cannot talk to. Restart or reinstall the worker service.");
        }
        catch (Exception ex)
        {
            // IPC must never crash the UI; any transport error just means "not reachable".
            return new PipeResponse(false, $"Cannot reach the worker: {ex.Message}");
        }
    }
}

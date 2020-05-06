using System.Security.Cryptography;
using System.Text;
using Pusher.Core.Abstractions;

namespace Pusher.Storage;

/// <summary>DPAPI (CurrentUser) token protection. Windows-only by design — the app targets
/// Windows (WPF + Windows Service). Both UI and service must run under the same user account
/// for the token to round-trip (plan/03-architecture.md, service account requirement).</summary>
public sealed class DpapiTokenProtector : ITokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GitHubSchedulePusher.v1");

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI token protection requires Windows.");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI token protection requires Windows.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

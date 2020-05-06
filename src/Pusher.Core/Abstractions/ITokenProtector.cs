namespace Pusher.Core.Abstractions;

/// <summary>Encrypts/decrypts the GitHub PAT at rest (DPAPI on Windows —
/// plan/03-architecture.md: the token is never stored in plaintext).</summary>
public interface ITokenProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

using System.Security.Cryptography;
using System.Text;

namespace ProxyManager.Services;

/// <summary>Encrypts secrets with DPAPI so they can only be read by the same Windows user.</summary>
public static class Secrets
{
    private static readonly byte[] Entropy = "ProxyManager.v1"u8.ToArray();

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Settings copied from another user/machine: the password has to be entered again.
            return "";
        }
    }
}

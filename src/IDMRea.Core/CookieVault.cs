using System.Security.Cryptography;
using System.Text;
namespace IDMRea.Core;

/// <summary>
/// Keeps a download's cookies on disk without storing them readable: on Windows they are encrypted for the current
/// user (DPAPI). Elsewhere nothing is stored, and the cookies last only while the engine runs.
/// </summary>
public static class CookieVault
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IDMRea.Cookies.v1");

    public static string? Protect(string? cookies)
    {
        if (string.IsNullOrEmpty(cookies) || !OperatingSystem.IsWindows()) return null;
        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(cookies), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>The cookies, or null when there are none or they can't be read (another user, another PC).</summary>
    public static string? Unprotect(string? stored)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}

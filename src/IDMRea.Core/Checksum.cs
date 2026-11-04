using System.Security.Cryptography;
namespace IDMRea.Core;

public static class Checksum
{
    /// <summary>A SHA-256 as the site writes it: 64 hexadecimal characters, any case. Returns it in lower case.</summary>
    public static string Normalize(string value)
    {
        var sum = value.Trim();
        if (sum.Length != 64 || !sum.All(Uri.IsHexDigit)) throw new ArgumentException("A SHA-256 checksum is 64 hexadecimal characters.");
        return sum.ToLowerInvariant();
    }

    public static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
}

using System.Buffers;
using System.Security.Cryptography;
namespace IDMRea.Core;

/// <summary>
/// SHA-256 of a finished download, read back in 1 MiB steps. (SHA256.HashDataAsync on a stream reads 4 KiB at a
/// time: for a 512 MiB file that is 131,072 reads and seconds of CPU.)
/// </summary>
public static class FileHash
{
    private const int Step = 1024 * 1024;

    public static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(Step);
        try
        {
            long at = 0;
            int read;
            while ((read = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, Step), at, token)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                at += read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

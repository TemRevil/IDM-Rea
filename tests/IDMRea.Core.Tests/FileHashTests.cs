using System.Security.Cryptography;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class FileHashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(3 * 1024 * 1024 + 7)]
    public async Task MatchesSha256OfTheWholeFile(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "idmrea-hash-" + Guid.NewGuid());
        try
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), await FileHash.Sha256Async(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }
}

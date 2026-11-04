using System.Text;
using System.Text.Json;
using IDMRea.BrowserHost;
using Xunit;
namespace IDMRea.Tests;

/// <summary>The browser talks to the host in its own framing: a 4-byte little-endian length, then UTF-8 JSON.</summary>
public sealed class BrowserHostTests
{
    [Fact]
    public async Task FramingRoundTrip()
    {
        using var pipe = new MemoryStream();
        await NativeMessaging.WriteAsync(pipe, new { type = "hello", text = "تحميل" });
        await NativeMessaging.WriteAsync(pipe, new { type = "second" });
        pipe.Position = 0;
        using (var first = await NativeMessaging.ReadAsync(pipe))
        {
            Assert.Equal("hello", first!.RootElement.GetProperty("type").GetString());
            Assert.Equal("تحميل", first.RootElement.GetProperty("text").GetString());
        }
        using (var second = await NativeMessaging.ReadAsync(pipe)) Assert.Equal("second", second!.RootElement.GetProperty("type").GetString());
        Assert.Null(await NativeMessaging.ReadAsync(pipe)); // the browser closed the port
    }

    [Fact]
    public async Task FramingUsesALittleEndianLength()
    {
        using var pipe = new MemoryStream();
        await NativeMessaging.WriteAsync(pipe, new { a = 1 });
        var bytes = pipe.ToArray();
        Assert.Equal(bytes.Length - 4, bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
    }

    [Fact]
    public async Task FramingRejectsOversizeAndCutMessages()
    {
        using var huge = new MemoryStream(BitConverter.GetBytes(NativeMessaging.MaxMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(huge));
        var cut = new MemoryStream(); cut.Write(BitConverter.GetBytes(10)); cut.Write("{\"a\""u8); cut.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(cut));
    }

    [Fact]
    public void TargetFolderComesFromTheRegistrationFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        Assert.Null(HostHandler.TargetDataFolder(dir));
        File.WriteAllText(Path.Combine(dir, "browser-target.txt"), @"  D:\Data\IDMRea  " + "\r\n");
        Assert.Equal(@"D:\Data\IDMRea", HostHandler.TargetDataFolder(dir));
    }

    [Fact]
    public void AFolderSetForTheBrowserItselfWinsOverTheRegistrationFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        Assert.Null(HostHandler.ChooseDataFolder(dir, null));
        File.WriteAllText(Path.Combine(dir, "browser-target.txt"), @"D:\Data\IDMRea");
        Assert.Equal(@"D:\Data\IDMRea", HostHandler.ChooseDataFolder(dir, null));
        Assert.Equal(@"D:\Data\IDMRea", HostHandler.ChooseDataFolder(dir, "  "));
        Assert.Equal(@"E:\Check\data", HostHandler.ChooseDataFolder(dir, @"E:\Check\data"));
    }
}

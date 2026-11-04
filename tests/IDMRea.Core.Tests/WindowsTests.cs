using IDMRea.Contracts;
using IDMRea.Engine.Windows;
using Microsoft.Win32;
using Xunit;
namespace IDMRea.Tests;

public sealed class WindowsTests
{
    [Fact]
    public void StartupRegistrationRoundTrip()
    {
        const string key = @"Software\IDMRea-Tests\Run";
        var startup = new StartupRegistration(key, "IDM-Rea");
        object? Value() { using var run = Registry.CurrentUser.OpenSubKey(key); return run?.GetValue("IDM-Rea"); }
        try
        {
            Assert.Null(Value());
            startup.Set(true, @"C:\Apps\IDM-Rea\engine\IDMRea.Engine.exe");
            Assert.Equal(@"""C:\Apps\IDM-Rea\engine\IDMRea.Engine.exe"" --tray", Value());
            startup.Set(false, @"C:\Apps\IDM-Rea\engine\IDMRea.Engine.exe");
            Assert.Null(Value());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\IDMRea-Tests", false); }
    }

    /// <summary>Each run leaves its test file in the real Recycle Bin, so it runs only where that is wanted (CI sets
    /// IDMREA_TEST_RECYCLE=1).</summary>
    [Fact]
    public void RecycleBinSendRemovesFile()
    {
        if (Environment.GetEnvironmentVariable("IDMREA_TEST_RECYCLE") != "1") return;
        var path = Path.Combine(Path.GetTempPath(), "idmrea-recycle-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(path, "recycle me");
        Assert.True(RecycleBin.Send(path));
        Assert.False(File.Exists(path));
        Assert.False(RecycleBin.Send(path + ".missing"));
    }

    [Fact]
    public void ToastXmlForCompletedAndFailed()
    {
        var id = Guid.NewGuid();
        var done = new DownloadJob(id, "https://example.test/a", @"C:\Downloads\Video\a & b.mp4", @"C:\Downloads\Video\a.part", DownloadStatus.Completed, 10, 10);
        var xml = Toasts.BuildXml(done);
        Assert.Contains("a &amp; b.mp4", xml);
        Assert.Contains("Download finished", xml);
        Assert.Contains($"arguments=\"open|{id}\"", xml);
        Assert.Contains($"arguments=\"folder|{id}\"", xml);
        Assert.DoesNotContain($"arguments=\"details|{id}\"", xml);

        var failed = done with { Status = DownloadStatus.Failed, ErrorKind = ErrorKind.Network };
        var bad = Toasts.BuildXml(failed);
        Assert.Contains("Can&apos;t reach the server. Check your connection.", bad);
        Assert.Contains($"arguments=\"details|{id}\"", bad);
        Assert.DoesNotContain($"open|{id}", bad);
        Assert.Equal(1, bad.Split("<action ").Length - 1);
        Assert.Equal(2, xml.Split("<action ").Length - 1);
        Assert.DoesNotContain('\u2014', xml + bad);
        _ = System.Xml.Linq.XDocument.Parse(xml); _ = System.Xml.Linq.XDocument.Parse(bad);
    }
}

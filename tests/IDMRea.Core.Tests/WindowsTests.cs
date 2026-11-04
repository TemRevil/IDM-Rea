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

    private static IEnumerable<TrayMenuItem> Flat(IEnumerable<TrayMenuItem> items) => items.SelectMany(i => (i.Items ?? []).Prepend(i));
    private static TrayMenuItem Item(IEnumerable<TrayMenuItem> items, string text) => Flat(items).Single(i => i.Text == text);

    [Fact]
    public void TrayMenuShowsStatusAndOnlyActionsThatApply()
    {
        var idle = TrayMenu.Build(new TrayState(0, 0, CanPause: false, CanResume: false, SpeedLimit: 0));
        Assert.Equal("No downloads running", idle[0].Text); Assert.False(idle[0].Enabled);
        Assert.True(Item(idle, "Open IDM-Rea").Default);
        Assert.False(Item(idle, "Pause all").Enabled); Assert.False(Item(idle, "Resume all").Enabled);
        Assert.True(Item(idle, "No limit").Checked);
        Assert.Equal(["Open IDM-Rea", "Add from clipboard", "Pause all", "Resume all", "Speed limit", "Open downloads folder", "Settings", "Quit"],
            idle.Skip(1).Where(i => !i.Separator).Select(i => i.Text));

        var busy = TrayMenu.Build(new TrayState(3, 5 * 1024 * 1024, CanPause: true, CanResume: true, SpeedLimit: 2 * 1024 * 1024));
        Assert.Equal("3 downloading, 5.0 MB/s", busy[0].Text);
        Assert.True(Item(busy, "Pause all").Enabled); Assert.True(Item(busy, "Resume all").Enabled);
        Assert.Equal(["2 MB/s"], Flat(busy).Where(i => i.Checked).Select(i => i.Text));
        Assert.DoesNotContain(Flat(busy), i => i.Text.Contains('\u2014'));
    }

    [Fact]
    public void TrayMenuKeepsACustomLimitVisible()
    {
        var menu = TrayMenu.Build(new TrayState(0, 0, false, false, SpeedLimit: 3 * 1024 * 1024));
        var custom = Assert.Single(Flat(menu), i => i.Checked);
        Assert.Equal("3.0 MB/s (from Settings)", custom.Text); Assert.False(custom.Enabled);
    }

    [Fact]
    public void TrayMenuIdsDecodeToCommands()
    {
        var menu = TrayMenu.Build(new TrayState(1, 0, true, true, 0));
        Assert.Equal(new TrayChoice(TrayCommand.Open), TrayMenu.Decode(Item(menu, "Open IDM-Rea").Id));
        Assert.Equal(new TrayChoice(TrayCommand.OpenFolder), TrayMenu.Decode(Item(menu, "Open downloads folder").Id));
        Assert.Equal(new TrayChoice(TrayCommand.Settings), TrayMenu.Decode(Item(menu, "Settings").Id));
        Assert.Equal(new TrayChoice(TrayCommand.SpeedLimit, 512 * 1024), TrayMenu.Decode(Item(menu, "512 KB/s").Id));
        Assert.Equal(new TrayChoice(TrayCommand.SpeedLimit, 0), TrayMenu.Decode(Item(menu, "No limit").Id));
        Assert.Equal(new TrayChoice(TrayCommand.SpeedLimit, 10 * 1024 * 1024), TrayMenu.Decode(Item(menu, "10 MB/s").Id));
        Assert.Null(TrayMenu.Decode(0)); Assert.Null(TrayMenu.Decode(9999));
    }
}

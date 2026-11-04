using IDMRea.Contracts;
using IDMRea.Engine.Windows;
using Microsoft.Win32;
using Xunit;
namespace IDMRea.Tests;

public sealed class WindowsTests
{
    [Fact]
    public void BrowserRegistrationWritesManifestKeysAndTarget()
    {
        const string prefix = @"Software\IDMRea-Tests";
        var dir = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid());
        var data = Path.Combine(dir, "data"); var home = Path.Combine(dir, "home");
        var registration = new BrowserRegistration(prefix);
        string? Key(string browser) { using var key = Registry.CurrentUser.OpenSubKey($@"{prefix}\{browser}\NativeMessagingHosts\com.idmrea.host"); return key?.GetValue("") as string; }
        string HostPath(string manifest) { using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)); return doc.RootElement.GetProperty("path").GetString()!; }
        try
        {
            registration.Register(@"C:\Apps\IDM-Rea\engine\IDMRea.BrowserHost.exe", data, home);
            var manifest = Path.Combine(data, "browser", "com.idmrea.host.json");
            Assert.All(new[] { @"Google\Chrome", @"Microsoft\Edge", "Chromium" }, b => Assert.Equal(manifest, Key(b)));
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)))
            {
                var root = doc.RootElement;
                Assert.Equal("com.idmrea.host", root.GetProperty("name").GetString());
                Assert.Equal("stdio", root.GetProperty("type").GetString());
                Assert.Equal("chrome-extension://ioenfhcbkjnjlpkmjngfhohmimmjgllg/", Assert.Single(root.GetProperty("allowed_origins").EnumerateArray()).GetString());
            }
            Assert.Equal(@"C:\Apps\IDM-Rea\engine\IDMRea.BrowserHost.exe", HostPath(manifest));
            Assert.Equal(Path.GetFullPath(data), File.ReadAllText(Path.Combine(home, "browser-target.txt")).Trim());

            // Another data folder (a test run) never takes the registration over; the owner's own folder refreshes it.
            Assert.False(registration.RefreshIfOwned(@"D:\Other\IDMRea.BrowserHost.exe", Path.Combine(dir, "other"), home));
            Assert.Equal(@"C:\Apps\IDM-Rea\engine\IDMRea.BrowserHost.exe", HostPath(manifest));
            Assert.True(registration.RefreshIfOwned(@"D:\Moved\IDMRea.BrowserHost.exe", data, home));
            Assert.Equal(@"D:\Moved\IDMRea.BrowserHost.exe", HostPath(manifest));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(prefix, false); }
    }

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

    [Fact]
    public void StartupEntryNamesADataFolderOtherThanTheDefault()
    {
        const string key = @"Software\IDMRea-Tests\Run";
        var startup = new StartupRegistration(key, "IDM-Rea");
        try
        {
            startup.Set(true, @"C:\Apps\engine\IDMRea.Engine.exe", @"G:\Data\IDMRea");
            using var run = Registry.CurrentUser.OpenSubKey(key);
            Assert.Equal(@"""C:\Apps\engine\IDMRea.Engine.exe"" --tray --data-directory ""G:\Data\IDMRea""", run?.GetValue("IDM-Rea"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\IDMRea-Tests", false); }
    }

    [Fact]
    public void AnEngineOnlyRemovesItsOwnStartupEntry()
    {
        const string key = @"Software\IDMRea-Tests\Run";
        var startup = new StartupRegistration(key, "IDM-Rea");
        object? Value() { using var run = Registry.CurrentUser.OpenSubKey(key); return run?.GetValue("IDM-Rea"); }
        const string exe = @"C:\Apps\engine\IDMRea.Engine.exe";
        try
        {
            startup.Set(true, exe, @"G:\Data\IDMRea");
            startup.Set(false, exe, @"G:\Test\capture-data");   // a test engine on another folder
            startup.Set(false, exe);                            // the default folder's engine
            startup.Set(false, @"D:\Other\engine\IDMRea.Engine.exe", @"G:\Data\IDMRea");
            Assert.NotNull(Value());
            startup.Set(false, exe, @"g:\data\idmrea");
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

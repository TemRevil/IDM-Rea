using IDMRea.Contracts;
using IDMRea.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace IDMRea.Tests;

public sealed class StoreTests
{
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-store-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static void Exec(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    [Fact]
    public void MigratesV1WithoutTouchingIt()
    {
        var dir = Temp(); var v1 = Path.Combine(dir, "jobs.db");
        var id = Guid.NewGuid();
        Exec(v1, "PRAGMA journal_mode=DELETE; CREATE TABLE jobs(id TEXT PRIMARY KEY,payload TEXT NOT NULL); PRAGMA user_version=1;" +
            $"INSERT INTO jobs VALUES('{id}','{{\"id\":\"{id}\",\"url\":\"https://e.com/f.bin\",\"destination\":\"/d/f.bin\",\"partialPath\":\"/d/f.part\",\"status\":\"Paused\",\"receivedBytes\":7}}');");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(v1); var stamp = File.GetLastWriteTimeUtc(v1);
        var store = new JobStore(dir);
        var job = Assert.Single(store.Load());
        Assert.Equal(id, job.Id); Assert.Equal(7, job.ReceivedBytes); Assert.Null(job.Parts);
        Assert.True(File.Exists(Path.Combine(dir, "jobs2.db")));
        store.Save(job with { ReceivedBytes = 9 });
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(v1)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(v1));
        Assert.Equal(9, Assert.Single(new JobStore(dir).Load()).ReceivedBytes);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var dir = Temp(); var store = new JobStore(dir);
        Assert.Equal(new EngineSettings().PartsPerFile, store.LoadSettings().PartsPerFile);
        store.SaveSettings(new EngineSettings(PartsPerFile: 12, UltraPerformance: true, Theme: AppTheme.Dark, Folders: new Dictionary<Category, string> { [Category.Video] = "/v" }));
        var loaded = new JobStore(dir).LoadSettings();
        Assert.Equal(12, loaded.PartsPerFile); Assert.True(loaded.UltraPerformance); Assert.Equal(AppTheme.Dark, loaded.Theme); Assert.Equal("/v", loaded.FolderFor(Category.Video));
    }

    [Fact]
    public void RefusesNewerSchema()
    {
        var dir = Temp(); _ = new JobStore(dir); SqliteConnection.ClearAllPools();
        Exec(Path.Combine(dir, "jobs2.db"), "PRAGMA user_version=3;"); SqliteConnection.ClearAllPools();
        var ex = Assert.Throws<InvalidDataException>(() => new JobStore(dir));
        Assert.Equal("This data was saved by a newer version of IDM-Rea.", ex.Message);
    }

    [Fact]
    public void DeleteRemovesRow()
    {
        var dir = Temp(); var store = new JobStore(dir); var id = Guid.NewGuid();
        store.Save(new DownloadJob(id, "https://e.com/a", "/d/a", "/d/a.part", DownloadStatus.Completed));
        store.Delete(id);
        Assert.Empty(new JobStore(dir).Load());
    }
}

using IDMRea.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace IDMRea.Core;

/// <summary>
/// Durable job and settings storage in <c>jobs2.db</c>. A version-1 <c>jobs.db</c> is copied in once,
/// opened read-only, and never written, so an older build can still use its own data.
/// </summary>
public sealed class JobStore
{
    public const int SchemaVersion = 2;
    private readonly string connection;
    public JobStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "jobs2.db"); var legacy = Path.Combine(directory, "jobs.db");
        var migrate = !File.Exists(file) && File.Exists(legacy);
        connection = new SqliteConnectionStringBuilder { DataSource = file }.ToString();
        using var db = Open();
        using (var version = db.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > SchemaVersion) throw new InvalidDataException("This data was saved by a newer version of IDM-Rea.");
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                "CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,payload TEXT NOT NULL); " +
                "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL); " +
                $"PRAGMA user_version={SchemaVersion};";
            cmd.ExecuteNonQuery();
        }
        if (migrate) Migrate(db, legacy);
    }

    private static void Migrate(SqliteConnection db, string legacy)
    {
        var rows = new List<(string Id, string Payload)>();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            old.Open(); using var read = old.CreateCommand(); read.CommandText = "SELECT id,payload FROM jobs";
            using var reader = read.ExecuteReader(); while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        using var tx = db.BeginTransaction();
        foreach (var (id, payload) in rows)
        {
            using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO jobs VALUES($id,$payload)";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$payload", payload); insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }

    public IReadOnlyList<DownloadJob> Load()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM jobs";
        using var rows = cmd.ExecuteReader(); var jobs = new List<DownloadJob>();
        while (rows.Read()) jobs.Add(JsonSerializer.Deserialize<DownloadJob>(rows.GetString(0), Protocol.Json)!);
        return jobs;
    }

    public void Save(DownloadJob job)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO jobs VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload";
        cmd.Parameters.AddWithValue("$id", job.Id.ToString()); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job, Protocol.Json)); cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
    }

    public EngineSettings LoadSettings()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key='engine'";
        return cmd.ExecuteScalar() is string json ? (JsonSerializer.Deserialize<EngineSettings>(json, Protocol.Json) ?? new()).Clamped() : new EngineSettings();
    }

    public void SaveSettings(EngineSettings settings)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings VALUES('engine',$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings.Clamped(), Protocol.Json)); cmd.ExecuteNonQuery();
    }
}

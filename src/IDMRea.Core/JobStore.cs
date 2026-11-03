using IDMRea.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace IDMRea.Core;
public sealed class JobStore
{
    private readonly string connection;
    public JobStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "jobs.db") }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,payload TEXT NOT NULL); PRAGMA user_version=1;";
        cmd.ExecuteNonQuery();
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
}

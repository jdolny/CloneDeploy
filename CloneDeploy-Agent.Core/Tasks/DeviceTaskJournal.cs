using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

/// <summary>
/// Saves deployment progress locally so the agent can recover and report results after a restart.
/// </summary>
public sealed class DeviceTaskJournal
{
    private readonly string connectionString;

    public DeviceTaskJournal(AgentOptions options)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.StateDirectory, "device-tasks.db"),
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        if (Convert.ToInt32(command.ExecuteScalar()) > 1)
            throw new InvalidDataException("Newer task journal schema.");

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TaskRuns (
                TaskId TEXT NOT NULL, LeaseToken TEXT NOT NULL, DeviceGuid TEXT NOT NULL,
                State TEXT NOT NULL CHECK (State IN ('Prepared','Running','Completed','Interrupted','Abandoned')),
                StartedAtUtc TEXT NOT NULL, FinishedAtUtc TEXT, Success INTEGER, Detail TEXT,
                Reported INTEGER NOT NULL, PRIMARY KEY (TaskId, LeaseToken));
            CREATE INDEX IF NOT EXISTS IX_TaskRuns_Pending ON TaskRuns(DeviceGuid, Reported, StartedAtUtc);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();

        return db;
    }

    public List<DeviceTaskRun> Pending(Guid deviceGuid)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT TaskId, LeaseToken, State, StartedAtUtc, FinishedAtUtc, Success, Detail FROM TaskRuns WHERE DeviceGuid=$device AND Reported=0 ORDER BY StartedAtUtc";
        command.Parameters.AddWithValue("$device", deviceGuid.ToString());
        using var reader = command.ExecuteReader();
        var runs = new List<DeviceTaskRun>();

        while (reader.Read())
            runs.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                deviceGuid,
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4)
                    ? null
                    : DateTime.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));

        return runs;
    }

    public bool HasExecution(Guid taskId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM TaskRuns WHERE TaskId=$id AND State IN ('Running','Completed','Interrupted'))";
        command.Parameters.AddWithValue("$id", taskId.ToString());

        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public void Save(DeviceTaskRun run)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskRuns (TaskId,LeaseToken,DeviceGuid,State,StartedAtUtc,FinishedAtUtc,Success,Detail,Reported)
            VALUES ($id,$token,$device,$state,$started,$finished,$success,$detail,$reported)
            ON CONFLICT(TaskId,LeaseToken) DO UPDATE SET State=excluded.State, FinishedAtUtc=excluded.FinishedAtUtc,
                Success=excluded.Success, Detail=excluded.Detail, Reported=excluded.Reported
            WHERE TaskRuns.DeviceGuid=excluded.DeviceGuid AND TaskRuns.StartedAtUtc=excluded.StartedAtUtc
            """;
        command.Parameters.AddWithValue("$id", run.TaskId.ToString());
        command.Parameters.AddWithValue("$token", run.LeaseToken.ToString());
        command.Parameters.AddWithValue("$device", run.DeviceGuid.ToString());
        command.Parameters.AddWithValue("$state", run.State);
        command.Parameters.AddWithValue("$started", run.StartedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$finished",
            (object?)run.FinishedAtUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", (object?)run.Success ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)run.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$reported", run.Reported);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("Task run identity cannot change.");
    }
}
using System.Globalization;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

// Separate connections avoid sharing the identity store's connection with SignalR callbacks.
/// <summary>
/// Keeps a local history of policy runs so restarts do not lose results or repeat interrupted work.
/// </summary>
public sealed class PolicyJournal
{
    private const int SchemaVersion = 3;
    private readonly string connectionString;

    public PolicyJournal(AgentOptions options)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.StateDirectory, "policies.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var version = db.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());

        if (current > SchemaVersion)
            throw new InvalidDataException("The policy journal was created by a newer agent.");

        if (current == SchemaVersion)
        {
            transaction.Commit();

            return;
        }

        if (current != 0)
            throw new InvalidDataException("Unsupported policy journal schema. A fresh agent installation is required.");

        Execute(
            db,
            transaction,
            """
            CREATE TABLE PolicySnapshots (
                Hash TEXT PRIMARY KEY COLLATE NOCASE,
                SchemaVersion INTEGER NOT NULL,
                PolicyJson TEXT NOT NULL
            );
            CREATE TABLE Runs (
                RunId TEXT PRIMARY KEY,
                DeviceGuid TEXT NOT NULL,
                PolicyId INTEGER NOT NULL,
                PolicyGuid TEXT NOT NULL,
                SnapshotHash TEXT NOT NULL COLLATE NOCASE REFERENCES PolicySnapshots(Hash),
                ReRunExisting INTEGER NOT NULL CHECK (ReRunExisting IN (0, 1)),
                State TEXT NOT NULL CHECK (State IN ('Prepared', 'Running', 'Completed', 'Interrupted')),
                StartedAtUtc TEXT NOT NULL,
                FinishedAtUtc TEXT,
                Result INTEGER CHECK (Result BETWEEN 0 AND 3),
                Detail TEXT,
                Reported INTEGER NOT NULL CHECK (Reported IN (0, 1)),
                UserName TEXT, UserSid TEXT, SelfService INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IX_Runs_Recovery ON Runs(DeviceGuid, State);
            CREATE INDEX IX_Runs_PendingReports ON Runs(DeviceGuid, StartedAtUtc)
                WHERE Reported = 0 AND Result IS NOT NULL;
            CREATE INDEX IX_Runs_LastCompleted ON Runs(DeviceGuid, PolicyGuid, UserName, FinishedAtUtc DESC)
                WHERE Result IN (0, 3) AND FinishedAtUtc IS NOT NULL;
            CREATE INDEX IX_Runs_CompletedHash ON Runs(DeviceGuid, PolicyGuid, UserName, SnapshotHash)
                WHERE Result IN (0, 3) AND FinishedAtUtc IS NOT NULL;
            CREATE INDEX IX_Runs_InterruptedHash ON Runs(DeviceGuid, PolicyGuid, UserName, SnapshotHash)
                WHERE State = 'Interrupted';
            """);

        CreateLoginSchema(db, transaction);
        transaction.Commit();
    }

    private static void CreateLoginSchema(SqliteConnection db, SqliteTransaction transaction) => Execute(
        db,
        transaction,
        """
        CREATE TABLE LoginTriggers (DeviceGuid TEXT NOT NULL, LogonKey TEXT NOT NULL, Created TEXT NOT NULL,
            PRIMARY KEY(DeviceGuid, LogonKey));
        CREATE INDEX IX_Runs_UserSid ON Runs(DeviceGuid, PolicyGuid, UserSid, SnapshotHash);
        PRAGMA user_version = 3;
        """);

    public bool ClaimLogin(Guid deviceGuid, string logonKey)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO LoginTriggers VALUES ($device,$logon,$time)";
        command.Parameters.AddWithValue("$device", deviceGuid.ToString());
        command.Parameters.AddWithValue("$logon", logonKey);
        command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));

        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();

        return db;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Full entries are only needed for recovery/reporting (and diagnostic callers).
    public bool Contains(Guid runId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Runs WHERE RunId = $id)";
        command.Parameters.AddWithValue("$id", runId.ToString());

        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public List<PolicyRunEntry> Read() => ReadEntries(null, "1 = 1");

    public List<PolicyRunEntry> ReadActive(Guid deviceGuid) =>
        ReadEntries(deviceGuid, "r.State IN ('Prepared', 'Running')");

    public List<PolicyRunEntry> ReadPendingReports(Guid deviceGuid) =>
        ReadEntries(deviceGuid, "r.Reported = 0 AND r.Result IS NOT NULL");

    // Reporting needs admission/result metadata, not the potentially large frozen policy JSON.
    public List<(AgentPolicyStart Start, AgentPolicyResult Result)> ReadReportPayloads(Guid deviceGuid)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT RunId, PolicyId, SnapshotHash, UserName, UserSid, SelfService, Result, Detail
            FROM Runs WHERE DeviceGuid=$device AND Reported=0 AND Result IS NOT NULL
            ORDER BY StartedAtUtc
            """;
        command.Parameters.AddWithValue("$device", deviceGuid.ToString());
        using var reader = command.ExecuteReader();
        var reports = new List<(AgentPolicyStart, AgentPolicyResult)>();

        while (reader.Read())
        {
            var runId = Guid.Parse(reader.GetString(0));
            reports.Add((new AgentPolicyStart(
                    runId,
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3))
                {
                    UserSid = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SelfService = reader.GetBoolean(5), SupportsNotificationRecipients = true
                },
                new AgentPolicyResult(runId, reader.GetInt32(6), reader.IsDBNull(7) ? "" : reader.GetString(7))));
        }

        return reports;
    }

    private List<PolicyRunEntry> ReadEntries(Guid? deviceGuid, string predicate)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        // Predicates are internal constants, never caller-supplied SQL.
        command.CommandText = $"""
            SELECT r.RunId, r.DeviceGuid, r.SnapshotHash, r.ReRunExisting, r.State,
                r.StartedAtUtc, r.FinishedAtUtc, r.Result, r.Detail, r.Reported, r.UserName,
                s.SchemaVersion, s.PolicyJson, r.UserSid, r.SelfService
            FROM Runs r JOIN PolicySnapshots s ON s.Hash = r.SnapshotHash
            WHERE{(deviceGuid.HasValue ? " r.DeviceGuid = $device AND " : " ")}{predicate} ORDER BY r.StartedAtUtc
            """;

        if (deviceGuid.HasValue)
            command.Parameters.AddWithValue("$device", deviceGuid.Value.ToString());

        using var reader = command.ExecuteReader();
        var entries = new List<PolicyRunEntry>();

        while (reader.Read())
        {
            var snapshot = new AgentPolicySnapshot(
                reader.GetInt32(11),
                reader.GetString(2),
                reader.GetBoolean(3),
                JsonSerializer.Deserialize<JsonElement>(reader.GetString(12)));
            entries.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                snapshot,
                reader.GetString(4),
                ParseTime(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            {
                UserSid = reader.IsDBNull(13) ? null : reader.GetString(13),
                SelfService = reader.GetBoolean(14)
            });
        }

        return entries;
    }

    public PolicyHistoryState ReadHistory(
        Guid deviceGuid,
        Guid policyGuid,
        string hash,
        string? userName = null)
        => ReadHistoryCore(deviceGuid, policyGuid, hash, userName, false);

    public PolicyHistoryState ReadLoginHistory(Guid deviceGuid, Guid policyGuid, string hash, string sid)
        => ReadHistoryCore(deviceGuid, policyGuid, hash, sid, true);

    private PolicyHistoryState ReadHistoryCore(
        Guid deviceGuid,
        Guid policyGuid,
        string hash,
        string? userName,
        bool bySid)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT FinishedAtUtc FROM Runs
                 WHERE DeviceGuid = $device AND PolicyGuid = $policy AND SelfService = 0 AND UserName IS $user
                    AND Result IN (0, 3) AND FinishedAtUtc IS NOT NULL
                 ORDER BY FinishedAtUtc DESC LIMIT 1),
                EXISTS(SELECT 1 FROM Runs
                 WHERE DeviceGuid = $device AND PolicyGuid = $policy AND SelfService = 0 AND UserName IS $user AND SnapshotHash = $hash
                    AND Result IN (0, 3) AND FinishedAtUtc IS NOT NULL),
                EXISTS(SELECT 1 FROM Runs
                 WHERE DeviceGuid = $device AND PolicyGuid = $policy AND SelfService = 0 AND UserName IS $user AND SnapshotHash = $hash
                    AND State = 'Interrupted'),
                (SELECT StartedAtUtc FROM Runs
                 WHERE DeviceGuid = $device AND PolicyGuid = $policy AND SelfService = 0 AND UserName IS $user
                    AND Result IN (0, 3) AND FinishedAtUtc IS NOT NULL
                 ORDER BY FinishedAtUtc DESC LIMIT 1)
            """;

        if (bySid)
            command.CommandText = command.CommandText.Replace("UserName IS $user", "UserSid IS $user");

        command.Parameters.AddWithValue("$device", deviceGuid.ToString());
        command.Parameters.AddWithValue("$policy", policyGuid.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$user", (object?)userName ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        reader.Read();

        return new(
            reader.IsDBNull(0) ? null : ParseTime(reader.GetString(0)),
            reader.GetBoolean(1),
            reader.GetBoolean(2))
        {
            LastStartedAtUtc = reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3))
        };
    }

    public void MarkReported(Guid runId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE Runs SET Reported = 1 WHERE RunId = $id AND Result IS NOT NULL";
        command.Parameters.AddWithValue("$id", runId.ToString());
        command.ExecuteNonQuery();
    }

    public void Save(PolicyRunEntry entry)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        Save(db, transaction, entry);
        transaction.Commit();
    }

    private static void Save(SqliteConnection db, SqliteTransaction transaction, PolicyRunEntry entry)
    {
        var policy = PolicySelection.Validate(entry.Snapshot);

        if (entry.RunId == Guid.Empty || entry.DeviceGuid == Guid.Empty)
            throw new InvalidDataException("Policy run identity is required.");

        using var snapshot = db.CreateCommand();
        snapshot.Transaction = transaction;
        snapshot.CommandText = """
            INSERT INTO PolicySnapshots (Hash, SchemaVersion, PolicyJson) VALUES ($hash, $version, $json)
            ON CONFLICT(Hash) DO NOTHING
            """;
        snapshot.Parameters.AddWithValue("$hash", entry.Snapshot.Hash);
        snapshot.Parameters.AddWithValue("$version", entry.Snapshot.SchemaVersion);
        snapshot.Parameters.AddWithValue("$json", entry.Snapshot.Policy.GetRawText());
        snapshot.ExecuteNonQuery();

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Runs (RunId, DeviceGuid, PolicyId, PolicyGuid, SnapshotHash, ReRunExisting, State,
                StartedAtUtc, FinishedAtUtc, Result, Detail, Reported, UserName, UserSid, SelfService)
            VALUES ($id, $device, $policyId, $policyGuid, $hash, $rerun, $state, $started, $finished, $result, $detail, $reported, $user, $sid, $selfService)
            ON CONFLICT(RunId) DO UPDATE SET State = excluded.State, FinishedAtUtc = excluded.FinishedAtUtc,
                Result = excluded.Result, Detail = excluded.Detail, Reported = excluded.Reported
            WHERE Runs.DeviceGuid = excluded.DeviceGuid AND Runs.PolicyGuid = excluded.PolicyGuid
                AND Runs.PolicyId = excluded.PolicyId AND Runs.SnapshotHash = excluded.SnapshotHash
                AND Runs.ReRunExisting = excluded.ReRunExisting AND Runs.UserName IS excluded.UserName
                AND Runs.StartedAtUtc = excluded.StartedAtUtc AND Runs.UserSid IS excluded.UserSid AND Runs.SelfService = excluded.SelfService
            """;
        command.Parameters.AddWithValue("$id", entry.RunId.ToString());
        command.Parameters.AddWithValue("$device", entry.DeviceGuid.ToString());
        command.Parameters.AddWithValue("$policyId", policy.Id);
        command.Parameters.AddWithValue("$policyGuid", policy.Guid.ToString());
        command.Parameters.AddWithValue("$hash", entry.Snapshot.Hash);
        command.Parameters.AddWithValue("$rerun", entry.Snapshot.ReRunExisting);
        command.Parameters.AddWithValue("$state", entry.State);
        command.Parameters.AddWithValue("$started", FormatTime(entry.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$finished",
            entry.FinishedAtUtc.HasValue ? FormatTime(entry.FinishedAtUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)entry.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)entry.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$reported", entry.Reported);
        command.Parameters.AddWithValue("$user", (object?)entry.UserName ?? DBNull.Value);
        command.Parameters.AddWithValue("$selfService", entry.SelfService);
        command.Parameters.AddWithValue("$sid", (object?)entry.UserSid ?? DBNull.Value);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("A policy run's identity or snapshot cannot change.");
    }

    private static string FormatTime(DateTime value) => value.ToUniversalTime().ToString(
        "O",
        CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

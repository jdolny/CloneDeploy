using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

/// <summary>Durable notices and per-user acknowledgment receipts; delivery never waits for a user.</summary>
public sealed class PortalNotificationJournal
{
    private const int MaximumTitleCharacters = 200;
    private const int MaximumBodyCharacters = 2000;
    private const int MaximumTimeoutSeconds = 86400;

    private readonly string connectionString;

    public PortalNotificationJournal(AgentOptions options)
    {
        Directory.CreateDirectory(options.StateDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.StateDirectory, "portal-notifications.db"),
            Pooling = false
        }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version == 1) { tx.Commit(); return; }
        if (version != 0) throw new InvalidDataException("Unsupported notification journal schema.");
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 0)
            throw new InvalidDataException("Unsupported notification journal schema. A fresh agent installation is required.");
        cmd.CommandText = """
            CREATE TABLE Notifications (Id TEXT PRIMARY KEY, Created INTEGER NOT NULL, Title TEXT NOT NULL,
                Body TEXT NOT NULL, Timeout INTEGER NOT NULL, RequireAck INTEGER NOT NULL DEFAULT 0,
                Remind INTEGER NOT NULL DEFAULT 0, Position INTEGER NOT NULL DEFAULT 0, TargetSid TEXT,
                Branding INTEGER NOT NULL DEFAULT 0, Kind INTEGER NOT NULL DEFAULT 0, Expires INTEGER);
            CREATE TABLE NotificationReceipts (NotificationId TEXT NOT NULL, Sid TEXT NOT NULL, UserName TEXT NOT NULL,
                Acknowledged INTEGER, Revision TEXT NOT NULL, Reported INTEGER NOT NULL DEFAULT 0,
                LastChecked INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (NotificationId,Sid));
            CREATE TABLE NotificationTargets (NotificationId TEXT NOT NULL, Sid TEXT NOT NULL, PRIMARY KEY(NotificationId,Sid));
            CREATE INDEX IX_Notifications_OrdinaryHistory ON Notifications(Created DESC, Id DESC) WHERE RequireAck=0;
            CREATE INDEX IX_NotificationReceipts_Pending ON NotificationReceipts(NotificationId) WHERE Reported=0;
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();

        return db;
    }

    public void Enqueue(Guid runId, Guid actionId, string title, string body, int timeout)
        => Store(new($"{runId:N}:{actionId:N}", DateTimeOffset.UtcNow, title, body, timeout));

    public void StoreForRecipients(AgentPortalNotification message, string[]? recipients, string? userSid = null)
    {
        if (recipients is null) { Store(message, userSid); return; }
        if (recipients.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 184 || !x.StartsWith("S-1-", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid notification recipients.");
        var selected = recipients.Distinct(StringComparer.Ordinal).Where(x => userSid is null || x == userSid).ToArray();
        // No matches means no delivery, never an unrestricted notification.
        if (selected.Length == 0) return;
        Store(message, selected[0]);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT OR IGNORE INTO NotificationTargets (NotificationId,Sid) VALUES ($id,$sid)";
        command.Parameters.AddWithValue("$id", message.Id);
        var sidParameter = command.Parameters.AddWithValue("$sid", "");
        foreach (var sid in selected) { sidParameter.Value = sid; command.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void Store(AgentPortalNotification message, string? targetSid = null)
    {
        if (string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 80
            || string.IsNullOrWhiteSpace(message.Title)
            || message.Title.Length > MaximumTitleCharacters
            ||
            string.IsNullOrWhiteSpace(message.Body)
            || message.Body.Length > MaximumBodyCharacters
            || message.TimeoutSeconds is < 0 or > MaximumTimeoutSeconds
            || !Enum.IsDefined(message.Position)
            || !Enum.IsDefined(message.Branding)
            || !Enum.IsDefined(message.Kind))
            throw new InvalidDataException("Invalid portal message settings.");

        if (message.RequireAcknowledgment && message.ExpiresAt is null)
            message = message with { ExpiresAt = message.CreatedAt.AddDays(7) };
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO Notifications (Id,Created,Title,Body,Timeout,RequireAck,Remind,Position,TargetSid,Branding,Kind,Expires) VALUES ($id,$time,$title,$body,$timeout,$ack,$remind,$position,$sid,$branding,$kind,$expires)";
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$time", message.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$title", message.Title);
        cmd.Parameters.AddWithValue("$body", message.Body);
        cmd.Parameters.AddWithValue("$timeout", message.TimeoutSeconds == 0 ? 60 : message.TimeoutSeconds);
        cmd.Parameters.AddWithValue("$ack", message.RequireAcknowledgment);
        cmd.Parameters.AddWithValue("$remind", message.RequireAcknowledgment);
        cmd.Parameters.AddWithValue("$branding", (int)message.Branding);
        cmd.Parameters.AddWithValue("$kind", (int)message.Kind);
        cmd.Parameters.AddWithValue("$position", (int)message.Position);
        cmd.Parameters.AddWithValue("$sid", (object?)targetSid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expires", message.ExpiresAt is { } expiry ? expiry.ToUnixTimeMilliseconds() : DBNull.Value);
        cmd.ExecuteNonQuery();

        // A deliberately edited absolute deadline takes effect on redelivery without resetting receipts.
        cmd.CommandText = "UPDATE Notifications SET Expires=$expires WHERE Id=$id";
        cmd.ExecuteNonQuery();
        cmd.CommandText = targetSid is null
            ? "UPDATE Notifications SET TargetSid=NULL WHERE Id=$id"
            : "INSERT OR IGNORE INTO NotificationTargets (NotificationId,Sid) VALUES ($id,$sid)";
        cmd.ExecuteNonQuery();
        if (message.RequireAcknowledgment)
            EnsureReceipt(db, tx, message.Id, "", "");

        tx.Commit();
        Prune(db);
    }

    private static void EnsureReceipt(
        SqliteConnection db,
        SqliteTransaction tx,
        string id,
        string sid,
        string userName)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO NotificationReceipts (NotificationId,Sid,UserName,Revision) VALUES ($id,$sid,$user,$revision)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sid", sid);
        cmd.Parameters.AddWithValue("$user", userName);
        cmd.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
        cmd.ExecuteNonQuery();
    }

    private static void Prune(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds());
        // Avoid acquiring a write lock on every portal poll when there is nothing to remove.
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM Notifications WHERE RequireAck=0 AND Created < $old)
                OR (SELECT COUNT(*) FROM (SELECT 1 FROM Notifications WHERE RequireAck=0 LIMIT 51)) > 50
            """;

        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            return;
        // Required notices must survive the ordinary seven-day / fifty-message history limit.
        cmd.CommandText = "DELETE FROM Notifications WHERE RequireAck=0 AND (Created < $old OR Id NOT IN (SELECT Id FROM Notifications WHERE RequireAck=0 ORDER BY Created DESC, Id DESC LIMIT 50))";
        cmd.ExecuteNonQuery();
    }

    private static AgentPortalNotification ReadNotification(SqliteDataReader r) => new(
        r.GetString(0),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
        r.GetString(2),
        r.GetString(3),
        r.GetInt32(4))
    {
        RequireAcknowledgment = r.GetBoolean(5),
        RemindAtLogin = r.GetBoolean(5),
        Position = (NotificationPosition)r.GetInt32(7),
        Branding = (NotificationBranding)r.GetInt32(9),
        Kind = (NotificationKind)r.GetInt32(10),
        AcknowledgedAt = r.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
        ExpiresAt = r.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(12))
    };

    public AgentPortalNotification[] Read() => ReadForUser("", "");

    public AgentPortalNotification[] ReadForUser(string sid, string userName)
    {
        using var db = Open();
        Prune(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT m.Id,m.Created,m.Title,m.Body,m.Timeout,m.RequireAck,m.Remind,m.Position,r.Acknowledged,m.Branding,m.Kind,r.NotificationId,m.Expires FROM Notifications m LEFT JOIN NotificationReceipts r ON r.NotificationId=m.Id AND r.Sid=$sid WHERE m.TargetSid IS NULL OR EXISTS(SELECT 1 FROM NotificationTargets t WHERE t.NotificationId=m.Id AND t.Sid=$sid) ORDER BY (m.RequireAck=1 AND r.Acknowledged IS NULL AND (m.Expires IS NULL OR m.Expires>$now)) DESC, CASE WHEN m.RequireAck=1 AND r.Acknowledged IS NULL AND (m.Expires IS NULL OR m.Expires>$now) THEN m.Created END ASC,m.Created DESC,m.Id DESC LIMIT 50";
        cmd.Parameters.AddWithValue("$sid", sid);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var messages = new List<AgentPortalNotification>();
        List<string>? missingReceipts = null;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var message = ReadNotification(reader);
                messages.Add(message);

                if (sid.Length > 0 && message.PendingAcknowledgment && reader.IsDBNull(11))
                    (missingReceipts ??= []).Add(message.Id);
            }

        if (missingReceipts is not null)
        {
            using var tx = db.BeginTransaction();

            foreach (var id in missingReceipts)
                EnsureReceipt(db, tx, id, sid, userName);

            tx.Commit();
        }

        return messages.ToArray();
    }

    public bool Acknowledge(string id, string sid, string userName)
    {
        if (string.IsNullOrWhiteSpace(sid))
            return false;

        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM Notifications WHERE Id=$id AND RequireAck=1 AND (TargetSid IS NULL OR EXISTS(SELECT 1 FROM NotificationTargets t WHERE t.NotificationId=Notifications.Id AND t.Sid=$sid)) AND (Expires IS NULL OR Expires>$now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sid", sid);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
            return false;

        EnsureReceipt(db, tx, id, sid, userName);
        cmd.CommandText = "UPDATE NotificationReceipts SET Acknowledged=$time,Revision=$revision,Reported=0 WHERE NotificationId=$id AND Sid=$sid AND Acknowledged IS NULL";
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
        cmd.ExecuteNonQuery();
        tx.Commit();

        return true;
    }

    public AgentNotificationReceipt[] PendingReports(string? userSid = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT m.Id,m.Created,m.Title,m.Body,m.Timeout,m.RequireAck,m.Remind,m.Position,r.Acknowledged,m.Branding,m.Kind,r.Sid,m.Expires,r.UserName,r.Revision FROM NotificationReceipts r JOIN Notifications m ON m.Id=r.NotificationId WHERE ($user IS NULL OR r.Sid=$user) AND (r.Reported=0 OR (r.Sid<>'' AND r.Acknowledged IS NULL AND (m.Expires IS NULL OR m.Expires>$now))) ORDER BY r.LastChecked,m.Created LIMIT 50";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$user", (object?)userSid ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var reports = new List<AgentNotificationReceipt>();

        while (reader.Read())
            reports.Add(new(
                ReadNotification(reader),
                reader.GetString(11),
                reader.GetString(13),
                Guid.Parse(reader.GetString(14))));

        return reports.ToArray();
    }

    public void MarkReported(AgentNotificationReceipt report)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE NotificationReceipts SET Reported=1,LastChecked=$now WHERE NotificationId=$id AND Sid=$sid AND Revision=$revision";
        cmd.Parameters.AddWithValue("$id", report.Notification.Id);
        cmd.Parameters.AddWithValue("$sid", report.UserSid);
        cmd.Parameters.AddWithValue("$revision", report.Revision.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }
    public void ApplyAcknowledgments(IEnumerable<AgentNotificationAcknowledgment> acknowledgments)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var acknowledgment in acknowledgments)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            // Never overwrite local acknowledgement or report a remote acknowledgement as local.
            cmd.CommandText = "UPDATE NotificationReceipts SET Acknowledged=$time,Reported=1 WHERE NotificationId=$id AND Sid=$sid AND Acknowledged IS NULL";
            cmd.Parameters.AddWithValue("$id", acknowledgment.NotificationId);
            cmd.Parameters.AddWithValue("$sid", acknowledgment.UserSid);
            cmd.Parameters.AddWithValue("$time", acknowledgment.AcknowledgedAt.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

}

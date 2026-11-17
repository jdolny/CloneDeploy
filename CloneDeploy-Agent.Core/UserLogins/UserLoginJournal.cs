using CloneDeploy_Agent.Contracts;
using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

/// <summary>
/// Keeps login history on this computer until the server confirms it has been received.
/// </summary>
public sealed class UserLoginJournal
{
    public const int BatchSize = 1000;
    private readonly object gate = new();
    private readonly string connectionString;
    private HashSet<string>? observedUsers;

    public UserLoginJournal(AgentOptions options)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.StateDirectory, "user-logins.db"),
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Logins (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, UserName TEXT NOT NULL,
                Login INTEGER NOT NULL, Logout INTEGER NULL);
            CREATE INDEX IF NOT EXISTS IX_Logins_Open ON Logins(Id) WHERE Logout IS NULL;
            CREATE INDEX IF NOT EXISTS IX_Logins_Pending ON Logins((Logout IS NULL), Id);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();

        return db;
    }

    public void Observe(IReadOnlyCollection<string> users, DateTimeOffset now)
    {
        lock (gate)
        {
            // Only this journal observes sessions. Reconcile after startup and cache only committed observations.
            if (observedUsers is not null && observedUsers.SetEquals(users))
                return;

            using var db = Open();
            using var transaction = db.BeginTransaction();
            var current = users.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var open = Read(db, transaction, "WHERE Logout IS NULL");

            foreach (var login in open)
            {
                if (current.Remove(login.UserName))
                    continue;

                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Logins SET Logout=$time WHERE Id=$id";
                command.Parameters.AddWithValue(
                    "$time",
                    Math.Max(
                        now.ToUnixTimeMilliseconds(),
                        new DateTimeOffset(login.LoggedInAtUtc).ToUnixTimeMilliseconds()));
                command.Parameters.AddWithValue("$id", login.ClientDatabaseLoginId);
                command.ExecuteNonQuery();
            }

            foreach (var user in current)
            {
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO Logins (UserName,Login) VALUES ($user,$time)";
                command.Parameters.AddWithValue("$user", user);
                command.Parameters.AddWithValue("$time", now.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            observedUsers = users.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public List<AgentUserLogin> Pending()
    {
        lock (gate)
        {
            using var db = Open();

            return Read(db, null, $"ORDER BY (Logout IS NULL), Id LIMIT {BatchSize}");
        }
    }

    private static List<AgentUserLogin> Read(
        SqliteConnection db,
        SqliteTransaction? transaction,
        string suffix)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Id,UserName,Login,Logout FROM Logins {suffix}";
        using var reader = command.ExecuteReader();
        var rows = new List<AgentUserLogin>();

        while (reader.Read())
            rows.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime,
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime));

        return rows;
    }

    public void Acknowledge(IEnumerable<AgentUserLogin> submitted)
    {
        lock (gate)
        {
            var completed = submitted.Where(x => x.LoggedOutAtUtc.HasValue).ToList();

            if (completed.Count == 0)
                return;

            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Logins WHERE Id=$id AND Logout=$logout";
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var logout = command.Parameters.Add("$logout", SqliteType.Integer);
            // Only delete the completed version actually sent, never a logout observed during HTTP submission.
            foreach (var login in completed)
            {
                id.Value = login.ClientDatabaseLoginId;
                logout.Value = new DateTimeOffset(login.LoggedOutAtUtc!.Value).ToUnixTimeMilliseconds();
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
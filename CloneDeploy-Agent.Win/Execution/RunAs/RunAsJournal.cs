using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

/// <summary>
/// Remembers temporary account changes so unfinished cleanup can be retried after a restart.
/// </summary>
public sealed class RunAsJournal
{
    private readonly string connectionString;

    public RunAsJournal(AgentOptions options)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.StateDirectory, "run-as.db"),
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        if (Convert.ToInt32(command.ExecuteScalar()) > 1)
            throw new InvalidDataException("Newer run-as recovery schema.");

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Recovery (Id TEXT PRIMARY KEY, AccountSid TEXT NOT NULL,
                MembershipState TEXT NOT NULL CHECK (MembershipState IN ('Unchanged','Adding','Added')));
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();

        return db;
    }

    public List<RunAsRecovery> Read()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Id,AccountSid,MembershipState FROM Recovery";
        using var reader = command.ExecuteReader();
        var rows = new List<RunAsRecovery>();

        while (reader.Read())
            rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));

        return rows;
    }

    public void Save(RunAsRecovery record)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO Recovery VALUES ($id,$sid,$state) ON CONFLICT(Id) DO UPDATE SET MembershipState=excluded.MembershipState WHERE Recovery.AccountSid=excluded.AccountSid";
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$sid", record.AccountSid);
        command.Parameters.AddWithValue("$state", record.MembershipState);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("Run-as account identity changed.");
    }

    public void Delete(Guid id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM Recovery WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }
}
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloneDeploy_Agent;

// One process owns this directory. Both service startup and maintenance commands use this lock.
/// <summary>
/// Saves and protects the agent settings and device identity on this computer.
/// </summary>
public sealed class AgentStore : IDisposable
{
    private readonly FileStream instanceLock;
    private readonly SqliteConnection connection;
    private readonly string directory;

    public AgentStore(string stateDirectory)
    {
        directory = Path.GetFullPath(stateDirectory);
        AgentStatePermissions.Protect(directory);

        instanceLock = new FileStream(
            Path.Combine(directory, "agent.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "agent.db"),
                Pooling = false
            }.ToString());
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";

            if (Convert.ToInt32(version.ExecuteScalar()) > 1)
                throw new InvalidDataException("This agent database was created by a newer version of the agent.");

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Settings (Name TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Identity (Id INTEGER PRIMARY KEY CHECK (Id = 1), Json TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            connection?.Dispose();
            instanceLock.Dispose();
            throw;
        }
    }

    public string? GetSetting(string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Name = $name";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() as string;
    }

    public string? GetProvisioningKey()
    {
        var encrypted = GetSetting("ProvisioningKey");

        if (encrypted is null)
            return null;

        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void SaveConfiguration(AgentOptions options)
        => SaveConfigurationAndIdentity(options, null);

    public void ApplyInstallerConfiguration(AgentOptions options, bool trustChanged)
    {
        var identity = LoadIdentity();
        if (identity is null) { SaveConfiguration(options); return; }
        if (identity.SchemaVersion != 1) throw new InvalidDataException("Unsupported identity version. Update the agent before reconfiguring.");
        var previous = new Uri(identity.ServerUrl);
        var destination = options.GetServerUri();
        var serverChanged = !string.Equals(previous.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            previous.Scheme != destination.Scheme || previous.AbsolutePath != destination.AbsolutePath;
        if (serverChanged || trustChanged)
            ResetIdentity(options, fullReset: true);
        else
            // Port-only moves keep the device key/certificate and atomically move its URL.
            SaveConfigurationAndIdentity(options, identity with { ServerUrl = destination.AbsoluteUri });
    }

    private void SaveConfigurationAndIdentity(AgentOptions options, IdentityState? replacement)
    {
        AgentServerTrust.ValidateConfiguration(options);
        var server = options.GetServerUri().AbsoluteUri;

        if (string.IsNullOrWhiteSpace(options.DeviceName)
            || options.DeviceName.Length > AgentOptions.MaximumDeviceNameLength
            || options.DeviceName.Any(char.IsControl))
            throw new InvalidOperationException("Agent:DeviceName must contain 1–128 printable characters.");

        var identity = LoadIdentity();

        if (replacement is null && identity is not null && identity.ServerUrl != server)
            throw new InvalidDataException("The saved identity belongs to another server. Use a separate state directory, or --resetfull with the destination server's provisioning key to replace enrollment.");

        var key = options.ProvisioningKey;

        if (key.Length != 0 && (key.Length != 64 || !key.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("Agent:ProvisioningKey must contain 64 hexadecimal characters.");

        using var transaction = connection.BeginTransaction();
        SetSetting("ServerUrl", server, transaction);
        SetSetting("DeviceName", options.DeviceName, transaction);
        SetSetting("ServerCaCertificatePath", options.ServerCaCertificatePath ?? "", transaction);

        if (key.Length != 0)
        {
            var bytes = Encoding.UTF8.GetBytes(key);

            try
            {
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                SetSetting("ProvisioningKey", Convert.ToBase64String(encrypted), transaction);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        if (replacement is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Identity (Id, Json) VALUES (1, $json) ON CONFLICT(Id) DO UPDATE SET Json = excluded.Json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(replacement));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void SetSetting(string name, string value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Settings (Name, Value) VALUES ($name, $value) ON CONFLICT(Name) DO UPDATE SET Value = excluded.Value";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public IdentityState? LoadIdentity()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM Identity WHERE Id = 1";
        var json = command.ExecuteScalar() as string;

        return json is null
            ? null
            : JsonSerializer.Deserialize<IdentityState>(json)
              ?? throw new InvalidDataException("Empty agent identity.");
    }

    public void SaveIdentity(IdentityState state)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Identity (Id, Json) VALUES (1, $json) ON CONFLICT(Id) DO UPDATE SET Json = excluded.Json";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
    }


    public void ResetIdentity(AgentOptions options, bool fullReset)
    {
        var previous = LoadIdentity();
        var server = options.GetServerUri().AbsoluteUri;
        var changingServer = previous is not null && previous.ServerUrl != server;

        if (changingServer && !fullReset)
            throw new InvalidDataException("Partial reset cannot change servers. Install a configured MSI for the destination server.");

        if (changingServer && string.IsNullOrEmpty(options.ProvisioningKey))
            throw new InvalidOperationException("Supply the destination server's provisioning key before changing servers.");

        if (previous is not null && previous.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported identity version. Update the agent before resetting.");

        if (string.IsNullOrEmpty(options.ProvisioningKey) && string.IsNullOrEmpty(GetProvisioningKey()))
            throw new InvalidOperationException("Save a provisioning key before resetting enrollment.");

        var installationId = fullReset || previous is null ? Guid.NewGuid() : previous.InstallationId;
        var state = new IdentityState(
            1,
            server,
            changingServer ? options.DeviceName : previous?.DeviceName ?? options.DeviceName,
            installationId,
            $"CloneDeploy.Agent.{Guid.NewGuid().ToString("N")}",
            RequestReset: true);
        SaveConfigurationAndIdentity(options, state);

        // Commit the replacement before removing the old key, so a restart can resume safely.
        if (previous is not null)
            foreach (var name in new[] { previous.KeyName, previous.PendingKeyName, previous.PreviousKeyName })
                DeleteIdentityKey(name);
    }

    public void Dispose()
    {
        connection.Dispose();
        instanceLock.Dispose();
    }

    internal void PrepareForImage()
    {
        var previous = LoadIdentity();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Identity; DELETE FROM Settings WHERE Name IN ('DeviceName', 'ProvisioningKey'); VACUUM;";
        command.ExecuteNonQuery();

        if (previous is not null)
            foreach (var name in new[] { previous.KeyName, previous.PendingKeyName, previous.PreviousKeyName })
                DeleteIdentityKey(name);
    }
    internal static void DeleteIdentityKey(string? name)
    {
        if (name is null || !name.StartsWith("CloneDeploy.Agent.", StringComparison.Ordinal) || !CngKey.Exists(name)) return;
        using var key = CngKey.Open(name);
        key.Delete();
    }
}

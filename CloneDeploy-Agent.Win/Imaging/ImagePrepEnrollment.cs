using System.Text.Json;

namespace CloneDeploy_Agent;

internal static class ImagePrepEnrollment
{
    private const string FileName = "image-enrollment.json";
    private sealed record Enrollment(string ServerUrl, string ProvisioningKey, string? ServerCaCertificatePath);

    // The protected state directory restricts bootstrap credentials to the service identity.
    // Each clone consumes this file before creating its own identity and DPAPI-protected settings.
    internal static void Save(AgentStore store, string directory)
    {
        var key = store.GetProvisioningKey();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("A saved provisioning key is required to reset the agent for imaging.");

        var enrollment = new Enrollment(store.GetSetting("ServerUrl")
            ?? throw new InvalidOperationException("The agent server URL is missing."), key,
            store.GetSetting("ServerCaCertificatePath"));
        var path = Path.Combine(directory, FileName);
        File.WriteAllText($"{path}.tmp", JsonSerializer.Serialize(enrollment));
        File.Move($"{path}.tmp", path, true);
    }

    internal static void Import(AgentStore store, AgentOptions options)
    {
        var path = Path.Combine(options.StateDirectory, FileName);
        if (!File.Exists(path))
            return;

        var enrollment = JsonSerializer.Deserialize<Enrollment>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid image enrollment configuration.");
        store.SaveConfiguration(new AgentOptions
        {
            StateDirectory = options.StateDirectory,
            ServerUrl = enrollment.ServerUrl,
            ProvisioningKey = enrollment.ProvisioningKey,
            ServerCaCertificatePath = enrollment.ServerCaCertificatePath,
            DeviceName = Environment.MachineName
        });
        File.Delete(path);
    }
}

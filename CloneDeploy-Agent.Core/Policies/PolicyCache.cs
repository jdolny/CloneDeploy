using System.Net;
using System.Security.Cryptography;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Downloads and checks the files needed by actions, reusing local copies when they match.
/// </summary>
public sealed class PolicyCache(AgentOptions options)
{
    private const int FileBufferBytes = 65536;

    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromSeconds(30);

    private string Root => Path.GetFullPath(Path.Combine(options.StateDirectory, "policy-cache"));

    public string DirectoryFor(AgentPolicySnapshot snapshot, AgentPolicyAction action) =>
        Path.Combine(Root, snapshot.Hash, action.Guid.ToString("N"));

    public async Task DownloadAsync(
        HttpClient client,
        PolicyRunEntry run,
        AgentPolicyContent policy,
        CancellationToken ct,
        string? contentBasePath = null)
    {
        foreach (var action in AllActions(policy))
        {
            if (action.Files.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                throw new InvalidDataException("An action has duplicate file names.");

            var directory = DirectoryFor(run.Snapshot, action);
            RejectReparsePoints(directory);
            Directory.CreateDirectory(directory);

            foreach (var file in action.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Name) || file.Name != Path.GetFileName(file.Name)
                                                         ||
                                                         file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                                                         || file.Name.EndsWith('.')
                                                         || file.Name.EndsWith(' ')
                                                         ||
                                                         file.Size < 0
                                                         || file.Sha256Hash.Length != 64
                                                         || !file.Sha256Hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid policy file descriptor.");

                var path = Path.Combine(directory, file.Name);
                RejectReparsePoints(path);

                if (await MatchesAsync(path, file, ct))
                    continue;

                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                transfer.CancelAfter(TransferTimeout);
                var temporary = $"{path}.{Guid.NewGuid().ToString("N")}.partial";

                try
                {
                    for (var attempt = 0;; attempt++)
                    {
                        using var response = await AgentResponseVerification.DownloadAsync(client,
                            $"{(contentBasePath ?? $"api/agent/v1/policies/runs/{run.RunId}")}/files/{file.Id}",
                            file.Sha256Hash,
                            transfer.Token);

                        if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 40)
                        {
                            await Task.Delay(response.Headers.RetryAfter?.Delta ?? DownloadRetryDelay, transfer.Token);
                            continue;
                        }

                        response.EnsureSuccessStatusCode();
                        await using (var destination = new FileStream(
                                         temporary,
                                         FileMode.CreateNew,
                                         FileAccess.Write,
                                         FileShare.None,
                                         FileBufferBytes,
                                         true))
                        await using (var source = await response.Content.ReadAsStreamAsync(transfer.Token))
                        {
                            var buffer = new byte[FileBufferBytes];
                            long size = 0;
                            int read;

                            while ((read = await source.ReadAsync(buffer, transfer.Token)) > 0)
                            {
                                size += read;

                                if (size > file.Size)
                                    throw new InvalidDataException("Downloaded file exceeds its published size.");

                                await destination.WriteAsync(buffer.AsMemory(0, read), transfer.Token);
                            }
                        }

                        if (!await MatchesAsync(temporary, file, transfer.Token))
                            throw new InvalidDataException("Downloaded file SHA-256 or size mismatch.");

                        File.Move(temporary, path, true);
                        break;
                    }
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
    }

    private static IEnumerable<AgentPolicyAction> AllActions(AgentPolicyContent policy)
    {
        if (policy.Condition is { } condition) yield return condition;
        foreach (var action in policy.Actions)
        {
            yield return action;
            if (action.Condition is { } nested) yield return nested;
        }
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Policy file path contains a reparse point.");
    }

    public static async Task<bool> MatchesAsync(string path, AgentPolicyFile file, CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes, true);

        return stream.Length == file.Size
               && Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256Hash, StringComparison.OrdinalIgnoreCase);
    }

    public void Remove(AgentPolicySnapshot snapshot)
    {
        var path = Path.GetFullPath(Path.Combine(Root, snapshot.Hash));

        if (!path.StartsWith($"{Root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid cache path.");

        RejectReparsePoints(path);

        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
}

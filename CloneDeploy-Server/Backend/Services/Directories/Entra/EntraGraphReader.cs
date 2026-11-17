using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed class EntraGraphReader(HttpClient http)
{
    public const int MaximumObjects = 200000;
    public const int MaximumMemberships = 2000000;

    public async Task TestAsync(string token, CancellationToken ct, bool includeDevices = true)
    {
        foreach (var collection in (includeDevices ? new[] { "users", "groups", "devices" } : new[] { "users", "groups" }))
            await ReadAsync($"https://graph.microsoft.com/v1.0/{collection}?$select=id&$top=1", token, 1, ct, firstPageOnly: true);
    }

    // A complete, bounded snapshot is committed atomically. A failed page never deletes old data.
    public async Task<DirectorySnapshotDto> ReadSnapshotAsync(int sourceId, Guid tenant, string token, CancellationToken ct, bool includeDevices = true)
    {
        var objects = new List<DirectoryObject>
        {
            new() { DirectorySourceId = sourceId, ObjectGuid = tenant, Kind = DirectoryObjectKind.Domain,
                Name = "Microsoft Entra ID", DistinguishedName = tenant.ToString() }
        };
        var groups = new List<Guid>();
        var hiddenGroups = new HashSet<Guid>();
        var excludedDevices = new HashSet<Guid>();
        foreach (var (collection, kind, select) in new[]
        {
            ("users", DirectoryObjectKind.User, "id,displayName,userPrincipalName,mail,accountEnabled,onPremisesSecurityIdentifier"),
            ("groups", DirectoryObjectKind.Group, "id,displayName,mail,visibility,onPremisesSecurityIdentifier"),
            ("devices", DirectoryObjectKind.Computer, "id,displayName,accountEnabled,deviceId,trustType")
        })
        {
            if (kind == DirectoryObjectKind.Computer && !includeDevices) continue;
            var rows = await ReadAsync($"https://graph.microsoft.com/v1.0/{collection}?$select={select}&$top=999", token, MaximumObjects - objects.Count, ct);
            foreach (var row in rows)
            {
                var id = Guid.Parse(row.GetProperty("id").GetString()!);
                if (id == Guid.Empty) throw new InvalidOperationException("Graph returned an invalid object ID.");
                if (kind == DirectoryObjectKind.Computer && Text(row, "trustType") is not ("AzureAd" or "ServerAd"))
                {
                    excludedDevices.Add(id);
                    continue;
                }
                if (Text(row, "visibility") == "HiddenMembership")
                    hiddenGroups.Add(id);
                objects.Add(new DirectoryObject { DirectorySourceId = sourceId, ObjectGuid = id, ParentGuid = tenant,
                    Kind = kind, Name = Text(row, "displayName") ?? id.ToString(), DistinguishedName = id.ToString(),
                    ObjectSid = Text(row, "onPremisesSecurityIdentifier") ?? (kind == DirectoryObjectKind.User ? CloudSid(id) : null),
                    UserPrincipalName = Text(row, "userPrincipalName"), Email = Text(row, "mail"),
                    Disabled = row.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False });
                if (kind == DirectoryObjectKind.Group) groups.Add(id);
            }
        }
        var known = objects.Select(x => x.ObjectGuid).ToHashSet();
        if (known.Count != objects.Count) throw new InvalidOperationException("Graph returned duplicate directory objects.");
        var memberships = new List<DirectoryGroupMembership>();
        foreach (var group in groups)
        {
            List<JsonElement> members;
            try
            {
                members = await ReadAsync($"https://graph.microsoft.com/v1.0/groups/{group:D}/members?$select=id&$top=999",
                    token, MaximumMemberships - memberships.Count, ct);
            }
            catch (GraphForbiddenException) when (hiddenGroups.Contains(group))
            {
                // Keep the group object so references from other groups remain valid. Do not
                // retain old memberships or commit partial pages when access is withdrawn.
                continue;
            }
            var seen = new HashSet<Guid>();
            foreach (var member in members)
            {
                var id = Guid.Parse(member.GetProperty("id").GetString()!);
                if (!seen.Add(id)) continue;
                // Service principals and contacts are outside the supported targeting model.
                var type = Text(member, "@odata.type");
                if (excludedDevices.Contains(id)) continue;
                if (!includeDevices && type == "#microsoft.graph.device") continue;
                if (!known.Contains(id))
                {
                    if (type is "#microsoft.graph.servicePrincipal" or "#microsoft.graph.orgContact") continue;
                    throw new InvalidOperationException("Directory membership changed during sync or is incomplete. Retry the sync.");
                }
                memberships.Add(new() { DirectorySourceId = sourceId, GroupGuid = group, MemberGuid = id, MemberDistinguishedName = id.ToString() });
            }
        }
        return new(tenant, objects) { Memberships = memberships };
    }

    public async Task<List<JsonElement>> ReadAsync(string url, string token, int limit, CancellationToken ct, bool firstPageOnly = false)
    {
        var result = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (url is not null)
        {
            var uri = new Uri(url);
            if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
                uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal) || !visited.Add(url) || visited.Count > 10000)
                throw new InvalidOperationException("Graph returned an invalid pagination link.");
            using var response = await SendAsync(uri, token, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new GraphForbiddenException();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Microsoft Graph returned HTTP {(int)response.StatusCode}. Check the application permissions and admin consent.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var document = JsonDocument.Parse(bytes);
            foreach (var row in document.RootElement.GetProperty("value").EnumerateArray())
            {
                if (result.Count >= limit) throw new InvalidOperationException("Directory sync exceeded its configured safety limit.");
                result.Add(row.Clone());
            }
            url = firstPageOnly ? null! : Text(document.RootElement, "@odata.nextLink")!;
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await http.SendAsync(request, ct);
            if (attempt >= 4 || response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return response;
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60)), ct);
        }
    }

    private static string CloudSid(Guid id)
    {
        var bytes = id.ToByteArray();
        return "S-1-12-1-" + string.Join("-", Enumerable.Range(0, 4).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4))));
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class GraphForbiddenException() : InvalidOperationException(
        "Microsoft Graph returned HTTP 403. Check the application permissions and admin consent.");
}

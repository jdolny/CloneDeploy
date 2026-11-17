using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace CloneDeploy.SiteServices;

// Service-port allowlist. Browser handoffs use ListenerPorts.PortalRoute on the portal listener.
// Management UI and SiteService management endpoints are deliberately excluded.
public static class SiteProxyRoutes
{
    private static readonly (string Method, Regex Path)[] Rules = Definitions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => { var parts = line.Split(' ', 2); return (parts[0], Compile(parts[1])); }).ToArray();

    public static bool Allows(PathString path, string method) => Rules.Any(rule =>
        string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase) && rule.Path.IsMatch(path.Value ?? ""));

    public enum Capability { None, Agents, Enrollment, Imaging, Portal, AgentAdmin, Storage }
    public static Capability RequiredCapability(PathString path)
    {
        if (path.StartsWithSegments("/api/agent/v1/admin") || path.StartsWithSegments("/agent/admin-sign-in") || path.StartsWithSegments("/signin-entra-agent-admin")) return Capability.AgentAdmin;
        if (path.StartsWithSegments("/api/agent/v1/enroll")) return Capability.Enrollment;
        if (path.StartsWithSegments("/api/agent/v1/portal-auth") || path.StartsWithSegments("/agent/portal-sign-in") || path.StartsWithSegments("/signin-entra-portal")) return Capability.Portal;
        if (path.StartsWithSegments("/api/agent")) return Capability.Agents;
        if (path.StartsWithSegments("/api/imaging") || path.StartsWithSegments("/api/boot")) return Capability.Imaging;
        if (path.StartsWithSegments("/api/site-download")) return Capability.Storage;
        return Capability.None;
    }

    private static Regex Compile(string template)
    {
        template = template.Replace("{architecture:regex(^(x64|arm64)$)}", "{architecture}");
        var parts = Regex.Split(template, @"(\{[^}]+})");
        var expression = string.Concat(parts.Select(part => part.StartsWith('{')
            ? part == "{architecture}" ? "(?:x64|arm64)" : "[^/]+" : Regex.Escape(part)));
        return new Regex("^" + expression + "/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private const string Definitions = """
        CONNECT /api/agent/v1/live
        CONNECT /api/agent/v1/remote/{id:guid}
        CONNECT /api/agent/v1/terminal/{id:guid}
        DELETE /api/agent/v1/admin/driver-packs/uploads/{id:guid}
        DELETE /api/agent/v1/live
        DELETE /api/agent/v1/policies/download-slots/{leaseId:guid}
        DELETE /api/agent/v1/prepared-capture/{id:guid}
        DELETE /api/agent/v1/tasks/download-slots/{leaseId:guid}
        DELETE /api/imaging/prepared-capture/uploads/{id:guid}
        DELETE /api/imaging/uploads/{id:guid}
        GET /api/agent/v1/admin/image-prep
        GET /api/agent/v1/admin/image-prep/images
        GET /api/agent/v1/admin/usb/builds/{id:guid}/iso
        GET /api/agent/v1/admin/usb/variants
        GET /api/agent/v1/branding
        GET /api/agent/v1/features
        GET /api/agent/v1/imaging-boot/{id:guid}
        GET /api/agent/v1/imaging-boot/{id:guid}/files/{name}
        GET /api/agent/v1/live
        GET /api/agent/v1/mac-updates/{hash}
        GET /api/agent/v1/policies
        GET /api/agent/v1/policies/runs/{runId:guid}/credentials/{credentialId:int}
        GET /api/agent/v1/policies/runs/{runId:guid}/files/{fileId:int}
        GET /api/agent/v1/policies/runs/{runId:guid}/managed-updates
        GET /api/agent/v1/policies/runs/{runId:guid}/windows-updates/{actionGuid:guid}
        GET /api/agent/v1/policies/user
        GET /api/agent/v1/portal/available
        GET /api/agent/v1/portal/icons/{policyId:int}
        GET /api/agent/v1/portal/identity
        GET /api/agent/v1/portal/policies
        GET /api/agent/v1/prepared-capture/{id:guid}
        GET /api/agent/v1/prepared-capture/{id:guid}/files/{name}
        GET /api/agent/v1/remote/{id:guid}
        GET /api/agent/v1/tasks/{runId:guid}/credentials/{credentialId:int}
        GET /api/agent/v1/tasks/{runId:guid}/files/{fileId:int}
        GET /api/agent/v1/tasks/{runId:guid}/managed-updates
        GET /api/agent/v1/tasks/{runId:guid}/windows-updates/{actionGuid:guid}
        GET /api/agent/v1/terminal/{id:guid}
        GET /api/agent/v1/updates/{hash}
        GET /api/boot
        GET /api/boot/files/{id:guid}/BCD
        GET /api/boot/files/{id:guid}/arm64/{name}
        GET /api/boot/files/{id:guid}/boot.sdi
        GET /api/boot/files/{id:guid}/boot.wim
        GET /api/boot/files/{id:guid}/bootmgfw.efi
        GET /api/boot/files/{id:guid}/bootmgr
        GET /api/boot/ipxe
        GET /api/boot/wimboot
        GET /api/boot/wimboot.arm64
        GET /api/imaging/capture-images
        GET /api/imaging/client/win-{architecture:regex(^(x64|arm64)$)}.json
        GET /api/imaging/client/win-{architecture:regex(^(x64|arm64)$)}.zip
        GET /api/imaging/deployment/catalog
        GET /api/imaging/deployment/drivers/{archiveId:guid}
        GET /api/imaging/deployment/execution
        GET /api/imaging/deployment/ffu
        GET /api/imaging/deployment/image
        GET /api/imaging/deployment/multicast
        GET /api/imaging/deployment/session
        GET /api/imaging/deployment/smb
        GET /api/imaging/prepared-capture/uploads/{id:guid}
        GET /api/imaging/prepared-capture/uploads/{id:guid}/ffu
        GET /api/imaging/session
        GET /api/imaging/uploads/{id:guid}/ffu
        HEAD /api/boot
        HEAD /api/boot/files/{id:guid}/BCD
        HEAD /api/boot/files/{id:guid}/arm64/{name}
        HEAD /api/boot/files/{id:guid}/boot.sdi
        HEAD /api/boot/files/{id:guid}/boot.wim
        HEAD /api/boot/files/{id:guid}/bootmgfw.efi
        HEAD /api/boot/files/{id:guid}/bootmgr
        HEAD /api/boot/ipxe
        HEAD /api/boot/wimboot
        HEAD /api/boot/wimboot.arm64
        POST /api/agent/v1/admin/driver-packs
        POST /api/agent/v1/admin/driver-packs/uploads
        POST /api/agent/v1/admin/driver-packs/uploads/{id:guid}/complete
        POST /api/agent/v1/admin/exchange
        POST /api/agent/v1/admin/image-prep/{id:int}/start
        POST /api/agent/v1/admin/session
        POST /api/agent/v1/admin/sign-in
        POST /api/agent/v1/admin/sign-out
        POST /api/agent/v1/check-in
        POST /api/agent/v1/enroll
        POST /api/agent/v1/help
        POST /api/agent/v1/imaging-boot/{id:guid}/progress
        POST /api/agent/v1/inventory
        POST /api/agent/v1/live
        POST /api/agent/v1/live/negotiate
        POST /api/agent/v1/policies/results
        POST /api/agent/v1/policies/runs
        POST /api/agent/v1/policies/runs/{runId:guid}/domain-join/{actionGuid:guid}
        POST /api/agent/v1/policies/runs/{runId:guid}/download-slot
        POST /api/agent/v1/policies/setup-readiness
        POST /api/agent/v1/portal-auth/exchange
        POST /api/agent/v1/portal-auth/help
        POST /api/agent/v1/portal-auth/policies
        POST /api/agent/v1/portal-auth/policy-start
        POST /api/agent/v1/portal-auth/self-service-policies
        POST /api/agent/v1/portal-auth/self-service-start
        POST /api/agent/v1/portal-auth/session
        POST /api/agent/v1/portal-auth/sign-out
        POST /api/agent/v1/portal-auth/start
        POST /api/agent/v1/portal/notifications/receipts
        POST /api/agent/v1/prepared-capture/{id:guid}/ready
        POST /api/agent/v1/tasks/claim
        POST /api/agent/v1/tasks/{runId:guid}/domain-join/{actionGuid:guid}
        POST /api/agent/v1/tasks/{runId:guid}/download-slot
        POST /api/agent/v1/tasks/{taskId:guid}/heartbeat
        POST /api/agent/v1/tasks/{taskId:guid}/result
        POST /api/agent/v1/tasks/{taskId:guid}/start
        POST /api/imaging/capture-images
        POST /api/imaging/deployment/automatic-matches
        POST /api/imaging/deployment/catalog
        POST /api/imaging/deployment/claim
        POST /api/imaging/secure/handshake
        POST /api/imaging/secure/request
        POST /api/imaging/deployment/driver-selection
        POST /api/imaging/deployment/finish
        POST /api/imaging/deployment/multicast/join
        POST /api/imaging/deployment/multicast/start
        POST /api/imaging/deployment/ou
        POST /api/imaging/deployment/prepare
        POST /api/imaging/deployment/progress
        POST /api/imaging/deployment/queue
        POST /api/imaging/deployment/release
        POST /api/imaging/deployment/renew
        POST /api/imaging/deployment/start
        POST /api/imaging/deployment/sysprep-credentials
        POST /api/imaging/device
        POST /api/imaging/device/logs
        POST /api/imaging/device/rename
        POST /api/imaging/images/{imageId:int}/uploads
        POST /api/imaging/login
        POST /api/imaging/prepared-capture/uploads/{id:guid}/complete
        POST /api/imaging/prepared-capture/uploads/{id:guid}/ffu
        POST /api/imaging/prepared-capture/uploads/{id:guid}/progress
        POST /api/imaging/uploads/{id:guid}/complete
        POST /api/imaging/uploads/{id:guid}/ffu
        POST /api/imaging/uploads/{id:guid}/progress
        PUT /api/agent/v1/admin/driver-packs/uploads/{id:guid}/chunks
        PUT /api/imaging/prepared-capture/uploads/{id:guid}/content
        PUT /api/imaging/uploads/{id:guid}/content
        """;
}

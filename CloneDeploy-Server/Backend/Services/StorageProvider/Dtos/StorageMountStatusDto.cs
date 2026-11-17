namespace CloneDeploy_Server.Backend.Services;

public sealed record StorageMountStatusDto(int ProviderId, string MountPath, string State, string Message,
    bool TargetAccessible, bool Mounted, bool Writable, DateTime CheckedAtUtc);

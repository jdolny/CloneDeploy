namespace CloneDeploy_Server.Backend.Services;

public enum DashboardAttentionSeverity { Information, Warning, Error }

public sealed record DashboardAttentionDto(string Key, string TitleKey, string DescriptionKey,
    string Href, DashboardAttentionSeverity Severity, int? Count = null, params object[] Args);

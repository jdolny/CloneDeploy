namespace CloneDeploy_Server.Backend.Services;

public sealed record BootActivitySnapshot(string Status, BootActivityEntry[] Entries,
    IReadOnlyList<CodePlex.JPMikkers.TFTP.SessionLogEntry> Transfers);

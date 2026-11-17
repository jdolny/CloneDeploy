
namespace CloneDeploy_Server.Backend.Services;

public sealed record DriverCatalogModelDto(string Name, string Source)
{
    public bool IsWinPe => Name.StartsWith("WinPE ", StringComparison.OrdinalIgnoreCase);
}


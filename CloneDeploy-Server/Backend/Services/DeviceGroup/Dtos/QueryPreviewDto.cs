using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record QueryPreviewDto(int Total, List<QueryDeviceDto> Devices);

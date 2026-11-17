using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceTaskOfferDto(Guid TaskId, Guid LeaseToken, DateTime LeaseExpiresAtUtc, string ActionJson, string ContentHash);
using CloneDeploy_Server.Localization;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages boot environment definitions and requests to build or cancel them.
/// </summary>
public sealed class WinPeAdministration(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    BuildStorage storage,
    DriverPreparationStorage drivers,
    StoredFileStorage sourceStorage,
    ILogger<WinPeAdministration> logger,
    WinPeArtifactStorage artifacts,
    ImagerBundle imagerBundle,
    ServerUrlSettingsStore urls,
    CertificateAuthorityService? certificates = null,
    NativePxePublication? nativePublication = null,
    NativePxeChanges? nativeChanges = null)
{
    public WindowsWorkQueue? Windows { get; set; }
    public async Task<ServiceResult<WinPeCatalogDto>> GetAsync()
    {
        if (await RequireAdmin() is null) return ServiceResult<WinPeCatalogDto>.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var definitions = await db.WinPeDefinitions.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var builds = await db.WinPeBuilds.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
        // Recover a server restart between the database commit and spool publication.
        foreach (var build in builds.Where(x => x.StoredAtUtc == null && storage.Status(x.Id).State == "Queued" && !File.Exists(storage.FilePath(x.Id, "request.json"))))
        {
            var request = JsonSerializer.Deserialize<BuildRequestDto>(build.RequestJson);
            if (request is not null) await storage.WriteAsync(storage.FilePath(build.Id, "request.json"), request);
        }

        var packs = await db.DriverProfiles.AsNoTracking().Include(x => x.Files).ThenInclude(x => x.File).Where(x => x.Enabled).OrderBy(x => x.Name).ToListAsync();
        return ServiceResult<WinPeCatalogDto>.Ok(new(definitions, builds.Select(x => new WinPeBuildRowDto(x, artifacts.Status(x))).ToList(), packs,
            storage.Read<BuilderHealthDto>(Path.Combine(storage.Root, "health.json"))));
    }

    public Task<ServiceResult<int>> SaveAsync(WinPeDefinition input, IEnumerable<int> driverIds) => SaveCoreAsync(input, driverIds, null);

    public Task<ServiceResult<int>> SaveSectionAsync(WinPeDefinition input, IEnumerable<int> driverIds, WinPeSettingsSection section) => SaveCoreAsync(input, driverIds, section);

    private async Task<ServiceResult<int>> SaveCoreAsync(WinPeDefinition input, IEnumerable<int> driverIds, WinPeSettingsSection? section)
    {
        var user = await RequireAdmin();
        if (user is null) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"WinPeDefinitions\" IN SHARE ROW EXCLUSIVE MODE");
        var entity = input.Id == 0 ? new WinPeDefinition { IsBase = input.IsBase } : await db.WinPeDefinitions.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.DefinitionNotFound));
        if (entity.IsBase != input.IsBase) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.TheDefinitionTypeCannotBeChanged));
        if (section is not null)
        {
            if (input.Id == 0 || !System.Enum.IsDefined(section.Value)) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.SaveTheNewEnvironmentBeforeEditingIndividualSections));
            var general = section == WinPeSettingsSection.General;
            var configuration = section == WinPeSettingsSection.Configuration;
            var matching = section == WinPeSettingsSection.ModelMatching;
            input = new WinPeDefinition
            {
                Id = entity.Id, IsBase = entity.IsBase,
                Name = general ? input.Name : entity.Name,
                Description = general ? input.Description : entity.Description,
                IncludePowerShell = configuration ? input.IncludePowerShell : entity.IncludePowerShell,
                UseBootEx = configuration ? input.UseBootEx : entity.UseBootEx,
                KeyboardLayout = configuration ? input.KeyboardLayout : entity.KeyboardLayout,
                NativePxe = configuration ? input.NativePxe : entity.NativePxe,
                IsDefault = matching ? input.IsDefault : entity.IsDefault,
                IsArm64Default = matching ? input.IsArm64Default : entity.IsArm64Default,
                ModelRulesJson = matching ? input.ModelRulesJson : entity.ModelRulesJson
            };
            if (section != WinPeSettingsSection.Drivers)
                driverIds = JsonSerializer.Deserialize<int[]>(entity.DriverPackIdsJson) ?? [];
        }
        var name = input.Name?.Trim() ?? "";
        if (!WinPeKeyboardLayouts.IsSupported(input.KeyboardLayout)) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ChooseASupportedWinPEKeyboardLayout));
        if (name.Length is < 1 or > 200 || input.Description?.Length > 2000) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.EnterANameOfUpTo200CharactersAnd));
        var selected = driverIds.Distinct().Order().ToArray();
        List<WinPeModelRuleDto> rules;
        try
        {
            rules = WinPeModelMatching.Read(input.ModelRulesJson);
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, exception.Message);
        }

        if (input.IsBase && rules.Count != 0) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ModelMatchingIsOnlyAvailableForBootEnvironments));
        if (input.IsBase && input.NativePxe) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.NativePXEIsOnlyAvailableForBootEnvironments));
        if (input.IsBase && (input.IsDefault || input.IsArm64Default)) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ABaseCannotBeTheDefaultBootEnvironment));
        if (await db.WinPeDefinitions.AnyAsync(x => x.Name == name && x.Id != input.Id)) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ThatNameIsAlreadyInUse));
        if (!entity.IsBase && (section is null || section == WinPeSettingsSection.Drivers) &&
            await db.DriverProfiles.CountAsync(x => selected.Contains(x.Id)) != selected.Length)
            return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ASelectedDriverPackDoesNotExist));

        if (input.NativePxe && !entity.NativePxe && entity.PublishedBuildId is { } publishedId)
        {
            if (nativePublication is null) return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.NativePXEPreparationIsUnavailable));
            var published = await db.WinPeBuilds.SingleAsync(x => x.Id == publishedId);
            try { await nativePublication.PrepareAsync(published); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { return ServiceResult<int>.Fail(ErrorKeys.WinPe.Validation, ex.Message); }
        }
        entity.NativePxe = input.NativePxe;
        entity.Name = name;
        entity.Description = input.Description?.Trim();
        entity.IncludePowerShell = true;
        entity.UseBootEx = input.UseBootEx;
        entity.KeyboardLayout = input.KeyboardLayout;
        entity.DriverPackIdsJson = JsonSerializer.Serialize(entity.IsBase ? [] : selected);
        entity.ModelRulesJson = JsonSerializer.Serialize(rules);
        if (input.IsDefault)
        {
            var previousDefaults = await db.WinPeDefinitions.Where(x => x.IsDefault && x.Id != entity.Id).ToListAsync();
            foreach (var previous in previousDefaults)
            {
                previous.IsDefault = false;
                previous.UpdatedAtUtc = DateTime.UtcNow;
                Audit(db, user, previous, new { Operation = "ClearDefault", ReplacedBy = name });
            }

            // Release the unique default slot before inserting/updating its replacement.
            await db.SaveChangesAsync();
        }

        entity.IsDefault = input.IsDefault;
        if (input.IsArm64Default)
        {
            foreach (var previous in await db.WinPeDefinitions.Where(x => x.IsArm64Default && x.Id != entity.Id).ToListAsync())
            {
                previous.IsArm64Default = false;
                previous.UpdatedAtUtc = DateTime.UtcNow;
                Audit(db, user, previous, new { Operation = "ClearArm64Default", ReplacedBy = name });
            }
            await db.SaveChangesAsync();
        }
        entity.IsArm64Default = input.IsArm64Default;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (entity.Id == 0) db.WinPeDefinitions.Add(entity);
        await db.SaveChangesAsync();
        Audit(db, user, entity, new { Operation = "Save", entity.IsBase, entity.IsDefault, entity.IsArm64Default, entity.NativePxe, Drivers = selected, ModelRules = rules });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        nativeChanges?.RulesChanged();
        return ServiceResult<int>.Ok(entity.Id);
    }

    public async Task<ServiceResult> DeleteAsync(int definitionId)
    {
        var user = await RequireAdmin();
        if (user is null) return ServiceResult.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Serialize with definition edits and build queuing, including dependent variants.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"WinPeDefinitions\" IN SHARE ROW EXCLUSIVE MODE");
        var definition = (await db.WinPeDefinitions.FromSqlInterpolated($"SELECT * FROM \"WinPeDefinitions\" WHERE \"Id\" = {definitionId} FOR UPDATE").ToListAsync()).SingleOrDefault();
        if (definition is null) return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.DefinitionNotFound));
        var builds = await db.WinPeBuilds.Where(x => x.DefinitionId == definitionId).ToListAsync();
        IDisposable claim;
        try
        {
            claim = artifacts.ClaimDeletion(builds);
        }
        catch (IOException)
        {
            return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.TheBuilderOrStorageWorkerIsStillUsingThis));
        }

        using (claim)
        {
            if (builds.Any(x => !artifacts.Status(x).IsComplete && artifacts.Status(x).State != "StorageFailed"))
                return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.FinishOrCancelPendingBuildsAndStorageTransfersBefore));
            var ids = builds.Select(x => x.Id).ToArray();
            if (await db.WinPeDefinitions.AnyAsync(x => x.Id != definitionId &&
                                                        (x.PublishedBuildId != null && ids.Contains(x.PublishedBuildId.Value))))
                return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ThisBaseIsUsedByABootEnvironmentSelect));

            definition.PublishedBuildId = null;
            await db.SaveChangesAsync();
            db.WinPeBuilds.RemoveRange(builds);
            await db.SaveChangesAsync();
            db.WinPeDefinitions.Remove(definition);
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Delete, ObjectType = nameof(WinPeDefinition),
                ObjectId = definition.Id, ObjectName = definition.Name,
                ObjectJson = JsonSerializer.Serialize(new { Operation = "Delete", Builds = ids })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        nativeChanges?.RulesChanged();
        foreach (var build in builds)
        {
            try
            {
                await artifacts.DeleteFilesAsync(build);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "WinPE definition deleted; artifact cleanup failed for build {BuildId}", build.Id);
            }
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<InventoriedModelDto>>> GetModelsAsync()
    {
        if (await RequireAdmin() is null) return ServiceResult<List<InventoriedModelDto>>.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var rows = await db.DeviceComputerSystems.AsNoTracking().Where(x => x.Device!.ArchivedAtUtc == null &&
                                                                            x.Manufacturer != null && x.Model != null && x.Manufacturer.Trim() != "" && x.Model.Trim() != "")
            .Select(x => new { x.DeviceId, Manufacturer = x.Manufacturer!.Trim(), Model = x.Model!.Trim() }).ToListAsync();
        return ServiceResult<List<InventoriedModelDto>>.Ok(rows.GroupBy(x => (x.Manufacturer.ToUpperInvariant(), x.Model.ToUpperInvariant()))
            .Select(g => new InventoriedModelDto(g.First().Manufacturer, g.First().Model, g.Select(x => x.DeviceId).Distinct().Count()))
            .OrderBy(x => x.Manufacturer).ThenBy(x => x.Model).ToList());
    }

    // Admin preview today; the future authenticated boot flow can reuse the matching contract.
    public async Task<ServiceResult<WinPeModelSelectionDto>> PreviewModelAsync(string? manufacturer, string? model)
    {
        if (await RequireAdmin() is null) return ServiceResult<WinPeModelSelectionDto>.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        WinPeDefinition definition;
        try
        {
            definition = WinPeModelMatching.Resolve(await db.WinPeDefinitions.AsNoTracking().Where(x => !x.IsBase && x.PublishedBuildId != null).ToListAsync(), manufacturer, model);
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult<WinPeModelSelectionDto>.Fail(ErrorKeys.WinPe.Validation, exception.Message);
        }

        var build = await db.WinPeBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == definition.PublishedBuildId && x.DefinitionId == definition.Id);
        if (build is null || artifacts.Status(build).State != "Ready") return ServiceResult<WinPeModelSelectionDto>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.TheMatchingBootEnvironmentSCurrentBuildIsNot));
        return ServiceResult<WinPeModelSelectionDto>.Ok(new(definition.Id, definition.Name, build.Id, build.Number));
    }

    public async Task<ServiceResult<Guid>> QueueAsync(int definitionId)
    {
        Guid? stagedId = null;
        var committed = false;
        try
        {
            var user = await RequireAdmin();
            if (user is null) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Forbidden);
            var worker = storage.Read<SetupHealthDto>(Path.Combine(storage.Root, "setup-health.json"));
            if (worker is not null && !WinPeToolchainPolicy.SupportsApplication(worker))
                return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.UpdateTheWindowsBuilderToThisApplicationReleaseBefore));
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await StorageProviderAdministration.LockAsync(db);
            var definition = (await db.WinPeDefinitions.FromSqlInterpolated($"SELECT * FROM \"WinPeDefinitions\" WHERE \"Id\" = {definitionId} FOR UPDATE").ToListAsync()).SingleOrDefault();
            if (definition is null) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.DefinitionNotFound));
            var providerId = await artifacts.SelectProviderAsync();
            var previous = await db.WinPeBuilds.Where(x => x.DefinitionId == definitionId).ToListAsync();
            if (previous.Any(x => !artifacts.Status(x).IsComplete)) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ThisDefinitionAlreadyHasABuildOrStorageTransfer));
            var build = new WinPeBuild { DefinitionId = definitionId, StorageProviderId = providerId, Number = previous.Select(x => x.Number).DefaultIfEmpty().Max() + 1, CreatedBy = user.Identity?.Name ?? "Administrator" };
            stagedId = build.Id;
            Directory.CreateDirectory(storage.Job(build.Id));
            BuildStorage.RejectLinks(storage.Job(build.Id));
            ImagerBuildDto? imager = null;
            if (!definition.IsBase)
            {
                try
                {
                    imager = imagerBundle.Bootstrap(await urls.RequireServiceUrlAsync());
                }
                catch (InvalidDataException exception)
                {
                    return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, exception.Message);
                }

                if (certificates is not null && await db.AppCertificates.AnyAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked && x.NotAfterUtc > DateTime.UtcNow))
                {
                    var ca = await certificates.GetPublicCertificateAsync(user);
                    if (!ca.Success) return ServiceResult<Guid>.Fail(ca.Error.Code, ca.Error.Args);
                    if (ca.Data is not null) imager = imager with { TrustedRootCertificate = Convert.ToBase64String(ca.Data) };
                }


            }

            var snapshots = new List<BuildDriverDto>();
            var ids = JsonSerializer.Deserialize<int[]>(definition.DriverPackIdsJson) ?? [];
            // Protect immutable prepared files while copying them into the durable job input directory.
            await sourceStorage.Gate.WaitAsync();
            try
            {
                foreach (var packId in ids)
                {
                    var pack = await db.DriverProfiles.AsNoTracking().Include(x => x.Files).ThenInclude(x => x.File).SingleOrDefaultAsync(x => x.Id == packId && x.Enabled);
                    if (pack is null) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ASelectedDriverPackIsMissingOrDisabled));
                    if (pack.Files.Count == 0) return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.DriverPackHasNoPackages) + pack.Name);
                    foreach (var file in pack.Files)
                    {
                        if (file.PreparationStatus != DriverPreparationStatus.Ready || file.PreparedArchiveId is null || file.PreparedSha256 is null || file.PreparedSourceHash != file.File.Sha256Hash)
                            return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.PrepareAllCurrentPackagesBeforeBuilding) + pack.Name);
                        if (file.CatalogArchitecture is { } arch && (arch.Equals("x86", StringComparison.OrdinalIgnoreCase) || arch.Contains("arm", StringComparison.OrdinalIgnoreCase)))
                            return ServiceResult<Guid>.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.OnlyX64DriverPackagesCanBeUsed) + file.File.Name);
                        if (file.PreparedStorageProviderId is { } driverProvider) await artifacts.EnsureProviderReadyAsync(driverProvider);
                        await Snapshot(drivers.ArchivePath(file.PreparedArchiveId.Value, file.PreparedStorageProviderId), storage.FilePath(build.Id, "driver-" + file.StoredFileId + ".zip"), file.PreparedSha256);
                        snapshots.Add(new(pack.Id, pack.Name, file.StoredFileId, file.File.Name, file.PreparedSha256));
                    }
                }
            }
            finally
            {
                sourceStorage.Gate.Release();
            }

            var request = new BuildRequestDto(build.Id, definition.IsBase, null, null, true, definition.UseBootEx, snapshots)
            {
                RemoteExecution = Windows is not null && await Windows.HostAsync(CloneDeploy.SiteServices.WindowsWorkerRoles.WinPe, default) is not null,
                RequiredToolchainVersion = ToolchainSpecDto.MicrosoftDefault.Version,
                KeyboardLayout = definition.KeyboardLayout,
                Imager = imager,
                BuildArm64 = true,
                PrepareMediaAutomatically = !definition.IsBase
            };
            build.RequestJson = JsonSerializer.Serialize(request);
            db.WinPeBuilds.Add(build);
            Audit(db, user, definition, new { Operation = "Build", build.Id, build.Number });
            await storage.SetStatus(build.Id, "Queued", "Waiting for the Windows builder.");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            committed = true;
            // Request publication is the queue commit. A failed publication is reported as a failed build.
            await storage.WriteAsync(storage.FilePath(build.Id, "request.json"), request);
            return ServiceResult<Guid>.Ok(build.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (committed && stagedId is { } id)
            {
                try
                {
                    await storage.SetStatus(id, "Failed", "Could not publish the build request. Create a new build to retry.");
                }
                catch (Exception statusException) when (statusException is not OperationCanceledException)
                {
                    logger.LogError(statusException, "Could not record failed WinPE build {BuildId}", id);
                }
            }

            return Failure<Guid>(ex);
        }
    }

    public async Task<ServiceResult> CancelAsync(Guid id)
    {
        var user = await RequireAdmin();
        if (user is null) return ServiceResult.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var build = await db.WinPeBuilds.Include(x => x.Definition).SingleOrDefaultAsync(x => x.Id == id);
        if (build is null) return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.BuildNotFound));
        if (storage.Status(id).IsComplete) return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.ThisBuildHasAlreadyFinished));
        await File.WriteAllTextAsync(storage.FilePath(id, "cancel"), "Cancel requested.");
        // Cancel unclaimed jobs immediately, including while the builder is offline.
        // A claimed job is cancelled by the worker's monitor after its current operation stops.
        FileStream? claim = null;
        try
        {
            claim = new FileStream(storage.FilePath(id, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
        }

        if (claim is not null)
        {
            using (claim)
                if (storage.Status(id).State == "Queued")
                    await storage.SetStatus(id, "Cancelled", "Cancelled before execution.");
        }

        Audit(db, user, build.Definition, new { Operation = "Cancel", BuildId = id });
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RetryStorageAsync(Guid id)
    {
        var user = await RequireAdmin();
        if (user is null) return ServiceResult.Fail(ErrorKeys.WinPe.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var build = await db.WinPeBuilds.Include(x => x.Definition).SingleOrDefaultAsync(x => x.Id == id);
        if (build is null) return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.BuildNotFound));
        if (build.StoredAtUtc is not null ? build.StorageError is null : storage.Status(id).State != "Ready")
            return ServiceResult.Fail(ErrorKeys.WinPe.Validation, UiText.Get(LocKeys.Ui.WinPeAdministration.OnlyCompletedBuildsWithPendingStorageOrFailedBoot));
        build.StorageError = null;
        Audit(db, user, build.Definition, new { Operation = "RetryStorage", BuildId = id });
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private static async Task Snapshot(string source, string destination, string expectedHash)
    {
        // Provider-backed sources intentionally pass through the app's managed mount.
        BuildStorage.RejectLinks(destination);
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(destination, FileMode.CreateNew))
            await input.CopyToAsync(output);
        if (!string.Equals(await BuildStorage.HashAsync(destination), expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An input changed while preparing the build. Refresh and retry.");
    }

    private async Task<ClaimsPrincipal?> RequireAdmin()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _)) return null;
        return user;
    }

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, WinPeDefinition definition, object detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = AuditType.Update, ObjectType = nameof(WinPeDefinition), ObjectId = definition.Id, ObjectName = definition.Name, ObjectJson = JsonSerializer.Serialize(detail)
        });

    private ServiceResult<T> Failure<T>(Exception exception)
    {
        logger.LogError(exception, "WinPE operation failed");
        return ServiceResult<T>.Fail(ErrorKeys.WinPe.Failed);
    }
}

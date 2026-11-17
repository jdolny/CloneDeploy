using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class ImagePrepProfileService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<ImagePrepProfile>>> GetProfilesAsync()
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImagePrepProfileView) is null)
            return ServiceResult<List<ImagePrepProfile>>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<ImagePrepProfile>>.Ok(await db.ImagePrepProfiles.AsNoTracking()
            .OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<ManagedCredentialDto>>> GetCredentialsAsync()
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImagePrepProfileView) is null)
            return ServiceResult<List<ManagedCredentialDto>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<ManagedCredentialDto>>.Ok(await db.ManagedCredentials.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ManagedCredentialDto { Id = x.Id, Name = x.Name, Username = x.Username }).ToListAsync());
    }

    public async Task<ServiceResult> SaveAsync(ImagePrepProfile input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImagePrepProfileEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        if (input.ResetAgent && input.UninstallAgent)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.ChooseEitherResetOrUninstallForTheAgentNot));
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.EnterANameBetween1And200PrintableCharacters));
        if (input.Description?.Length > 2000)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.DescriptionIsLimitedTo2000Characters));
        if (input.SetupComplete?.Length > ImagePrepAnswerFile.MaximumContentLength
            || input.PreSysprepScript?.Length > ImagePrepAnswerFile.MaximumContentLength)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.ScriptsAreLimitedTo128KCharactersEach));
        await using var db = await factory.CreateContextAsync();
        ImagePrepGeneratorOptions? options;
        try { options = input.GeneratorOptions; }
        catch (System.Text.Json.JsonException) { return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.InvalidGeneratorSettings)); }
        if (input.GeneratorJson?.Length > ImagePrepAnswerFile.MaximumContentLength)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.GeneratorSettingsAreTooLarge));
        if (input.GenerateAnswerFile && options is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagePrepProfileService.ConfigureTheAnswerFileGeneratorFirst));
        if (options is not null)
        {
            var usernames = await db.ManagedCredentials.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Username);
            var validation = ImagePrepAnswerGenerator.Validate(options, usernames);
            if (validation is not null) return ServiceResult.Fail("Image.Validation", validation);
            if (input.GenerateAnswerFile) input.AnswerFile = ImagePrepAnswerGenerator.Generate(options, usernames);
        }
        var error = ImagePrepAnswerFile.Validate(input.AnswerFile);
        if (error is not null)
            return ServiceResult.Fail("Image.Validation", error);

        if (await db.ImagePrepProfiles.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ThisNameIsAlreadyInUse));

        var entity = input.Id == 0 ? new ImagePrepProfile { Name = name }
            : await db.ImagePrepProfiles.FindAsync(input.Id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ManageImagePrepProfilePage.ImagePrepProfileNotFound));

        await using var transaction = await db.Database.BeginTransactionAsync();
        if (input.Id == 0)
            db.ImagePrepProfiles.Add(entity);

        entity.Name = name;
        entity.Description = input.Description?.Trim();
        entity.Enabled = input.Enabled;
        entity.AnswerFile = input.AnswerFile;
        entity.GenerateAnswerFile = input.GenerateAnswerFile;
        entity.GeneratorJson = input.GeneratorJson;
        entity.SetupComplete = input.SetupComplete;
        entity.PreSysprepScript = input.PreSysprepScript;
        entity.DisableHibernate = input.DisableHibernate;
        entity.DisableBitLocker = input.DisableBitLocker;
        entity.ResetAgent = input.ResetAgent;
        entity.UninstallAgent = input.UninstallAgent;
        entity.RunSysprep = input.RunSysprep;
        entity.RebootAndCapture = input.RebootAndCapture;

        await db.SaveChangesAsync();
        db.AuditLogs.Add(Audit(user, entity, input.Id == 0 ? AuditType.Create : AuditType.Update));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        input.Id = entity.Id;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImagePrepProfileEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var entity = await db.ImagePrepProfiles.FindAsync(id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ManageImagePrepProfilePage.ImagePrepProfileNotFound));

        db.ImagePrepProfiles.Remove(entity);
        db.AuditLogs.Add(Audit(user, entity, AuditType.Delete));
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private static AuditLog Audit(ClaimsPrincipal user, ImagePrepProfile profile, AuditType type) => new()
    {
        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
        UserName = user.Identity?.Name ?? "Administrator",
        AuditType = type,
        ObjectType = nameof(ImagePrepProfile),
        ObjectId = profile.Id,
        ObjectName = profile.Name,
        ObjectJson = "{}"
    };
}

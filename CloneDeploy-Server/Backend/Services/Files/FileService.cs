using System.Runtime.CompilerServices;
using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services.Files;

/// <summary>
/// Lets users upload files and checks names and external download addresses.
/// </summary>
/// <remarks>
/// Browser upload operations. Storage and attachment helpers are used directly by internal services.
/// </remarks>
public sealed class FileService(ChunkedUploadStore uploads, ILogger<FileService> logger)
{
    public const int ChunkSize = ChunkedUploadStore.ChunkSize;

    public Task<ServiceResult<UploadSessionDto>> StartAsync(ClaimsPrincipal user, UploadRequestDto request, CancellationToken ct = default) =>
        ExecuteAsync(user, owner => uploads.StartAsync(owner, request, ct));

    public Task<ServiceResult> AppendAsync(ClaimsPrincipal user, Guid id, long offset, Stream body, long? length, CancellationToken ct = default) =>
        ExecuteAsync(user, owner => uploads.AppendAsync(owner, id, offset, body, length, ct));

    public Task<ServiceResult<UploadResultDto>> CompleteAsync(ClaimsPrincipal user, Guid id, CancellationToken ct = default) =>
        ExecuteAsync(user, owner => uploads.CompleteAsync(owner, id, ct));

    public Task<ServiceResult> CancelAsync(ClaimsPrincipal user, Guid id, CancellationToken ct = default) =>
        ExecuteAsync(user, owner => uploads.CancelAsync(owner, id, ct));

    public static string? SuggestExternalName(string? url) => StoredFileStorage.SuggestExternalName(url);

    public static ServiceResult ValidateExternalName(string? name) => Validate(() => StoredFileStorage.ValidateExternalName(name));

    public static ServiceResult ValidateUrl(string? url) => Validate(() => StoredFileStorage.ValidateUrl(url));

    private static ServiceResult Validate(Func<string> validate)
    {
        try { validate(); return ServiceResult.Ok(); }
        catch (InvalidOperationException exception)
        {
            return ServiceResult.Fail(ErrorKeys.File.Validation, exception.Message);
        }
    }

    private async Task<ServiceResult> ExecuteAsync(ClaimsPrincipal user, Func<string, Task> operation,
        [CallerMemberName] string operationName = "") =>
        await ExecuteAsync(user, async owner => { await operation(owner); return true; }, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(ClaimsPrincipal user, Func<string, Task<T>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(async () =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(owner))
                return ServiceResult<T>.Fail(ErrorKeys.File.Unauthorized);
            return ServiceResult<T>.Ok(await operation(owner));
        }, logger, ErrorKeys.File.Failed, MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception is UploadException upload
        ? upload.StatusCode switch
        {
            400 => new(ErrorKeys.File.Validation, [upload.Message]),
            404 => new(ErrorKeys.File.NotFound, [upload.Message]),
            409 => new(ErrorKeys.File.Conflict, [upload.Message]),
            429 => new(ErrorKeys.File.Busy, [upload.Message]),
            _ => null
        }
        : null;
}

using LockService.Security;
using Shared;

namespace LockService;

public static class ApiEndpoints
{
    public static void MapFocusLockApi(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        var protectedApi = app.MapGroup("/api").WithMetadata(new ProtectedEndpoint());
        protectedApi.MapGet("/status", async (LockManager manager, CancellationToken ct) => Results.Ok(await manager.GetStatusAsync(ct)))
            .WithMetadata(new EmptyBodyEndpoint());
        protectedApi.MapPost("/lock", (LockRequest request, LockManager manager, ILogger<LockManager> logger, CancellationToken ct) =>
            ExecuteAsync(() => manager.LockAsync(request.DurationMinutes, ct), logger));
        protectedApi.MapPost("/unlock", (LockManager manager, ILogger<LockManager> logger, CancellationToken ct) => ExecuteAsync(() => manager.UnlockAsync(ct), logger))
            .WithMetadata(new EmptyBodyEndpoint());
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<LockStatus>> command, ILogger logger)
    {
        try { return Results.Ok(await command()); }
        catch (ArgumentOutOfRangeException) { return Results.BadRequest(new ApiResult(false, "Duration must be between 1 and 720 minutes.")); }
        catch (EnforcementException exception)
        {
            logger.LogError(exception, "Account enforcement request refused or incomplete");
            return Results.Json(new ApiResult(false, exception.Message), statusCode: 409);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Unable to persist lock state");
            return Results.Json(new ApiResult(false, "Unable to save state. Check service logs and state-directory permissions."), statusCode: 503);
        }
    }
}

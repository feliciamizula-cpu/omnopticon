namespace Argus.AgentService.ProviderUsage;

using Argus.Contracts.ProviderUsage;

internal static class ProviderUsageEndpoints
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/provider-usage", ListProviderUsage);
        app.MapPost("/provider-usage/refresh", RefreshAllProviderUsage);
        app.MapPost("/provider-usage/{accountId:guid}/refresh", RefreshProviderUsage);
        app.MapGet("/provider-usage/{accountId:guid}/history", GetProviderUsageHistory);
        app.MapPost("/provider-usage/{accountId:guid}/manual-snapshot", AddManualSnapshot);
        app.MapPost("/provider-usage/{accountId:guid}/pause-agents", PauseAgentsForProvider);
        app.MapPost("/provider-usage/accounts", CreateProviderAccount);
        app.MapPut("/provider-usage/accounts/{accountId:guid}", UpdateProviderAccount);
    }

    private static async Task<IResult> ListProviderUsage(ProviderUsageService service, CancellationToken ct)
    {
        var summary = await service.GetSummaryAsync(ct);
        return Results.Ok(summary);
    }

    private static async Task<IResult> RefreshAllProviderUsage(ProviderUsageService service, CancellationToken ct)
    {
        var snapshots = await service.RefreshAllAsync(ct);
        return Results.Ok(new { items = snapshots, count = snapshots.Count });
    }

    private static async Task<IResult> RefreshProviderUsage(Guid accountId, ProviderUsageService service, CancellationToken ct)
    {
        var snapshots = await service.RefreshAsync(accountId, ct);
        return snapshots is not null
            ? Results.Ok(new { items = snapshots, count = snapshots.Count })
            : Results.NotFound();
    }

    private static async Task<IResult> GetProviderUsageHistory(
        Guid accountId, int take, ProviderUsageService service, CancellationToken ct)
    {
        if (take <= 0) take = 100;
        var history = await service.GetHistoryAsync(accountId, take, ct);
        return Results.Ok(new { items = history, count = history.Count });
    }

    private static async Task<IResult> AddManualSnapshot(
        Guid accountId, ManualUsageSnapshotRequest request, ProviderUsageService service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.WindowKind) || string.IsNullOrWhiteSpace(request.Unit))
            return Results.BadRequest(new { message = "WindowKind and Unit are required." });

        var snapshot = await service.AddManualSnapshotAsync(accountId, request, ct);
        return snapshot is not null ? Results.Ok(snapshot) : Results.NotFound();
    }

    private static async Task<IResult> PauseAgentsForProvider(
        Guid accountId, ProviderUsageService service, CancellationToken ct)
    {
        var paused = await service.PauseAgentsForProviderAsync(accountId, ct);
        return Results.Ok(new { paused });
    }

    private static async Task<IResult> CreateProviderAccount(
        CreateProviderAccountRequest request, ProviderUsageService service, CancellationToken ct)
    {
        var account = await service.CreateAccountAsync(request, ct);
        return Results.Created($"/provider-usage/accounts/{account.AccountId}", account);
    }

    private static async Task<IResult> UpdateProviderAccount(
        Guid accountId, UpdateProviderAccountRequest request, ProviderUsageService service, CancellationToken ct)
    {
        var account = await service.UpdateAccountAsync(accountId, request, ct);
        return account is not null ? Results.Ok(account) : Results.NotFound();
    }
}

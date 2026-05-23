using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Argus.RealtimeService;

internal sealed class WebhookService : BackgroundService
{
    private readonly RealtimeStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookService> _logger;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public WebhookService(
        RealtimeStore store,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("webhook");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessEventStream(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook service error, restarting in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessEventStream(CancellationToken cancellationToken)
    {
        var subscription = _store.Subscribe();

        try
        {
            await foreach (var envelope in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await ProcessEvent(envelope, cancellationToken);
            }
        }
        finally
        {
            _store.Unsubscribe(subscription.SubscriptionId);
        }
    }

    private async Task ProcessEvent(IntegrationEventEnvelope<JsonNode> envelope, CancellationToken cancellationToken)
    {
        List<WebhookConfig> activeConfigs;

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();
            activeConfigs = await dbContext.WebhookConfigs
                .Where(c => c.IsActive)
                .ToListAsync(cancellationToken);
        }

        if (activeConfigs.Count == 0)
            return;

        var matchingConfigs = activeConfigs
            .Where(config => MatchesFilter(config, envelope))
            .ToList();

        if (matchingConfigs.Count == 0)
            return;

        await Parallel.ForEachAsync(matchingConfigs, cancellationToken, async (config, ct) =>
        {
            await DeliverWithRetry(config, envelope, ct);
        });
    }

    private static bool MatchesFilter(WebhookConfig config, IntegrationEventEnvelope<JsonNode> envelope)
    {
        if (!string.IsNullOrWhiteSpace(config.EventTypes))
        {
            var allowedTypes = JsonSerializer.Deserialize<string[]>(config.EventTypes)
                ?? [];
            if (allowedTypes.Length > 0 && !allowedTypes.Contains(envelope.EventType, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (config.ProgramId.HasValue)
        {
            if (envelope.Payload is not JsonObject payload)
                return false;

            if (!payload.TryGetPropertyValue("programId", out var programIdNode) || programIdNode is null)
                return false;

            if (!Guid.TryParse(programIdNode.ToString(), out var eventProgramId))
                return false;

            if (eventProgramId != config.ProgramId.Value)
                return false;
        }

        if (config.MinInterestingScore.HasValue)
        {
            if (envelope.Payload is not JsonObject payload)
                return false;

            if (!payload.TryGetPropertyValue("interestingScore", out var scoreNode) || scoreNode is null)
                return false;

            if (!int.TryParse(scoreNode.ToString(), out var score))
                return false;

            if (score < config.MinInterestingScore.Value)
                return false;
        }

        return true;
    }

    private async Task DeliverWithRetry(
        WebhookConfig config,
        IntegrationEventEnvelope<JsonNode> envelope,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, config.RetryCount);
        var payload = new
        {
            eventId = envelope.EventId,
            eventType = envelope.EventType,
            occurredAt = envelope.OccurredAt,
            correlationId = envelope.CorrelationId,
            causationId = envelope.CausationId,
            sourceService = envelope.SourceService,
            payload = envelope.Payload
        };

        var jsonContent = JsonSerializer.Serialize(payload, _jsonOptions);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var success = false;
            int? statusCode = null;
            string? responseBody = null;
            string? errorMessage = null;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

                var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
                {
                    Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(config.SecretHeader) && !string.IsNullOrWhiteSpace(config.SecretValue))
                {
                    request.Headers.TryAddWithoutValidation(config.SecretHeader, config.SecretValue);
                }

                using var response = await _httpClient.SendAsync(request, cts.Token);
                statusCode = (int)response.StatusCode;
                responseBody = await response.Content.ReadAsStringAsync(cts.Token);
                success = response.IsSuccessStatusCode;

                if (success)
                {
                    _logger.LogDebug(
                        "Webhook {WebhookName} ({WebhookId}) delivered event {EventId} (attempt {Attempt}) with status {StatusCode}",
                        config.Name, config.Id, envelope.EventId, attempt, statusCode);
                }
                else
                {
                    _logger.LogWarning(
                        "Webhook {WebhookName} ({WebhookId}) returned {StatusCode} for event {EventId} (attempt {Attempt})",
                        config.Name, config.Id, statusCode, envelope.EventId, attempt);
                }
            }
            catch (TaskCanceledException)
            {
                errorMessage = "Request timed out";
                _logger.LogWarning(
                    "Webhook {WebhookName} ({WebhookId}) timed out for event {EventId} (attempt {Attempt})",
                    config.Name, config.Id, envelope.EventId, attempt);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _logger.LogWarning(ex,
                    "Webhook {WebhookName} ({WebhookId}) failed for event {EventId} (attempt {Attempt})",
                    config.Name, config.Id, envelope.EventId, attempt);
            }

            await LogDeliveryAttempt(config, envelope, attempt, success, statusCode, responseBody, jsonContent, errorMessage, cancellationToken);

            if (success)
                return;

            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogDebug(
                    "Retrying webhook {WebhookName} ({WebhookId}) for event {EventId} in {Delay}s (attempt {Attempt}/{MaxAttempts})",
                    config.Name, config.Id, envelope.EventId, delay.TotalSeconds, attempt + 1, maxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task LogDeliveryAttempt(
        WebhookConfig config,
        IntegrationEventEnvelope<JsonNode> envelope,
        int attempt,
        bool success,
        int? statusCode,
        string? responseBody,
        string requestBody,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();

            dbContext.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
            {
                Id = Guid.NewGuid(),
                WebhookConfigId = config.Id,
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                RequestUrl = config.Url,
                RequestBody = requestBody,
                ResponseStatusCode = statusCode,
                ResponseBody = responseBody,
                AttemptNumber = attempt,
                Success = success,
                ErrorMessage = errorMessage,
                AttemptedAt = DateTimeOffset.UtcNow
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log webhook delivery attempt for config {WebhookId}", config.Id);
        }
    }
}

internal sealed record CreateWebhookRequest(
    string Name,
    string Url,
    string[]? EventTypes,
    Guid? ProgramId,
    int? MinInterestingScore,
    bool? IsActive,
    string? SecretHeader,
    string? SecretValue,
    int? RetryCount,
    int? TimeoutSeconds);

internal sealed record UpdateWebhookRequest(
    string? Name,
    string? Url,
    string[]? EventTypes,
    Guid? ProgramId,
    int? MinInterestingScore,
    bool? IsActive,
    string? SecretHeader,
    string? SecretValue,
    int? RetryCount,
    int? TimeoutSeconds);

internal sealed record WebhookConfigDto(
    Guid Id,
    string Name,
    string Url,
    string[]? EventTypes,
    Guid? ProgramId,
    int? MinInterestingScore,
    bool IsActive,
    string? SecretHeader,
    string? SecretValue,
    int RetryCount,
    int TimeoutSeconds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record WebhookDeliveryLogDto(
    Guid Id,
    Guid WebhookConfigId,
    Guid EventId,
    string EventType,
    string RequestUrl,
    int? ResponseStatusCode,
    int AttemptNumber,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset AttemptedAt);

internal static class WebhookMapping
{
    public static WebhookConfigDto ToDto(this WebhookConfig config) => new(
        config.Id,
        config.Name,
        config.Url,
        string.IsNullOrWhiteSpace(config.EventTypes) ? null : JsonSerializer.Deserialize<string[]>(config.EventTypes),
        config.ProgramId,
        config.MinInterestingScore,
        config.IsActive,
        config.SecretHeader,
        config.SecretValue,
        config.RetryCount,
        config.TimeoutSeconds,
        config.CreatedAt,
        config.UpdatedAt);

    public static WebhookConfig ToEntity(this CreateWebhookRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new WebhookConfig
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Url = request.Url.Trim(),
            EventTypes = request.EventTypes is { Length: > 0 } ? JsonSerializer.Serialize(request.EventTypes) : null,
            ProgramId = request.ProgramId,
            MinInterestingScore = request.MinInterestingScore,
            IsActive = request.IsActive ?? true,
            SecretHeader = string.IsNullOrWhiteSpace(request.SecretHeader) ? null : request.SecretHeader.Trim(),
            SecretValue = string.IsNullOrWhiteSpace(request.SecretValue) ? null : request.SecretValue.Trim(),
            RetryCount = request.RetryCount is > 0 ? request.RetryCount.Value : 3,
            TimeoutSeconds = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : 30,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static void ApplyUpdate(this WebhookConfig config, UpdateWebhookRequest request)
    {
        if (request.Name is not null) config.Name = request.Name.Trim();
        if (request.Url is not null) config.Url = request.Url.Trim();
        if (request.EventTypes is not null) config.EventTypes = request.EventTypes.Length > 0 ? JsonSerializer.Serialize(request.EventTypes) : null;
        if (request.ProgramId is not null) config.ProgramId = request.ProgramId;
        if (request.MinInterestingScore is not null) config.MinInterestingScore = request.MinInterestingScore;
        if (request.IsActive is not null) config.IsActive = request.IsActive.Value;
        if (request.SecretHeader is not null) config.SecretHeader = string.IsNullOrWhiteSpace(request.SecretHeader) ? null : request.SecretHeader.Trim();
        if (request.SecretValue is not null) config.SecretValue = string.IsNullOrWhiteSpace(request.SecretValue) ? null : request.SecretValue.Trim();
        if (request.RetryCount is not null) config.RetryCount = Math.Max(1, request.RetryCount.Value);
        if (request.TimeoutSeconds is not null) config.TimeoutSeconds = Math.Max(1, request.TimeoutSeconds.Value);
        config.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static WebhookDeliveryLogDto ToDto(this WebhookDeliveryLog log) => new(
        log.Id,
        log.WebhookConfigId,
        log.EventId,
        log.EventType,
        log.RequestUrl,
        log.ResponseStatusCode,
        log.AttemptNumber,
        log.Success,
        log.ErrorMessage,
        log.AttemptedAt);
}

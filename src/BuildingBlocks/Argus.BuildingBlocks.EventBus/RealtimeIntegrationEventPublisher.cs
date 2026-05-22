using Argus.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace Argus.BuildingBlocks.EventBus;

public sealed class RealtimeIntegrationEventPublisher(
    IHttpClientFactory httpClientFactory,
    IOptions<ArgusEventBusOptions> options,
    ILogger<RealtimeIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    private readonly ArgusEventBusOptions _options = options.Value;

    public async Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = _options.RealtimeServiceBaseAddress;

        var request = new EventIngestRequest(
            envelope.EventType,
            envelope.SourceService,
            envelope.CorrelationId,
            envelope.CausationId,
            JsonSerializer.Serialize(envelope.Payload));

        try
        {
            using var response = await client.PostAsJsonAsync("/events", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Realtime event publish failed with {StatusCode} for {EventType} {EventId}",
                    response.StatusCode,
                    envelope.EventType,
                    envelope.EventId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "Realtime event publish failed for {EventType} {EventId}",
                envelope.EventType,
                envelope.EventId);
        }
    }

    private sealed record EventIngestRequest(
        string EventType,
        string? SourceService,
        Guid? CorrelationId,
        Guid? CausationId,
        string? PayloadJson);
}

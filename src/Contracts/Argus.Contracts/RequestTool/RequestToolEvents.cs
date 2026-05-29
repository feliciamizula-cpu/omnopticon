namespace Argus.Contracts.RequestTool;

public sealed record RequestToolExchangeCreatedEvent(
    Guid ExchangeId,
    Guid SessionId,
    Guid AssetId,
    Guid ProgramId,
    RequestToolExchangeOrigin Origin,
    RequestToolExchangeOutcome Outcome,
    string RequestMethod,
    string RequestUrl,
    int? ResponseStatusCode,
    int? DurationMs,
    DateTimeOffset CreatedAt);

public sealed record RequestToolExchangeUpdatedEvent(
    Guid ExchangeId,
    Guid SessionId,
    Guid AssetId,
    string ChangeType,
    DateTimeOffset UpdatedAt);
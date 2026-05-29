using Argus.Contracts.RequestTool;
using Argus.RequestToolService.Data;

namespace Argus.RequestToolService.Services;

public interface IAuditService
{
    Task AuditAsync(InsertAuditCommand command, CancellationToken ct);
}

public sealed class AuditService : IAuditService
{
    private readonly IRequestToolRepository _repository;
    private readonly IRedactionService _redactionService;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IRequestToolRepository repository,
        IRedactionService redactionService,
        ILogger<AuditService> logger)
    {
        _repository = repository;
        _redactionService = redactionService;
        _logger = logger;
    }

    public async Task AuditAsync(InsertAuditCommand command, CancellationToken ct)
    {
        var redactedCommand = new InsertAuditCommand(
            command.ExchangeId,
            command.SessionId,
            command.AssetId,
            command.ProgramId,
            command.Actor,
            command.Action,
            _redactionService.RedactText(command.TargetUrl),
            command.RequestMethod,
            command.ScopeStatus,
            command.RequestSha256,
            command.ResponseSha256,
            command.Outcome,
            command.Metadata);

        await _repository.InsertAuditAsync(redactedCommand, ct);
    }
}
using System.Text;
using Argus.Contracts.RequestTool;
using Argus.RequestToolService.Data;

namespace Argus.RequestToolService.Services;

public interface IRawHttpRenderer
{
    Task<string> RenderRequestAsync(HttpExchangeRecord exchange, CancellationToken ct);
    Task<string> RenderResponseAsync(HttpExchangeRecord exchange, CancellationToken ct);
}

public sealed class RawHttpRenderer : IRawHttpRenderer
{
    private readonly IBodyStorageService _bodyStorage;

    public RawHttpRenderer(IBodyStorageService bodyStorage)
    {
        _bodyStorage = bodyStorage;
    }

    public async Task<string> RenderRequestAsync(HttpExchangeRecord exchange, CancellationToken ct)
    {
        var sb = new StringBuilder();

        var path = exchange.RequestPath;
        if (!string.IsNullOrEmpty(exchange.RequestQuery))
        {
            path = path + "?" + exchange.RequestQuery;
        }

        sb.AppendLine($"{exchange.RequestMethod} {path} {exchange.RequestHttpVersion ?? "HTTP/1.1"}");
        sb.AppendLine($"Host: {exchange.RequestHost}");

        var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(exchange.RequestHeaders)
            ?? new Dictionary<string, string[]>();

        foreach (var header in headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var value in header.Value)
            {
                sb.AppendLine($"{header.Key}: {value}");
            }
        }

        if (!string.IsNullOrEmpty(exchange.RequestBodyInline))
        {
            sb.AppendLine();
            sb.Append(exchange.RequestBodyInline);
        }

        return sb.ToString();
    }

    public async Task<string> RenderResponseAsync(HttpExchangeRecord exchange, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{exchange.ResponseHttpVersion ?? "HTTP/1.1"} {exchange.ResponseStatusCode} {exchange.ResponseReasonPhrase ?? ""}");

        if (!string.IsNullOrEmpty(exchange.ResponseHeaders))
        {
            var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(exchange.ResponseHeaders)
                ?? new Dictionary<string, string[]>();

            foreach (var header in headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var value in header.Value)
                {
                    sb.AppendLine($"{header.Key}: {value}");
                }
            }
        }

        if (!string.IsNullOrEmpty(exchange.ResponseBodyInline))
        {
            sb.AppendLine();
            sb.Append(exchange.ResponseBodyInline);
        }

        return sb.ToString();
    }
}

public interface IRequestToolDiffService
{
    Task<CompareHttpExchangeResponse> CompareAsync(CompareHttpExchangeRequest request, CancellationToken ct);
}

public sealed class RequestToolDiffService : IRequestToolDiffService
{
    private readonly IRequestToolRepository _repository;
    private readonly IRawHttpRenderer _renderer;
    private readonly IBodyStorageService _bodyStorage;
    private readonly DiffPlex.DiffBuilder.ISideBySideDiffBuilder _diffBuilder;

    public RequestToolDiffService(
        IRequestToolRepository repository,
        IRawHttpRenderer renderer,
        IBodyStorageService bodyStorage)
    {
        _repository = repository;
        _renderer = renderer;
        _bodyStorage = bodyStorage;
        _diffBuilder = new DiffPlex.DiffBuilder.SideBySideDiffBuilder(new DiffPlex.Differ());
    }

    public async Task<CompareHttpExchangeResponse> CompareAsync(CompareHttpExchangeRequest request, CancellationToken ct)
    {
        var leftExchange = await _repository.GetExchangeAsync(request.LeftExchangeId, ct);
        var rightExchange = await _repository.GetExchangeAsync(request.RightExchangeId, ct);

        if (leftExchange is null || rightExchange is null)
        {
            throw new InvalidOperationException("One or both exchanges not found");
        }

        var leftText = await RenderTargetAsync(leftExchange, request.Target, ct);
        var rightText = await RenderTargetAsync(rightExchange, request.Target, ct);

        var diffModel = _diffBuilder.BuildDiffModel(leftText, rightText);
        var unifiedDiff = GenerateUnifiedDiff(leftText, rightText);

        return new CompareHttpExchangeResponse(
            request.LeftExchangeId,
            request.RightExchangeId,
            request.Target,
            leftExchange.TabTitle,
            rightExchange.TabTitle,
            leftText,
            rightText,
            unifiedDiff,
            diffModel);
    }

    private async Task<string> RenderTargetAsync(HttpExchangeDetailDto exchange, RequestToolCompareTarget target, CancellationToken ct)
    {
        return target switch
        {
            RequestToolCompareTarget.RequestHeaders => RenderHeaders(exchange.RequestHeaders),
            RequestToolCompareTarget.RequestBody => await _bodyStorage.GetFullBodyAsync(exchange.RequestBodyArtifactId, exchange.RequestBody, ct) ?? "",
            RequestToolCompareTarget.RawRequest => await _renderer.RenderRequestAsync(ToRecord(exchange), ct),
            RequestToolCompareTarget.ResponseHeaders => exchange.ResponseHeaders is not null ? RenderHeaders(exchange.ResponseHeaders) : "",
            RequestToolCompareTarget.ResponseBody => await _bodyStorage.GetFullBodyAsync(exchange.ResponseBodyArtifactId, exchange.ResponseBody, ct) ?? "",
            RequestToolCompareTarget.RawResponse => await _renderer.RenderResponseAsync(ToRecord(exchange), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }

    private static string RenderHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        var sb = new StringBuilder();
        foreach (var header in headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var value in header.Value)
            {
                sb.AppendLine($"{header.Key}: {value}");
            }
        }
        return sb.ToString();
    }

    private static string GenerateUnifiedDiff(string left, string right)
    {
        var differ = new DiffPlex.Differ();
        var diffModel = new DiffPlex.DiffBuilder.SideBySideDiffBuilder(differ).BuildDiffModel(left, right);

        var sb = new StringBuilder();
        sb.AppendLine("--- Left");
        sb.AppendLine("+++ Right");

        var oldLines = diffModel.OldText.Lines;
        var newLines = diffModel.NewText.Lines;

        for (int i = 0; i < Math.Max(oldLines.Count, newLines.Count); i++)
        {
            if (i < oldLines.Count && oldLines[i].Position != i + 1)
            {
                sb.AppendLine($"- {oldLines[i].Text}");
            }
            else if (i < oldLines.Count)
            {
                var prefix = oldLines[i].Type.ToString()[0].ToString().ToLower();
                sb.AppendLine($"{prefix} {oldLines[i].Text}");
            }

            if (i < newLines.Count && newLines[i].Position != i + 1)
            {
                sb.AppendLine($"+ {newLines[i].Text}");
            }
            else if (i < newLines.Count)
            {
                var prefix = newLines[i].Type.ToString()[0].ToString().ToLower();
                sb.AppendLine($"{prefix} {newLines[i].Text}");
            }
        }

        return sb.ToString();
    }

    private static HttpExchangeRecord ToRecord(HttpExchangeDetailDto dto) =>
        new()
        {
            ExchangeId = dto.ExchangeId,
            SessionId = dto.SessionId,
            AssetId = dto.AssetId,
            ProgramId = dto.ProgramId,
            ParentExchangeId = dto.ParentExchangeId,
            Origin = dto.Origin.ToString(),
            Outcome = dto.Outcome.ToString(),
            TabTitle = dto.TabTitle,
            IsPinned = dto.IsPinned,
            RequestMethod = dto.RequestMethod,
            RequestUrl = dto.RequestUrl,
            RequestScheme = dto.RequestScheme,
            RequestHost = dto.RequestHost,
            RequestPort = dto.RequestPort,
            RequestPath = dto.RequestPath,
            RequestQuery = dto.RequestQuery,
            RequestHttpVersion = dto.RequestHttpVersion,
            RequestHeaders = System.Text.Json.JsonSerializer.Serialize(dto.RequestHeaders),
            RequestCookies = System.Text.Json.JsonSerializer.Serialize(dto.RequestCookies),
            RequestBodyInline = dto.RequestBody,
            RequestBodyArtifactId = dto.RequestBodyArtifactId,
            RequestBodySha256 = dto.RequestBodySha256,
            RequestBodySizeBytes = dto.RequestBodySizeBytes,
            RequestContentType = dto.RequestContentType,
            ResponseStatusCode = dto.ResponseStatusCode,
            ResponseReasonPhrase = dto.ResponseReasonPhrase,
            ResponseHttpVersion = dto.ResponseHttpVersion,
            ResponseHeaders = dto.ResponseHeaders is not null ? System.Text.Json.JsonSerializer.Serialize(dto.ResponseHeaders) : null,
            ResponseCookies = dto.ResponseCookies is not null ? System.Text.Json.JsonSerializer.Serialize(dto.ResponseCookies) : null,
            ResponseBodyInline = dto.ResponseBody,
            ResponseBodyArtifactId = dto.ResponseBodyArtifactId,
            ResponseBodySha256 = dto.ResponseBodySha256,
            ResponseBodySizeBytes = dto.ResponseBodySizeBytes,
            ResponseContentType = dto.ResponseContentType,
            DurationMs = dto.DurationMs,
            RedirectChain = System.Text.Json.JsonSerializer.Serialize(dto.RedirectChain),
            TlsInfo = dto.TlsInfo is not null ? System.Text.Json.JsonSerializer.Serialize(dto.TlsInfo) : null,
            NetworkError = dto.NetworkError,
            ScopeStatus = dto.ScopeStatus.ToString(),
            RateLimitKey = dto.RateLimitKey,
            ProxyId = dto.ProxyId,
            CreatedAt = dto.CreatedAt,
            SentAt = dto.SentAt,
            CompletedAt = dto.CompletedAt
        };
}
namespace Argus.RequestToolService.Options;

public sealed class RequestToolOptions
{
    public const string SectionName = "RequestTool";

    public int InlineBodyThresholdBytes { get; init; } = 64 * 1024;
    public int PreviewBodyThresholdBytes { get; init; } = 1024 * 1024;
    public int MaxBodyBytes { get; init; } = 25 * 1024 * 1024;
    public int DefaultTimeoutSeconds { get; init; } = 30;
    public int MaxRedirects { get; init; } = 10;
    public bool AllowOutOfScopeSend { get; init; } = true;
    public bool RequireOutOfScopeConfirmation { get; init; } = false;
    public bool RedactSecretsInAudit { get; init; } = true;
    public string UserAgent { get; init; } = "Argus-RequestTool/1.0";
    public bool AllowPrivateNetworkTargets { get; init; } = false;
    public bool AllowLocalhostTargets { get; init; } = false;
    public bool AllowCloudMetadataTargets { get; init; } = false;
}
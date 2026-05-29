using Argus.Contracts.Workers;

namespace Argus.RealtimeService.Workers;

public interface IWorkerTypeCatalog
{
    IReadOnlyCollection<WorkerTypeDefinition> GetAll();
    WorkerTypeDefinition? Find(string workerType);
}

public sealed record WorkerTypeDefinition(
    string WorkerType,
    string DisplayName,
    string RuntimeMode,
    string ProjectPath,
    string DeploymentName,
    IReadOnlyCollection<string> SubscribedAssetTypes,
    IReadOnlyCollection<string> ProducedAssetTypes,
    bool RequiresHttp,
    bool SupportsCheckpoint,
    int DefaultDesiredReplicas,
    int DefaultMinReplicas,
    int DefaultMaxReplicas,
    int DefaultMaxConcurrency);

public sealed class WorkerTypeCatalog : IWorkerTypeCatalog
{
    private static readonly WorkerTypeDefinition[] Definitions =
    [
        new(
            WorkerType: "AmassWorker",
            DisplayName: "Amass",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.Amass/Argus.Workers.Amass.csproj",
            DeploymentName: "amass-worker",
            SubscribedAssetTypes: ["Domain"],
            ProducedAssetTypes: ["Subdomain", "DnsRecord"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 10,
            DefaultMaxConcurrency: 2),

        new(
            WorkerType: "SubfinderWorker",
            DisplayName: "Subfinder",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.Subfinder/Argus.Workers.Subfinder.csproj",
            DeploymentName: "subfinder-worker",
            SubscribedAssetTypes: ["Domain"],
            ProducedAssetTypes: ["Subdomain"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 10,
            DefaultMaxConcurrency: 2),

        new(
            WorkerType: "DnsResolverWorker",
            DisplayName: "DNS Resolver",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.DnsResolver/Argus.Workers.DnsResolver.csproj",
            DeploymentName: "dns-resolver-worker",
            SubscribedAssetTypes: ["Domain", "Subdomain"],
            ProducedAssetTypes: ["Ip", "DnsRecord"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "HttpProbeWorker",
            DisplayName: "HTTP Probe",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.HttpProbe/Argus.Workers.HttpProbe.csproj",
            DeploymentName: "http-probe-worker",
            SubscribedAssetTypes: ["Subdomain", "Ip"],
            ProducedAssetTypes: ["Url", "HttpResponse"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 2,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 30,
            DefaultMaxConcurrency: 20),

        new(
            WorkerType: "HtmlDomSpiderWorker",
            DisplayName: "HTML DOM Spider",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.HtmlDomSpider/Argus.Workers.HtmlDomSpider.csproj",
            DeploymentName: "html-dom-spider-worker",
            SubscribedAssetTypes: ["Url"],
            ProducedAssetTypes: ["HtmlPage", "Url", "ApiEndpoint", "JavaScriptFile"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "JsExtractorWorker",
            DisplayName: "JS Extractor",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.JsExtractor/Argus.Workers.JsExtractor.csproj",
            DeploymentName: "js-extractor-worker",
            SubscribedAssetTypes: ["JavaScriptFile"],
            ProducedAssetTypes: ["ApiEndpoint", "Url", "FindingCandidate"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "RegexScannerWorker",
            DisplayName: "Regex Scanner",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.RegexScanner/Argus.Workers.RegexScanner.csproj",
            DeploymentName: "regex-scanner-worker",
            SubscribedAssetTypes: ["HtmlPage", "JavaScriptFile", "TextDocument"],
            ProducedAssetTypes: ["FindingCandidate"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "WordlistDiscoveryWorker",
            DisplayName: "Wordlist Discovery",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.WordlistDiscovery/Argus.Workers.WordlistDiscovery.csproj",
            DeploymentName: "wordlist-discovery-worker",
            SubscribedAssetTypes: ["Url"],
            ProducedAssetTypes: ["Url"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "HeadlessSpiderWorker",
            DisplayName: "Headless Spider",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.HeadlessSpider/Argus.Workers.HeadlessSpider.csproj",
            DeploymentName: "headless-spider-worker",
            SubscribedAssetTypes: ["Url"],
            ProducedAssetTypes: ["Url", "ApiEndpoint", "JavaScriptFile"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 10,
            DefaultMaxConcurrency: 4),

        new(
            WorkerType: "FingerprintWorker",
            DisplayName: "Fingerprint",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.Fingerprint/Argus.Workers.Fingerprint.csproj",
            DeploymentName: "fingerprint-worker",
            SubscribedAssetTypes: ["Url", "HttpResponse", "HtmlPage"],
            ProducedAssetTypes: ["Technology"],
            RequiresHttp: true,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "ValidationWorker",
            DisplayName: "Validation",
            RuntimeMode: "validation-service",
            ProjectPath: "src/Workers/Argus.Workers.Validation/Argus.Workers.Validation.csproj",
            DeploymentName: "validation-worker",
            SubscribedAssetTypes: ["Asset"],
            ProducedAssetTypes: ["ValidationStatus"],
            RequiresHttp: false,
            SupportsCheckpoint: false,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 1,
            DefaultMaxReplicas: 10,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "AssetScoringWorker",
            DisplayName: "Asset Scoring",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.AssetScoring/Argus.Workers.AssetScoring.csproj",
            DeploymentName: "asset-scoring-worker",
            SubscribedAssetTypes: ["Asset", "FindingCandidate"],
            ProducedAssetTypes: ["Scores", "FindingCandidate"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "FindingDeduperWorker",
            DisplayName: "Finding Deduper",
            RuntimeMode: "continuous",
            ProjectPath: "src/Workers/Argus.Workers.FindingDeduper/Argus.Workers.FindingDeduper.csproj",
            DeploymentName: "finding-deduper-worker",
            SubscribedAssetTypes: ["FindingCandidate"],
            ProducedAssetTypes: ["Finding"],
            RequiresHttp: false,
            SupportsCheckpoint: true,
            DefaultDesiredReplicas: 1,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 10,
            DefaultMaxConcurrency: 5),

        new(
            WorkerType: "HttpWorker",
            DisplayName: "HTTP Worker",
            RuntimeMode: "ephemeral",
            ProjectPath: "src/Workers/Argus.Workers.Http/Argus.Workers.Http.csproj",
            DeploymentName: "http-worker",
            SubscribedAssetTypes: ["Url"],
            ProducedAssetTypes: ["HttpResponse"],
            RequiresHttp: true,
            SupportsCheckpoint: false,
            DefaultDesiredReplicas: 0,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10),

        new(
            WorkerType: "AssetStorageWorker",
            DisplayName: "Asset Storage",
            RuntimeMode: "ephemeral",
            ProjectPath: "src/Workers/Argus.Workers.AssetStorage/Argus.Workers.AssetStorage.csproj",
            DeploymentName: "asset-storage-worker",
            SubscribedAssetTypes: ["Asset"],
            ProducedAssetTypes: ["StoredAsset"],
            RequiresHttp: false,
            SupportsCheckpoint: false,
            DefaultDesiredReplicas: 0,
            DefaultMinReplicas: 0,
            DefaultMaxReplicas: 20,
            DefaultMaxConcurrency: 10)
    ];

    public IReadOnlyCollection<WorkerTypeDefinition> GetAll() => Definitions;

    public WorkerTypeDefinition? Find(string workerType) =>
        Definitions.FirstOrDefault(x => string.Equals(x.WorkerType, workerType, StringComparison.OrdinalIgnoreCase));
}
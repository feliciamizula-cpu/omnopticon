using Microsoft.EntityFrameworkCore;

namespace Argus.AssetService.Data;

/// <summary>
/// A configurable context-menu action available for a given asset type (e.g. for "Domain":
/// "Enumerate Subdomains (Amass)" -> AmassWorker). Drives the per-asset-type action menu in the UI;
/// editable and persisted so operators can add/remove/reorder actions without code changes.
/// </summary>
public sealed class AssetTypeActionRecord
{
    public Guid ActionId { get; set; } = Guid.NewGuid();
    public string AssetType { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public string WorkerCapability { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AssetTypeActionInitializer
{
    // Adding a table to an already-created context is skipped by the schema initializer (sentinel
    // table already exists), so create it explicitly + idempotently and seed defaults once.
    public static async Task EnsureAssetTypeActionsAsync(this AssetDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS asset_type_actions (
                "ActionId" uuid PRIMARY KEY,
                "AssetType" character varying(64) NOT NULL,
                "ActionKey" character varying(64) NOT NULL,
                "Label" character varying(256) NOT NULL,
                "TaskType" character varying(64) NOT NULL,
                "WorkerCapability" character varying(128) NOT NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsEnabled" boolean NOT NULL DEFAULT TRUE,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_asset_type_actions_type_key"
                ON asset_type_actions ("AssetType", "ActionKey");
            """, ct);

        if (await db.AssetTypeActions.AnyAsync(ct)) return;

        var now = DateTimeOffset.UtcNow;
        AssetTypeActionRecord A(string type, string key, string label, string taskType, string cap, int order) =>
            new() { AssetType = type, ActionKey = key, Label = label, TaskType = taskType, WorkerCapability = cap, SortOrder = order, CreatedAt = now, UpdatedAt = now };

        db.AssetTypeActions.AddRange(
            // Domains / subdomains -> subdomain enumeration tools
            A("Domain", "enum-amass", "Enumerate Subdomains (Amass)", "enum", "AmassWorker", 10),
            A("Domain", "enum-subfinder", "Enumerate Subdomains (Subfinder)", "enum", "SubfinderWorker", 20),
            A("Domain", "spider-html", "Spider (HTML)", "spider", "HtmlDomSpiderWorker", 30),
            A("Domain", "spider-headless", "Spider (Headless)", "headless-spider", "HeadlessSpiderWorker", 40),
            A("Subdomain", "enum-amass", "Enumerate Subdomains (Amass)", "enum", "AmassWorker", 10),
            A("Subdomain", "enum-subfinder", "Enumerate Subdomains (Subfinder)", "enum", "SubfinderWorker", 20),
            A("Subdomain", "http-probe", "HTTP Probe", "http", "HttpWorker", 30),
            A("Subdomain", "spider-html", "Spider (HTML)", "spider", "HtmlDomSpiderWorker", 40),
            // URLs / pages -> spiders, script extraction, wordlist
            A("Url", "spider-html", "Spider (HTML)", "spider", "HtmlDomSpiderWorker", 10),
            A("Url", "spider-headless", "Spider (Headless)", "headless-spider", "HeadlessSpiderWorker", 20),
            A("Url", "extract-scripts", "Extract Scripts", "js-extract", "JsExtractorWorker", 30),
            A("Url", "wordlist", "Wordlist Guesses", "wordlist", "WordlistDiscoveryWorker", 40),
            A("Url", "http-fetch", "Fetch (HTTP)", "http", "HttpWorker", 50),
            A("HtmlPage", "spider-html", "Spider (HTML)", "spider", "HtmlDomSpiderWorker", 10),
            A("HtmlPage", "extract-scripts", "Extract Scripts", "js-extract", "JsExtractorWorker", 20),
            A("HtmlPage", "wordlist", "Wordlist Guesses", "wordlist", "WordlistDiscoveryWorker", 30),
            A("JavaScriptFile", "extract-scripts", "Extract Endpoints/Secrets", "js-extract", "JsExtractorWorker", 10),
            A("Ip", "http-probe", "HTTP Probe", "http", "HttpWorker", 10));

        await db.SaveChangesAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;

namespace Argus.AssetService.Data;

/// <summary>
/// Creates the AssetService tables idempotently.
///
/// <para>
/// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.EnsureCreatedAsync"/>
/// is unsafe here: <c>argusdb</c> is shared by several services, and EF's EnsureCreated no-ops as
/// soon as <em>any</em> table exists. AgentService creates the database (and
/// <c>__EFMigrationsHistory</c>) via migrations first, so by the time AssetService starts the
/// database already "exists" and its asset tables were silently never created — every query then
/// fails with <c>42P01: relation "assets" does not exist</c>.
/// </para>
/// <para>
/// We instead generate the DDL from the current EF model and rewrite CREATE TABLE / CREATE INDEX
/// to their <c>IF NOT EXISTS</c> forms, so it is safe to run on every startup against the shared
/// database. The asset entities declare no foreign keys, so there are no non-idempotent ALTER
/// statements to guard.
/// </para>
/// </summary>
public static class AssetSchemaInitializer
{
    public static Task EnsureAssetSchemaCreatedAsync(
        this AssetDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var script = dbContext.Database.GenerateCreateScript();
        return dbContext.Database.ExecuteSqlRawAsync(MakeIdempotent(script), cancellationToken);
    }

    private static string MakeIdempotent(string createScript) =>
        createScript
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
}

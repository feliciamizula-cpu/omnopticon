using Microsoft.EntityFrameworkCore;

namespace Argus.RequestToolService.Data;

public static class RequestToolSchemaInitializer
{
    public static async Task EnsureRequestToolSchemaCreatedAsync(
        this RequestToolDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var script = dbContext.Database.GenerateCreateScript();
        await dbContext.Database.ExecuteSqlRawAsync(MakeIdempotent(script), cancellationToken);
    }

    private static string MakeIdempotent(string createScript) =>
        createScript
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);
}
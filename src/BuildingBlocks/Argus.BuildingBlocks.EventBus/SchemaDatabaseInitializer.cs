using Microsoft.EntityFrameworkCore;

namespace Argus.BuildingBlocks.EventBus;

/// <summary>
/// Creates a DbContext's tables idempotently on the SHARED <c>argusdb</c> database.
///
/// <para>
/// <c>EnsureCreatedAsync()</c> no-ops once the database already exists (another service creates it
/// first), so each context's own tables were silently never created. This generates DDL straight
/// from the EF model — so it never drifts from the entities — and only applies it when this
/// context's schema is absent, detected via a sentinel table. That makes it safe to run on every
/// startup even when the shared database already holds other services' tables. CREATE TABLE/INDEX
/// are rewritten to <c>IF NOT EXISTS</c> so the shared outbox/inbox tables don't collide.
/// </para>
/// </summary>
public static class SchemaDatabaseInitializer
{
    public static async Task EnsureRelationalSchemaCreatedAsync(
        this DbContext context,
        CancellationToken cancellationToken = default)
    {
        // A table owned by THIS context (excluding the shared outbox/inbox) tells us whether this
        // context's schema has been created yet. Using a non-shared table avoids being fooled by
        // outbox_messages/inbox_messages, which other services may have already created.
        var sentinel = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .FirstOrDefault(name =>
                !string.IsNullOrEmpty(name) &&
                name != "outbox_messages" &&
                name != "inbox_messages");

        if (sentinel is null)
        {
            return;
        }

        var alreadyCreated = await context.Database
            .SqlQueryRaw<bool>("SELECT (to_regclass({0}) IS NOT NULL) AS \"Value\"", sentinel)
            .SingleAsync(cancellationToken);

        if (alreadyCreated)
        {
            return;
        }

        // Only reached on a fresh schema, so foreign-key statements (which Postgres can't guard with
        // IF NOT EXISTS) run exactly once. IF NOT EXISTS still protects the shared outbox/inbox.
        var script = context.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal);

        await context.Database.ExecuteSqlRawAsync(script, cancellationToken);
    }
}

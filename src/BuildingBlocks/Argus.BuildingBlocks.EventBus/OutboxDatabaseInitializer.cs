using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Argus.BuildingBlocks.EventBus;

public static class OutboxDatabaseInitializer
{
    public static Task EnsureArgusOutboxCreatedAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default) =>
        database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS outbox_messages (
                "OutboxMessageId" uuid PRIMARY KEY,
                "EventId" uuid NOT NULL,
                "EventType" character varying(256) NOT NULL,
                "SourceService" character varying(256) NOT NULL,
                "EnvelopeJson" jsonb NOT NULL,
                "OccurredAt" timestamp with time zone NOT NULL,
                "ProcessedAt" timestamp with time zone NULL,
                "NextAttemptAt" timestamp with time zone NULL,
                "LockedUntil" timestamp with time zone NULL,
                "LockOwner" character varying(256) NULL,
                "AttemptCount" integer NOT NULL DEFAULT 0,
                "Error" character varying(2048) NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_outbox_messages_EventId"
                ON outbox_messages ("EventId");

            CREATE INDEX IF NOT EXISTS "IX_outbox_messages_dispatch"
                ON outbox_messages ("ProcessedAt", "NextAttemptAt", "LockedUntil");

            CREATE INDEX IF NOT EXISTS "IX_outbox_messages_source_type_occurred"
                ON outbox_messages ("SourceService", "EventType", "OccurredAt");
            """,
            cancellationToken);
}

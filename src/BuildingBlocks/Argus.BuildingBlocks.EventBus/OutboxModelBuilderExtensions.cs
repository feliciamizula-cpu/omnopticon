using Microsoft.EntityFrameworkCore;

namespace Argus.BuildingBlocks.EventBus;

public static class OutboxModelBuilderExtensions
{
    public static ModelBuilder ConfigureArgusOutbox(this ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("outbox_messages");
        outbox.HasKey(message => message.OutboxMessageId);
        outbox.HasIndex(message => message.EventId).IsUnique();
        outbox.HasIndex(message => new { message.ProcessedAt, message.NextAttemptAt, message.LockedUntil });
        outbox.HasIndex(message => new { message.SourceService, message.EventType, message.OccurredAt });
        outbox.Property(message => message.EventType).HasMaxLength(256);
        outbox.Property(message => message.SourceService).HasMaxLength(256);
        outbox.Property(message => message.EnvelopeJson).HasColumnType("jsonb");
        outbox.Property(message => message.LockOwner).HasMaxLength(256);
        outbox.Property(message => message.Error).HasMaxLength(2048);

        return modelBuilder;
    }
}

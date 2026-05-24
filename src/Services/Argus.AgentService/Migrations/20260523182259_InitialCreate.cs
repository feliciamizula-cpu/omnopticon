using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ResponsibilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CurrentTaskId = table.Column<string>(type: "text", nullable: true),
                    WorkStatus = table.Column<string>(type: "text", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    Context = table.Column<string>(type: "text", nullable: true),
                    Tool = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.AgentId);
                });

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AssignedTo = table.Column<string>(type: "text", nullable: true),
                    RequeueReason = table.Column<string>(type: "text", nullable: true),
                    RecoveryContext = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.MessageId);
                });

            // outbox_messages is a shared infrastructure table created by the event bus building block.
            // Use IF NOT EXISTS so this migration is idempotent when other services have already created it.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS outbox_messages (
                    ""OutboxMessageId"" uuid NOT NULL,
                    ""EventId"" uuid NOT NULL,
                    ""EventType"" character varying(256) NOT NULL,
                    ""SourceService"" character varying(256) NOT NULL,
                    ""EnvelopeJson"" jsonb NOT NULL,
                    ""OccurredAt"" timestamp with time zone NOT NULL,
                    ""ProcessedAt"" timestamp with time zone,
                    ""NextAttemptAt"" timestamp with time zone,
                    ""LockedUntil"" timestamp with time zone,
                    ""LockOwner"" character varying(256),
                    ""AttemptCount"" integer NOT NULL,
                    ""Error"" character varying(2048),
                    CONSTRAINT ""PK_outbox_messages"" PRIMARY KEY (""OutboxMessageId"")
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_outbox_messages_EventId""
                    ON outbox_messages (""EventId"");
                CREATE INDEX IF NOT EXISTS ""IX_outbox_messages_ProcessedAt_NextAttemptAt_LockedUntil""
                    ON outbox_messages (""ProcessedAt"", ""NextAttemptAt"", ""LockedUntil"");
                CREATE INDEX IF NOT EXISTS ""IX_outbox_messages_SourceService_EventType_OccurredAt""
                    ON outbox_messages (""SourceService"", ""EventType"", ""OccurredAt"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}

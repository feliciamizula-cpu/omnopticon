using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentExecutionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Agents\" ADD COLUMN IF NOT EXISTS \"CapabilitiesJson\" jsonb NOT NULL DEFAULT '[]';");
            migrationBuilder.Sql("ALTER TABLE \"Agents\" ADD COLUMN IF NOT EXISTS \"DefaultRuntime\" text NOT NULL DEFAULT 'in_pod';");

            migrationBuilder.Sql("ALTER TABLE \"AgentTasks\" ADD COLUMN IF NOT EXISTS \"Instructions\" text;");
            migrationBuilder.Sql("ALTER TABLE \"AgentTasks\" ADD COLUMN IF NOT EXISTS \"Runtime\" text NOT NULL DEFAULT 'default';");
            migrationBuilder.Sql("ALTER TABLE \"AgentTasks\" ADD COLUMN IF NOT EXISTS \"RequiredCapabilitiesJson\" jsonb NOT NULL DEFAULT '[]';");
            migrationBuilder.Sql("ALTER TABLE \"AgentTasks\" ADD COLUMN IF NOT EXISTS \"EnabledAt\" timestamptz;");
            migrationBuilder.Sql("ALTER TABLE \"AgentTasks\" ADD COLUMN IF NOT EXISTS \"DisabledAt\" timestamptz;");

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentTaskSchedules" (
                    "ScheduleId" uuid PRIMARY KEY,
                    "TaskId" text NOT NULL,
                    "Cron" text NOT NULL,
                    "Label" text,
                    "Enabled" boolean NOT NULL DEFAULT TRUE,
                    "NextRunAt" timestamptz,
                    "LastRunAt" timestamptz,
                    "CreatedAt" timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskSchedules_TaskId"
                    ON "AgentTaskSchedules" ("TaskId");
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskSchedules_Enabled_NextRunAt"
                    ON "AgentTaskSchedules" ("Enabled", "NextRunAt");
            """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentTaskTriggers" (
                    "TriggerId" uuid PRIMARY KEY,
                    "TaskId" text NOT NULL,
                    "EventName" text NOT NULL,
                    "Enabled" boolean NOT NULL DEFAULT TRUE,
                    "CreatedAt" timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskTriggers_TaskId"
                    ON "AgentTaskTriggers" ("TaskId");
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskTriggers_EventName_Enabled"
                    ON "AgentTaskTriggers" ("EventName", "Enabled");
            """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentTaskRuns" (
                    "RunId" uuid PRIMARY KEY,
                    "TaskId" text NOT NULL,
                    "ScheduleId" uuid,
                    "TriggerId" uuid,
                    "AgentId" uuid,
                    "TriggerSource" text NOT NULL,
                    "Status" text NOT NULL,
                    "Runtime" text NOT NULL,
                    "CapabilitiesSnapshotJson" jsonb NOT NULL DEFAULT '[]',
                    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                    "StartedAt" timestamptz,
                    "CompletedAt" timestamptz,
                    "Output" text,
                    "Error" text,
                    "WorkspaceRef" text
                );
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskRuns_TaskId"   ON "AgentTaskRuns" ("TaskId");
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskRuns_AgentId"  ON "AgentTaskRuns" ("AgentId");
                CREATE INDEX IF NOT EXISTS "IX_AgentTaskRuns_CreatedAt" ON "AgentTaskRuns" ("CreatedAt");
            """);

            migrationBuilder.Sql("""
                INSERT INTO "AgentTaskSchedules" ("ScheduleId", "TaskId", "Cron", "Enabled", "CreatedAt", "NextRunAt")
                SELECT gen_random_uuid(), "TaskId", "ScheduleExpression", TRUE, now(), now()
                FROM "AgentTasks"
                WHERE "ScheduleExpression" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "AgentTaskSchedules" s WHERE s."TaskId" = "AgentTasks"."TaskId");
            """);

            migrationBuilder.Sql("""
                INSERT INTO "AgentTaskTriggers" ("TriggerId", "TaskId", "EventName", "Enabled", "CreatedAt")
                SELECT gen_random_uuid(), "TaskId", "TriggerEvent", TRUE, now()
                FROM "AgentTasks"
                WHERE "TriggerEvent" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "AgentTaskTriggers" t WHERE t."TaskId" = "AgentTasks"."TaskId");
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("-- intentionally non-destructive; reverse manually if needed");
        }
    }
}

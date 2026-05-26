using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentModelExpansion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRunAt",
                table: "AgentTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRunAt",
                table: "AgentTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultOutput",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleExpression",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetRole",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskType",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerEvent",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoleDescription",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CodeReviews",
                columns: table => new
                {
                    ReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTaskId = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewContent = table.Column<string>(type: "text", nullable: false),
                    CommitRef = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeReviews", x => x.ReviewId);
                });

            migrationBuilder.CreateTable(
                name: "SystemReports",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportContent = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemReports", x => x.ReportId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodeReviews_CreatedAt",
                table: "CodeReviews",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CodeReviews_SourceTaskId",
                table: "CodeReviews",
                column: "SourceTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemReports_CreatedAt",
                table: "SystemReports",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodeReviews");

            migrationBuilder.DropTable(
                name: "SystemReports");

            migrationBuilder.DropColumn(
                name: "LastRunAt",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "ResultOutput",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleExpression",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "TargetRole",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "TaskType",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "TriggerEvent",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "RoleDescription",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Agents");
        }
    }
}

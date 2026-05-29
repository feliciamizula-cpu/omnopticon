using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.RealtimeService.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerScaleTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceService = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CausationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    RunningTasks = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.WorkerId);
                });

            migrationBuilder.CreateTable(
                name: "WorkerScaleCommands",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkerType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PreviousDesiredReplicas = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedDesiredReplicas = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedReplicas = table.Column<int>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ScalerKind = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerScaleCommands", x => x.CommandId);
                });

            migrationBuilder.CreateTable(
                name: "WorkerScaleSettings",
                columns: table => new
                {
                    WorkerType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RuntimeMode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DesiredReplicas = table.Column<int>(type: "INTEGER", nullable: false),
                    MinReplicas = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxReplicas = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeploymentName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerScaleSettings", x => x.WorkerType);
                });

            migrationBuilder.CreateTable(
                name: "WorkerCapabilities",
                columns: table => new
                {
                    WorkerId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkerType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    SubscribedAssetTypes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerCapabilities", x => x.WorkerId);
                    table.ForeignKey(
                        name: "FK_WorkerCapabilities_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "WorkerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CorrelationId",
                table: "Events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_EventType",
                table: "Events",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_Events_RecordedAt",
                table: "Events",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Workers_LastSeenAt",
                table: "Workers",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerScaleCommands_RequestedAt",
                table: "WorkerScaleCommands",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerScaleCommands_Status",
                table: "WorkerScaleCommands",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerScaleCommands_WorkerType",
                table: "WorkerScaleCommands",
                column: "WorkerType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "WorkerCapabilities");

            migrationBuilder.DropTable(
                name: "WorkerScaleCommands");

            migrationBuilder.DropTable(
                name: "WorkerScaleSettings");

            migrationBuilder.DropTable(
                name: "Workers");
        }
    }
}

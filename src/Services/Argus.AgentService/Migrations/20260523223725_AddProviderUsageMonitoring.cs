using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderUsageMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    AccountType = table.Column<string>(type: "text", nullable: false),
                    AuthMode = table.Column<string>(type: "text", nullable: false),
                    SecretName = table.Column<string>(type: "text", nullable: true),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    RelatedToolsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RelatedModelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    WarningThresholdPercent = table.Column<decimal>(type: "numeric", nullable: false),
                    CriticalThresholdPercent = table.Column<decimal>(type: "numeric", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "ProviderUsageSnapshots",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowKind = table.Column<string>(type: "text", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: false),
                    LimitAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    UsedAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    RemainingAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    RemainingPercent = table.Column<decimal>(type: "numeric", nullable: true),
                    ResetsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageSnapshots", x => x.SnapshotId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_ProviderKey",
                table: "ProviderAccounts",
                column: "ProviderKey");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderUsageSnapshots_AccountId_ObservedAt",
                table: "ProviderUsageSnapshots",
                columns: new[] { "AccountId", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderAccounts");

            migrationBuilder.DropTable(
                name: "ProviderUsageSnapshots");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Agents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Agents");
        }
    }
}

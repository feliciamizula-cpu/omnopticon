using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.AgentService.Migrations
{
    /// <inheritdoc />
    public partial class ReseedAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"AgentSchedules\";");
            migrationBuilder.Sql("DELETE FROM \"Todos\";");
            migrationBuilder.Sql("DELETE FROM \"Agents\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

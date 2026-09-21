using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryEvaluationV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EvaluationVersion",
                table: "DiscordMemoryEvaluations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationVersion",
                table: "DiscordMemoryBackfillCursors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvaluationVersion",
                table: "DiscordMemoryEvaluations");

            migrationBuilder.DropColumn(
                name: "EvaluationVersion",
                table: "DiscordMemoryBackfillCursors");
        }
    }
}

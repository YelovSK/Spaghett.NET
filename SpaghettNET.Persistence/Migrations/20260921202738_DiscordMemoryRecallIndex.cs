using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordMemoryRecallIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscordMemories_GuildId_CreatedAt",
                table: "DiscordMemories");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_GuildId_MessageId",
                table: "DiscordMemories",
                columns: new[] { "GuildId", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscordMemories_GuildId_MessageId",
                table: "DiscordMemories");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_GuildId_CreatedAt",
                table: "DiscordMemories",
                columns: new[] { "GuildId", "CreatedAt" });
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordMemoryRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscordMemories_MessageId",
                table: "DiscordMemories");

            migrationBuilder.RenameColumn(
                name: "MessageId",
                table: "DiscordMemories",
                newName: "LastMessageId");

            migrationBuilder.RenameIndex(
                name: "IX_DiscordMemories_GuildId_MessageId",
                table: "DiscordMemories",
                newName: "IX_DiscordMemories_GuildId_LastMessageId");

            migrationBuilder.AddColumn<long>(
                name: "FirstMessageId",
                table: "DiscordMemories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("UPDATE DiscordMemories SET FirstMessageId = LastMessageId;");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_FirstMessageId",
                table: "DiscordMemories",
                column: "FirstMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscordMemories_FirstMessageId",
                table: "DiscordMemories");

            migrationBuilder.DropColumn(
                name: "FirstMessageId",
                table: "DiscordMemories");

            migrationBuilder.RenameColumn(
                name: "LastMessageId",
                table: "DiscordMemories",
                newName: "MessageId");

            migrationBuilder.RenameIndex(
                name: "IX_DiscordMemories_GuildId_LastMessageId",
                table: "DiscordMemories",
                newName: "IX_DiscordMemories_GuildId_MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_MessageId",
                table: "DiscordMemories",
                column: "MessageId",
                unique: true);
        }
    }
}

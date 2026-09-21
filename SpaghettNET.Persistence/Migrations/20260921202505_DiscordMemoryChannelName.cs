using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordMemoryChannelName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChannelName",
                table: "DiscordMemories",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Memories stored before the name was captured fall back to the channel id.
            migrationBuilder.Sql(
                "UPDATE DiscordMemories SET ChannelName = 'channel ' || ChannelId WHERE ChannelName = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChannelName",
                table: "DiscordMemories");
        }
    }
}

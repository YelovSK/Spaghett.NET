using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordMemoryExcludedChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordMemoryExcludedChannels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordMemoryExcludedChannels", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordMemoryExcludedChannels");
        }
    }
}

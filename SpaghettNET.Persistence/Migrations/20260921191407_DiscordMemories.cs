using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpaghettNET.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordMemories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordMemoryBackfillCursors",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    BeforeMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    IsComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordMemoryBackfillCursors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordMemoryEvaluations",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    WasStored = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordMemoryEvaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_GuildId_CreatedAt",
                table: "DiscordMemories",
                columns: new[] { "GuildId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordMemories_MessageId",
                table: "DiscordMemories",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordMemories");

            migrationBuilder.DropTable(
                name: "DiscordMemoryBackfillCursors");

            migrationBuilder.DropTable(
                name: "DiscordMemoryEvaluations");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neruna.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReminderStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    SnoozedUntilUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Dismissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpiresUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderStates", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderStates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neruna.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarIsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "Calendars",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "Calendars");
        }
    }
}

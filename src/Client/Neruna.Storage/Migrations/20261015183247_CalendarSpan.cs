using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neruna.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CalendarSpan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SpanEnd",
                table: "CalendarObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpanStart",
                table: "CalendarObjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarObjects_CalendarId_SpanStart",
                table: "CalendarObjects",
                columns: new[] { "CalendarId", "SpanStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalendarObjects_CalendarId_SpanStart",
                table: "CalendarObjects");

            migrationBuilder.DropColumn(
                name: "SpanEnd",
                table: "CalendarObjects");

            migrationBuilder.DropColumn(
                name: "SpanStart",
                table: "CalendarObjects");
        }
    }
}

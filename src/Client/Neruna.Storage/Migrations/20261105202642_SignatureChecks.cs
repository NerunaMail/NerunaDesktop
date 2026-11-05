using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neruna.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SignatureChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignatureChecks",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Epoch = table.Column<string>(type: "TEXT", nullable: false),
                    CheckedUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureChecks", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureChecks_CheckedUnixMs",
                table: "SignatureChecks",
                column: "CheckedUnixMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignatureChecks");
        }
    }
}

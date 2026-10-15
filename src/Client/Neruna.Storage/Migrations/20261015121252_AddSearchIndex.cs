using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neruna.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SearchLevel",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SearchLevel_DateUnixMs",
                table: "Messages",
                columns: new[] { "SearchLevel", "DateUnixMs" });

            // Full-text index of the messages (rowid = Messages.Id). Contentless: it keeps only the index, not a second
            // copy of the texts; rows can still be deleted. Diacritics are ignored ("Zurich" finds "Zürich"); prefixes
            // of 2 and 3 characters are indexed for fast searching while typing.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE MessageSearch USING fts5(
                    Sender, Recipients, Subject, Body, Attachments,
                    content='', contentless_delete=1, prefix='2 3', tokenize='unicode61 remove_diacritics 2');
                """);

            // A message that goes (deleted, moved, its folder or account removed) leaves the index too.
            migrationBuilder.Sql("""
                CREATE TRIGGER Messages_SearchDelete AFTER DELETE ON Messages
                BEGIN
                    DELETE FROM MessageSearch WHERE rowid = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS Messages_SearchDelete;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS MessageSearch;");

            migrationBuilder.DropIndex(
                name: "IX_Messages_SearchLevel_DateUnixMs",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SearchLevel",
                table: "Messages");
        }
    }
}

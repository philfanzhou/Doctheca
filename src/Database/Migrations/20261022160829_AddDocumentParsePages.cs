using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentParsePages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_parse_pages",
                columns: table => new
                {
                    parse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    label_source = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_parse_pages", x => new { x.parse_id, x.page_number });
                    table.CheckConstraint("CK_document_parse_pages_page_number", "page_number > 0");
                    table.ForeignKey(
                        name: "FK_document_parse_pages_document_parses_parse_id",
                        column: x => x.parse_id,
                        principalTable: "document_parses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_parse_pages");
        }
    }
}

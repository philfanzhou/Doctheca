using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddStructureAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_structure_audio",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    match_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    link_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_structure_audio", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_structure_audio_document_parses_parse_id",
                        column: x => x.parse_id,
                        principalTable: "document_parses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_node_id",
                table: "document_structure_audio",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_object_key",
                table: "document_structure_audio",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_parse_id_source_path",
                table: "document_structure_audio",
                columns: new[] { "parse_id", "source_path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_structure_audio");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sequence",
                table: "document_parse_blocks",
                type: "integer",
                nullable: true);

            // Backfill the global reading order before the unique index exists. A parse whose
            // blocks all carry a distinct non-negative integer StructaDoc "sequence" in
            // block_data keeps it; every other parse (legacy MinerU rows, partial or duplicated
            // values) is numbered 0..n-1 by its existing (page_id, sort_index) order, with id
            // as a deterministic tie-breaker. No other column or row is changed.
            migrationBuilder.Sql("""
                WITH quality AS (
                    SELECT parse_id,
                           bool_and(COALESCE(block_data ->> 'sequence' ~ '^[0-9]{1,9}$', false)) AS all_valid,
                           count(*) AS total,
                           count(DISTINCT block_data ->> 'sequence') AS distinct_values
                    FROM document_parse_blocks
                    GROUP BY parse_id
                )
                UPDATE document_parse_blocks AS b
                SET sequence = (b.block_data ->> 'sequence')::integer
                FROM quality AS q
                WHERE b.parse_id = q.parse_id
                  AND q.all_valid
                  AND q.distinct_values = q.total;

                UPDATE document_parse_blocks AS b
                SET sequence = numbered.position
                FROM (
                    SELECT id,
                           (row_number() OVER (PARTITION BY parse_id ORDER BY page_id, sort_index, id) - 1)::integer AS position
                    FROM document_parse_blocks
                    WHERE parse_id IN (SELECT parse_id FROM document_parse_blocks WHERE sequence IS NULL)
                ) AS numbered
                WHERE b.id = numbered.id;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "structure_definition_id",
                table: "document_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "structure_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_structure_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "structure_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    parent_level_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_structure_levels", x => x.id);
                    table.ForeignKey(
                        name: "FK_structure_levels_structure_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "structure_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_structure_levels_structure_levels_parent_level_id",
                        column: x => x.parent_level_id,
                        principalTable: "structure_levels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_structure_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    start_sequence = table.Column<int>(type: "integer", nullable: false),
                    end_sequence = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_structure_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_structure_nodes_document_parses_parse_id",
                        column: x => x.parse_id,
                        principalTable: "document_parses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_structure_nodes_document_structure_nodes_parent_id",
                        column: x => x.parent_id,
                        principalTable: "document_structure_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_structure_nodes_structure_levels_level_id",
                        column: x => x.level_id,
                        principalTable: "structure_levels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_blocks_parse_id_sequence",
                table: "document_parse_blocks",
                columns: new[] { "parse_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_files_structure_definition_id",
                table: "document_files",
                column: "structure_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_nodes_level_id",
                table: "document_structure_nodes",
                column: "level_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_nodes_parent_id",
                table: "document_structure_nodes",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_nodes_parse_id",
                table: "document_structure_nodes",
                column: "parse_id");

            migrationBuilder.CreateIndex(
                name: "IX_structure_levels_definition_id",
                table: "structure_levels",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_structure_levels_parent_level_id",
                table: "structure_levels",
                column: "parent_level_id");

            migrationBuilder.AddForeignKey(
                name: "FK_document_files_structure_definitions_structure_definition_id",
                table: "document_files",
                column: "structure_definition_id",
                principalTable: "structure_definitions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_files_structure_definitions_structure_definition_id",
                table: "document_files");

            migrationBuilder.DropTable(
                name: "document_structure_nodes");

            migrationBuilder.DropTable(
                name: "structure_levels");

            migrationBuilder.DropTable(
                name: "structure_definitions");

            migrationBuilder.DropIndex(
                name: "IX_document_parse_blocks_parse_id_sequence",
                table: "document_parse_blocks");

            migrationBuilder.DropIndex(
                name: "IX_document_files_structure_definition_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "sequence",
                table: "document_parse_blocks");

            migrationBuilder.DropColumn(
                name: "structure_definition_id",
                table: "document_files");
        }
    }
}

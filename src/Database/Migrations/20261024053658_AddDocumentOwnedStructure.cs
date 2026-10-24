using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOwnedStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_structure_audio_document_parses_parse_id",
                table: "document_structure_audio");

            migrationBuilder.DropForeignKey(
                name: "FK_document_structure_nodes_document_parses_parse_id",
                table: "document_structure_nodes");

            migrationBuilder.AlterColumn<Guid>(name: "parse_id", table: "document_structure_audio", type: "uuid",
                nullable: true, oldClrType: typeof(Guid), oldType: "uuid");

            migrationBuilder.DropIndex(
                name: "IX_document_structure_audio_parse_id_source_path",
                table: "document_structure_audio");

            migrationBuilder.AlterColumn<int>(
                name: "start_sequence",
                table: "document_structure_nodes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<Guid>(
                name: "parse_id",
                table: "document_structure_nodes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "end_sequence",
                table: "document_structure_nodes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<Guid>(
                name: "document_file_id",
                table: "document_structure_nodes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tree_id",
                table: "document_structure_nodes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "document_file_id",
                table: "document_structure_audio",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tree_id",
                table: "document_structure_audio",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "expected_binding_generation",
                table: "document_parses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "current_structure_tree_id",
                table: "document_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "structure_binding_generation",
                table: "document_files",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_document_parses_document_file_id_id",
                table: "document_parses",
                columns: new[] { "document_file_id", "id" });

            migrationBuilder.CreateTable(
                name: "document_structure_trees",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin_parse_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_structure_trees", x => x.id);
                    table.UniqueConstraint("AK_document_structure_trees_document_file_id_id", x => new { x.document_file_id, x.id });
                    table.ForeignKey(
                        name: "FK_document_structure_trees_document_files_document_file_id",
                        column: x => x.document_file_id,
                        principalTable: "document_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // One separate tree per legacy parse, including audio-only history. Never copy bytes.
            migrationBuilder.Sql("""
                INSERT INTO document_structure_trees (id, document_file_id, origin_parse_id)
                SELECT gen_random_uuid(), p.document_file_id, p.id FROM document_parses p
                WHERE EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.parse_id = p.id)
                   OR EXISTS (SELECT 1 FROM document_structure_audio a WHERE a.parse_id = p.id);
                UPDATE document_structure_nodes n SET document_file_id = t.document_file_id, tree_id = t.id
                FROM document_structure_trees t WHERE t.origin_parse_id = n.parse_id;
                UPDATE document_structure_audio a SET document_file_id = t.document_file_id, tree_id = t.id
                FROM document_structure_trees t WHERE t.origin_parse_id = a.parse_id;
                ALTER TABLE document_files DISABLE TRIGGER set_document_files_updated_at;
                UPDATE document_files f SET current_structure_tree_id = (
                    SELECT t.id FROM document_structure_trees t JOIN document_parses p ON p.id = t.origin_parse_id
                    WHERE t.document_file_id = f.id AND p.status = 'parsed'
                      AND EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.tree_id = t.id)
                    ORDER BY p.parsed_at DESC NULLS LAST, p.id DESC LIMIT 1);
                ALTER TABLE document_files ENABLE TRIGGER set_document_files_updated_at;
                ALTER TABLE document_structure_nodes ALTER COLUMN document_file_id DROP DEFAULT, ALTER COLUMN tree_id DROP DEFAULT;
                ALTER TABLE document_structure_audio ALTER COLUMN document_file_id DROP DEFAULT, ALTER COLUMN tree_id DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_nodes_document_file_id_parse_id",
                table: "document_structure_nodes",
                columns: new[] { "document_file_id", "parse_id" });

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_nodes_document_file_id_tree_id",
                table: "document_structure_nodes",
                columns: new[] { "document_file_id", "tree_id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_document_structure_nodes_content_binding",
                table: "document_structure_nodes",
                sql: "(parse_id IS NULL AND start_sequence IS NULL AND end_sequence IS NULL) OR (parse_id IS NOT NULL AND start_sequence IS NOT NULL AND end_sequence IS NOT NULL AND start_sequence <= end_sequence)");

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_document_file_id_tree_id",
                table: "document_structure_audio",
                columns: new[] { "document_file_id", "tree_id" });

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_tree_id_source_path",
                table: "document_structure_audio",
                columns: new[] { "tree_id", "source_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_files_id_current_structure_tree_id",
                table: "document_files",
                columns: new[] { "id", "current_structure_tree_id" });

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_trees_origin_parse_id",
                table: "document_structure_trees",
                column: "origin_parse_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_document_files_document_structure_trees_id_current_structur~",
                table: "document_files",
                columns: new[] { "id", "current_structure_tree_id" },
                principalTable: "document_structure_trees",
                principalColumns: new[] { "document_file_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_structure_audio_document_structure_trees_document_~",
                table: "document_structure_audio",
                columns: new[] { "document_file_id", "tree_id" },
                principalTable: "document_structure_trees",
                principalColumns: new[] { "document_file_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_document_structure_nodes_document_parses_document_file_id_p~",
                table: "document_structure_nodes",
                columns: new[] { "document_file_id", "parse_id" },
                principalTable: "document_parses",
                principalColumns: new[] { "document_file_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_structure_nodes_document_structure_trees_document_~",
                table: "document_structure_nodes",
                columns: new[] { "document_file_id", "tree_id" },
                principalTable: "document_structure_trees",
                principalColumns: new[] { "document_file_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $ownership_down$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM document_structure_trees t LEFT JOIN document_parses p ON p.id = t.origin_parse_id
                        WHERE p.id IS NULL OR p.document_file_id <> t.document_file_id
                           OR (NOT EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.tree_id = t.id)
                               AND NOT EXISTS (SELECT 1 FROM document_structure_audio a WHERE a.tree_id = t.id)))
                       OR EXISTS (SELECT 1 FROM document_structure_nodes n JOIN document_structure_trees t ON t.id = n.tree_id
                                  WHERE n.parse_id IS DISTINCT FROM t.origin_parse_id OR n.start_sequence IS NULL OR n.end_sequence IS NULL)
                       OR EXISTS (SELECT 1 FROM document_structure_audio a JOIN document_structure_trees t ON t.id = a.tree_id
                                  WHERE a.parse_id IS DISTINCT FROM t.origin_parse_id)
                       OR EXISTS (
                           SELECT 1 FROM document_files f WHERE f.current_structure_tree_id IS DISTINCT FROM (
                               SELECT t.id FROM document_structure_trees t JOIN document_parses p ON p.id = t.origin_parse_id
                               WHERE t.document_file_id = f.id AND p.status = 'parsed'
                                 AND EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.tree_id = t.id)
                               ORDER BY p.parsed_at DESC NULLS LAST, p.id DESC LIMIT 1))
                    THEN
                        RAISE EXCEPTION 'Document ownership cannot be downgraded losslessly. Restore a verified backup with its matching image and configuration.';
                    END IF;
                END
                $ownership_down$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_document_files_document_structure_trees_id_current_structur~",
                table: "document_files");

            migrationBuilder.DropForeignKey(
                name: "FK_document_structure_audio_document_structure_trees_document_~",
                table: "document_structure_audio");

            migrationBuilder.DropForeignKey(
                name: "FK_document_structure_nodes_document_parses_document_file_id_p~",
                table: "document_structure_nodes");

            migrationBuilder.DropForeignKey(
                name: "FK_document_structure_nodes_document_structure_trees_document_~",
                table: "document_structure_nodes");

            migrationBuilder.DropTable(
                name: "document_structure_trees");

            migrationBuilder.DropIndex(
                name: "IX_document_structure_nodes_document_file_id_parse_id",
                table: "document_structure_nodes");

            migrationBuilder.DropIndex(
                name: "IX_document_structure_nodes_document_file_id_tree_id",
                table: "document_structure_nodes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_document_structure_nodes_content_binding",
                table: "document_structure_nodes");

            migrationBuilder.DropIndex(
                name: "IX_document_structure_audio_document_file_id_tree_id",
                table: "document_structure_audio");

            migrationBuilder.DropIndex(
                name: "IX_document_structure_audio_tree_id_source_path",
                table: "document_structure_audio");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_document_parses_document_file_id_id",
                table: "document_parses");

            migrationBuilder.DropIndex(
                name: "IX_document_files_id_current_structure_tree_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "document_file_id",
                table: "document_structure_nodes");

            migrationBuilder.DropColumn(
                name: "tree_id",
                table: "document_structure_nodes");

            migrationBuilder.DropColumn(
                name: "document_file_id",
                table: "document_structure_audio");

            migrationBuilder.DropColumn(
                name: "tree_id",
                table: "document_structure_audio");

            migrationBuilder.DropColumn(
                name: "expected_binding_generation",
                table: "document_parses");

            migrationBuilder.DropColumn(
                name: "current_structure_tree_id",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "structure_binding_generation",
                table: "document_files");

            migrationBuilder.AlterColumn<int>(
                name: "start_sequence",
                table: "document_structure_nodes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "parse_id",
                table: "document_structure_nodes",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "end_sequence",
                table: "document_structure_nodes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(name: "parse_id", table: "document_structure_audio", type: "uuid",
                nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_structure_audio_parse_id_source_path",
                table: "document_structure_audio",
                columns: new[] { "parse_id", "source_path" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_document_structure_audio_document_parses_parse_id",
                table: "document_structure_audio",
                column: "parse_id",
                principalTable: "document_parses",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_document_structure_nodes_document_parses_parse_id",
                table: "document_structure_nodes",
                column: "parse_id",
                principalTable: "document_parses",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

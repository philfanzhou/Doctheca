using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    structadoc_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    subject = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    grade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    year = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", rowVersion: true, nullable: true, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_parses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    external_task_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    structadoc_parse_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    markdown_content = table.Column<string>(type: "text", nullable: true),
                    content_list = table.Column<string>(type: "jsonb", nullable: true),
                    content_list_v2 = table.Column<string>(type: "jsonb", nullable: true),
                    model_json = table.Column<string>(type: "jsonb", nullable: true),
                    layout_json = table.Column<string>(type: "jsonb", nullable: true),
                    zip_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_parses", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_parses_document_files_document_file_id",
                        column: x => x.document_file_id,
                        principalTable: "document_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_parse_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    image_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_parse_images", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_parse_images_document_parses_parse_id",
                        column: x => x.parse_id,
                        principalTable: "document_parses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_parse_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<int>(type: "integer", nullable: false),
                    sort_index = table.Column<int>(type: "integer", nullable: false),
                    block_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text_content = table.Column<string>(type: "text", nullable: true),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    block_data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sub_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    text_level = table.Column<int>(type: "integer", nullable: false),
                    text_format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bbox_x0 = table.Column<float>(type: "real", nullable: true),
                    bbox_y0 = table.Column<float>(type: "real", nullable: true),
                    bbox_x1 = table.Column<float>(type: "real", nullable: true),
                    bbox_y1 = table.Column<float>(type: "real", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_parse_blocks", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_parse_blocks_document_parse_images_image_id",
                        column: x => x.image_id,
                        principalTable: "document_parse_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_document_parse_blocks_document_parses_parse_id",
                        column: x => x.parse_id,
                        principalTable: "document_parses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_files_file_name",
                table: "document_files",
                column: "file_name");

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_blocks_block_type",
                table: "document_parse_blocks",
                column: "block_type");

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_blocks_image_id",
                table: "document_parse_blocks",
                column: "image_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_blocks_parse_id_page_id_sort_index",
                table: "document_parse_blocks",
                columns: new[] { "parse_id", "page_id", "sort_index" });

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_images_parse_id",
                table: "document_parse_images",
                column: "parse_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parses_document_file_id",
                table: "document_parses",
                column: "document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parses_model_version",
                table: "document_parses",
                column: "model_version");

            migrationBuilder.CreateIndex(
                name: "IX_document_parses_status",
                table: "document_parses",
                column: "status");

            // The updated_at trigger belongs to the baseline (the model declares it through
            // HasTrigger and the retired initializer created it on every path). The scaffold
            // does not emit trigger DDL, so it is declared here explicitly.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION set_document_files_updated_at()
                RETURNS TRIGGER AS $$
                BEGIN
                    NEW.updated_at = NOW();
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS set_document_files_updated_at ON document_files;
                CREATE TRIGGER set_document_files_updated_at
                    BEFORE UPDATE ON document_files
                    FOR EACH ROW
                    EXECUTE FUNCTION set_document_files_updated_at();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS set_document_files_updated_at ON document_files");

            migrationBuilder.DropTable(
                name: "document_parse_blocks");

            migrationBuilder.DropTable(
                name: "document_parse_images");

            migrationBuilder.DropTable(
                name: "document_parses");

            migrationBuilder.DropTable(
                name: "document_files");
        }
    }
}

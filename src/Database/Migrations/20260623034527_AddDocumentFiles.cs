using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.DocRetrieval.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "progress",
                table: "document_ingestion_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "progress_stage",
                table: "document_ingestion_jobs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    external_task_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    markdown_content = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_file_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    image_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_file_images", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_file_images_document_files_document_file_id",
                        column: x => x.document_file_id,
                        principalTable: "document_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_file_images_document_file_id",
                table: "document_file_images",
                column: "document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_files_status",
                table: "document_files",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_file_images");

            migrationBuilder.DropTable(
                name: "document_files");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "progress",
                table: "document_ingestion_jobs");

            migrationBuilder.DropColumn(
                name: "progress_stage",
                table: "document_ingestion_jobs");
        }
    }
}

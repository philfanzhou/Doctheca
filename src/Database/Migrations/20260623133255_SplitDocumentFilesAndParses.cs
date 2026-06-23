using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.DocRetrieval.Database.Migrations
{
    /// <inheritdoc />
    public partial class SplitDocumentFilesAndParses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 0: Create document_files table if it doesn't exist (previous migration may have rolled back)
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS document_files (
                    id uuid PRIMARY KEY,
                    file_name varchar(500) NOT NULL,
                    file_path varchar(500) NOT NULL,
                    file_size bigint NOT NULL DEFAULT 0,
                    content_type varchar(100) NOT NULL,
                    status varchar(30),
                    external_task_id varchar(100),
                    markdown_content text,
                    error_message text,
                    created_by uuid,
                    parsed_at timestamptz,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz
                );

                CREATE TABLE IF NOT EXISTS document_file_images (
                    id uuid PRIMARY KEY,
                    document_file_id uuid NOT NULL REFERENCES document_files(id) ON DELETE CASCADE,
                    image_name varchar(200) NOT NULL,
                    image_path varchar(500) NOT NULL,
                    content_type varchar(50) NOT NULL DEFAULT 'image/jpeg',
                    file_size bigint NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS IX_document_files_status ON document_files(status);
                CREATE INDEX IF NOT EXISTS IX_document_file_images_document_file_id ON document_file_images(document_file_id);
            ");

            // Step 1: Create new tables
            migrationBuilder.CreateTable(
                name: "document_parses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    external_task_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    markdown_content = table.Column<string>(type: "text", nullable: true),
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

            // Step 2: Migrate existing data from document_files to document_parses
            // Only migrate rows that have a non-null status (meaning they were parsed or attempted)
            migrationBuilder.Sql(@"
                INSERT INTO document_parses (id, document_file_id, status, external_task_id, markdown_content, error_message, parsed_at)
                SELECT
                    gen_random_uuid(),
                    df.id,
                    CASE df.status
                        WHEN 'uploaded' THEN 'pending'
                        WHEN 'pending_parse' THEN 'pending'
                        WHEN 'parsing' THEN 'parsing'
                        WHEN 'parsed' THEN 'parsed'
                        WHEN 'parse_failed' THEN 'failed'
                        ELSE 'pending'
                    END,
                    df.external_task_id,
                    df.markdown_content,
                    df.error_message,
                    df.parsed_at
                FROM document_files df
                WHERE df.status IS NOT NULL AND df.status != 'uploaded';
            ");

            // Step 3: Migrate existing data from document_file_images to document_parse_images
            migrationBuilder.Sql(@"
                INSERT INTO document_parse_images (id, parse_id, image_name, image_path, content_type)
                SELECT
                    gen_random_uuid(),
                    dp.id,
                    dfi.image_name,
                    dfi.image_path,
                    dfi.content_type
                FROM document_file_images dfi
                JOIN document_parses dp ON dp.document_file_id = dfi.document_file_id;
            ");

            // Step 4: Drop old document_file_images table
            migrationBuilder.DropTable(name: "document_file_images");

            // Step 5: Remove columns that moved from document_files to document_parses
            migrationBuilder.DropIndex(name: "IX_document_files_status", table: "document_files");

            migrationBuilder.DropColumn(name: "file_size", table: "document_files");
            migrationBuilder.DropColumn(name: "status", table: "document_files");
            migrationBuilder.DropColumn(name: "external_task_id", table: "document_files");
            migrationBuilder.DropColumn(name: "markdown_content", table: "document_files");
            migrationBuilder.DropColumn(name: "error_message", table: "document_files");
            migrationBuilder.DropColumn(name: "parsed_at", table: "document_files");

            // Step 6: Set database-generated defaults for created_at and updated_at
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "created_at",
                table: "document_files",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "updated_at",
                table: "document_files",
                type: "timestamp with time zone",
                nullable: true,
                defaultValueSql: "NOW()");

            // Step 7: Create trigger for auto-updating updated_at
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION set_document_files_updated_at()
                RETURNS TRIGGER AS $$
                BEGIN
                    NEW.updated_at = NOW();
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER set_document_files_updated_at
                    BEFORE UPDATE ON document_files
                    FOR EACH ROW
                    EXECUTE FUNCTION set_document_files_updated_at();
            ");

            // Step 8: Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_document_parse_images_parse_id",
                table: "document_parse_images",
                column: "parse_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parses_document_file_id",
                table: "document_parses",
                column: "document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parses_status",
                table: "document_parses",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop trigger and function
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS set_document_files_updated_at ON document_files;
                DROP FUNCTION IF EXISTS set_document_files_updated_at();
            ");

            // Re-add columns to document_files
            migrationBuilder.AddColumn<long>(name: "file_size", table: "document_files", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<string>(name: "status", table: "document_files", type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "uploaded");
            migrationBuilder.AddColumn<string>(name: "external_task_id", table: "document_files", type: "character varying(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(name: "markdown_content", table: "document_files", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "error_message", table: "document_files", type: "text", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "parsed_at", table: "document_files", type: "timestamp with time zone", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_document_files_status", table: "document_files", column: "status");

            // Re-create document_file_images table
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

            migrationBuilder.CreateIndex(name: "IX_document_file_images_document_file_id", table: "document_file_images", column: "document_file_id");

            migrationBuilder.DropTable(name: "document_parse_images");
            migrationBuilder.DropTable(name: "document_parses");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.DocRetrieval.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    grade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    tags = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    llm_profile_json = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_ingestion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parser_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ocr_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_ingestion_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_ingestion_jobs_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    image_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_pages", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_pages_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_segment_backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backup_data = table.Column<string>(type: "text", nullable: false),
                    correction_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_segment_backups", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_segment_backups_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sentence_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    segment_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_segments_document_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "document_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_segments_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    stem = table.Column<string>(type: "text", nullable: false),
                    options_json = table.Column<string>(type: "text", nullable: true),
                    answer_area = table.Column<string>(type: "text", nullable: true),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_question_segments_document_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "document_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_question_segments_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    question_segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    token_stem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    start_offset = table.Column<int>(type: "integer", nullable: false),
                    end_offset = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_occurrences_document_segments_segment_id",
                        column: x => x.segment_id,
                        principalTable: "document_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_document_occurrences_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_occurrences_question_segments_question_segment_id",
                        column: x => x.question_segment_id,
                        principalTable: "question_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_ingestion_jobs_document_id",
                table: "document_ingestion_jobs",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_ingestion_jobs_status",
                table: "document_ingestion_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_document_occurrences_document_id_token_stem",
                table: "document_occurrences",
                columns: new[] { "document_id", "token_stem" });

            migrationBuilder.CreateIndex(
                name: "IX_document_occurrences_document_id_token_text",
                table: "document_occurrences",
                columns: new[] { "document_id", "token_text" });

            migrationBuilder.CreateIndex(
                name: "IX_document_occurrences_question_segment_id",
                table: "document_occurrences",
                column: "question_segment_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_occurrences_segment_id",
                table: "document_occurrences",
                column: "segment_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_pages_document_id_page_number",
                table: "document_pages",
                columns: new[] { "document_id", "page_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_segment_backups_document_id",
                table: "document_segment_backups",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_segments_document_id_sentence_id",
                table: "document_segments",
                columns: new[] { "document_id", "sentence_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_segments_page_id",
                table: "document_segments",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "IX_documents_file_hash",
                table: "documents",
                column: "file_hash");

            migrationBuilder.CreateIndex(
                name: "IX_documents_status",
                table: "documents",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_documents_subject_grade_year",
                table: "documents",
                columns: new[] { "subject", "grade", "year" });

            migrationBuilder.CreateIndex(
                name: "IX_documents_title",
                table: "documents",
                column: "title",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_segments_document_id_question_id",
                table: "question_segments",
                columns: new[] { "document_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_segments_page_id",
                table: "question_segments",
                column: "page_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_ingestion_jobs");

            migrationBuilder.DropTable(
                name: "document_occurrences");

            migrationBuilder.DropTable(
                name: "document_segment_backups");

            migrationBuilder.DropTable(
                name: "document_segments");

            migrationBuilder.DropTable(
                name: "question_segments");

            migrationBuilder.DropTable(
                name: "document_pages");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}

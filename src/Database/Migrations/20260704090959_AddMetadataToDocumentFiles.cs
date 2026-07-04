using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.DocLibrary.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataToDocumentFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only add the three new metadata columns to document_files.
            // Other table/column operations detected by EF diff are skipped because
            // those tables/columns already exist in the database (created by DatabaseInitializer
            // or earlier migrations not tracked in the snapshot).
            migrationBuilder.AddColumn<string>(
                name: "grade",
                table: "document_files",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "document_files",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "year",
                table: "document_files",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "grade",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "document_files");

            migrationBuilder.DropColumn(
                name: "year",
                table: "document_files");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.DocRetrieval.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBlockIdFromDocumentSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "block_id",
                table: "document_segments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "block_id",
                table: "document_segments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Doctheca.Database.Migrations
{
    /// <inheritdoc />
    public partial class ClassifyLevelLessTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The CASE is the conversion boundary: malformed input never reaches numeric.
            // ASCII whitespace matches invariant NumberStyles.Integer; stored subtypes are <=50 chars.
            migrationBuilder.Sql("""
                UPDATE document_parse_blocks
                SET text_level = -2
                WHERE block_type = 'title' AND text_level <= 0 AND text_level <> -2
                  AND NOT CASE
                    WHEN left(sub_type, 8) COLLATE "C" ~ '^[hH][eE][aA][dD][iI][nN][gG]-$'
                      AND substring(sub_type FROM 9) COLLATE "C"
                        ~ E'^[ \\t\\n\\013\\f\\r]*[+]?[0-9]+[ \\t\\n\\013\\f\\r]*$'
                    THEN regexp_replace(substring(sub_type FROM 9), '[^0-9]', '', 'g')::numeric
                         BETWEEN 1 AND 2147483647
                    ELSE false
                  END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberate representation collapse; exact historical recovery requires a backup.
            migrationBuilder.Sql("""
                UPDATE document_parse_blocks SET text_level = -1
                WHERE block_type = 'title' AND text_level = -2;
                """);
        }
    }
}

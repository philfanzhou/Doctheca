using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Doctheca.Common.Database;

namespace Doctheca.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(DocthecaDbContext context, ILoggerFactory loggerFactory)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, loggerFactory, GetTableCreationSql);
        await EnsureColumnsAsync(context, loggerFactory);
    }

    private static async Task EnsureColumnsAsync(DocthecaDbContext context, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DatabaseInitializer");

        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        // Column migrations for legacy databases (migrations stamped but DDL not applied)
        var alterStatements = new[]
        {
            // document_parses: add content_list + zip_path (phase 2)
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list jsonb NULL",
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS zip_path character varying(500) NULL",
            // document_parses: model_version (parse model version: vlm/pipeline)
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_version character varying(20) NOT NULL DEFAULT 'vlm'",
            // document_parses: new jsonb columns for MinerU pipeline output
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list_v2 jsonb NULL",
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_json jsonb NULL",
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS layout_json jsonb NULL",
            // document_parses: drop legacy layout_pdf_path column (replaced by layout_json)
            "ALTER TABLE document_parses DROP COLUMN IF EXISTS layout_pdf_path",
            // document_files: subject/grade/year metadata columns (DocumentMetadataAnalysis feature)
            "ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject character varying(50) NULL",
            "ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade character varying(20) NULL",
            "ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year character varying(10) NULL",
            // StructaDoc migration (ADR-0009): originals owned by StructaDoc, file_path becomes legacy-only
            "ALTER TABLE document_files ADD COLUMN IF NOT EXISTS structadoc_document_id uuid NULL",
            "ALTER TABLE document_files ALTER COLUMN file_path DROP NOT NULL",
            "ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS structadoc_parse_run_id uuid NULL",
            // document_parse_blocks: [Gen-2] minerU block-level structured columns
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS sub_type character varying(50) NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS text_level integer NOT NULL DEFAULT -1",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS text_format character varying(20) NOT NULL DEFAULT ''",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS bbox_x0 real NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS bbox_y0 real NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS bbox_x1 real NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS bbox_y1 real NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS score real NULL",
            "ALTER TABLE document_parse_blocks ADD COLUMN IF NOT EXISTS caption text NULL"
        };

        foreach (var sql in alterStatements)
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(sql);
            }
            catch (Exception ex)
            {
                // Column may already exist, log and continue
                logger.LogDebug(ex, "Column migration statement skipped: {Sql}", sql);
            }
        }

        // Create document_parse_blocks table for legacy DBs that predate this table
        await context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS document_parse_blocks (
                id uuid NOT NULL,
                parse_id uuid NOT NULL,
                page_id integer NOT NULL,
                sort_index integer NOT NULL,
                block_type character varying(20) NOT NULL,
                text_content text NULL,
                image_id uuid NULL,
                block_data jsonb NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT NOW(),
                CONSTRAINT PK_document_parse_blocks PRIMARY KEY (id),
                CONSTRAINT FK_blocks_parse_parse_id
                    FOREIGN KEY (parse_id) REFERENCES document_parses(id) ON DELETE CASCADE,
                CONSTRAINT FK_blocks_image_image_id
                    FOREIGN KEY (image_id) REFERENCES document_parse_images(id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_parse_id_page_id_sort_index
                ON document_parse_blocks (parse_id, page_id, sort_index);
            CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_block_type
                ON document_parse_blocks (block_type);
            CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_image_id
                ON document_parse_blocks (image_id);
        ");

        // Create trigger for auto-updating document_files.updated_at
        await context.Database.ExecuteSqlRawAsync(@"
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
        ");

        logger.LogInformation("Column migration check completed");
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "document_files" => @"
                CREATE TABLE IF NOT EXISTS document_files (
                    id uuid NOT NULL,
                    file_name character varying(500) NOT NULL,
                    file_path character varying(500) NULL,
                    structadoc_document_id uuid NULL,
                    content_type character varying(100) NOT NULL,
                    created_by uuid NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT NOW(),
                    updated_at timestamp with time zone NULL DEFAULT NOW(),
                    subject character varying(50) NULL,
                    grade character varying(20) NULL,
                    year character varying(10) NULL,
                    CONSTRAINT PK_document_files PRIMARY KEY (id)
                );
                CREATE INDEX IF NOT EXISTS IX_document_files_file_name ON document_files (file_name);",

            "document_parses" => @"
                CREATE TABLE IF NOT EXISTS document_parses (
                    id uuid NOT NULL,
                    document_file_id uuid NOT NULL,
                    model_version character varying(20) NOT NULL DEFAULT 'vlm',
                    status character varying(30) NOT NULL DEFAULT 'pending',
                    external_task_id character varying(100) NULL,
                    structadoc_parse_run_id uuid NULL,
                    markdown_content text NULL,
                    content_list jsonb NULL,
                    content_list_v2 jsonb NULL,
                    model_json jsonb NULL,
                    layout_json jsonb NULL,
                    zip_path character varying(500) NULL,
                    error_message text NULL,
                    parsed_at timestamp with time zone NULL,
                    CONSTRAINT PK_document_parses PRIMARY KEY (id),
                    CONSTRAINT FK_parses_file_document_file_id
                        FOREIGN KEY (document_file_id) REFERENCES document_files(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_document_parses_status ON document_parses (status);
                CREATE INDEX IF NOT EXISTS IX_document_parses_document_file_id ON document_parses (document_file_id);
                CREATE INDEX IF NOT EXISTS IX_document_parses_model_version ON document_parses (model_version);",

            "document_parse_images" => @"
                CREATE TABLE IF NOT EXISTS document_parse_images (
                    id uuid NOT NULL,
                    parse_id uuid NOT NULL,
                    image_name character varying(200) NOT NULL,
                    image_path character varying(500) NOT NULL,
                    content_type character varying(50) NOT NULL DEFAULT 'image/jpeg',
                    CONSTRAINT PK_document_parse_images PRIMARY KEY (id),
                    CONSTRAINT FK_images_parse_parse_id
                        FOREIGN KEY (parse_id) REFERENCES document_parses(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_document_parse_images_parse_id ON document_parse_images (parse_id);",

            "document_parse_blocks" => @"
                CREATE TABLE IF NOT EXISTS document_parse_blocks (
                    id uuid NOT NULL,
                    parse_id uuid NOT NULL,
                    page_id integer NOT NULL,
                    sort_index integer NOT NULL,
                    block_type character varying(20) NOT NULL,
                    text_content text NULL,
                    image_id uuid NULL,
                    block_data jsonb NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT NOW(),
                    CONSTRAINT PK_document_parse_blocks PRIMARY KEY (id),
                    CONSTRAINT FK_blocks_parse_parse_id
                        FOREIGN KEY (parse_id) REFERENCES document_parses(id) ON DELETE CASCADE,
                    CONSTRAINT FK_blocks_image_image_id
                        FOREIGN KEY (image_id) REFERENCES document_parse_images(id) ON DELETE SET NULL
                );
                CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_parse_id_page_id_sort_index
                    ON document_parse_blocks (parse_id, page_id, sort_index);
                CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_block_type
                    ON document_parse_blocks (block_type);
                CREATE INDEX IF NOT EXISTS IX_document_parse_blocks_image_id
                    ON document_parse_blocks (image_id);",

            _ => null
        };
    }
}

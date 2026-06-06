using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.DocRetrieval.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(DocRetrievalDbContext context, ILogger logger)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, logger, GetTableCreationSql);
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "documents" => @"
                CREATE TABLE IF NOT EXISTS documents (
                    id uuid NOT NULL,
                    title character varying(200) NOT NULL,
                    source_type character varying(20) NOT NULL,
                    file_hash character varying(64) NOT NULL,
                    file_path character varying(500) NOT NULL,
                    file_size bigint NOT NULL,
                    language character varying(10) NOT NULL DEFAULT 'en',
                    grade character varying(20) NOT NULL,
                    subject character varying(20) NOT NULL,
                    year character varying(10) NOT NULL,
                    tags text NULL,
                    status character varying(20) NOT NULL DEFAULT 'pending',
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NULL,
                    CONSTRAINT PK_documents PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_documents_title ON documents (title);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_documents_file_hash ON documents (file_hash);
                CREATE INDEX IF NOT EXISTS IX_documents_subject_grade_year ON documents (subject, grade, year);
                CREATE INDEX IF NOT EXISTS IX_documents_status ON documents (status);",

            "document_pages" => @"
                CREATE TABLE IF NOT EXISTS document_pages (
                    id uuid NOT NULL,
                    document_id uuid NOT NULL,
                    page_number integer NOT NULL,
                    image_path character varying(500) NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_document_pages PRIMARY KEY (id),
                    CONSTRAINT FK_document_pages_document_document_id
                        FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_document_pages_document_id_page_number ON document_pages (document_id, page_number);",

            "document_segments" => @"
                CREATE TABLE IF NOT EXISTS document_segments (
                    id uuid NOT NULL,
                    document_id uuid NOT NULL,
                    page_id uuid NOT NULL,
                    block_id character varying(50) NOT NULL,
                    sentence_id character varying(50) NOT NULL,
                    segment_type character varying(20) NOT NULL DEFAULT 'sentence',
                    text text NOT NULL,
                    start_offset integer NOT NULL,
                    end_offset integer NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_document_segments PRIMARY KEY (id),
                    CONSTRAINT FK_document_segments_document_document_id
                        FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE,
                    CONSTRAINT FK_document_segments_page_page_id
                        FOREIGN KEY (page_id) REFERENCES document_pages(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_document_segments_document_id_sentence_id ON document_segments (document_id, sentence_id);",

            "question_segments" => @"
                CREATE TABLE IF NOT EXISTS question_segments (
                    id uuid NOT NULL,
                    document_id uuid NOT NULL,
                    page_id uuid NOT NULL,
                    question_id character varying(50) NOT NULL,
                    stem text NOT NULL,
                    options_json text NULL,
                    answer_area text NULL,
                    start_offset integer NOT NULL,
                    end_offset integer NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_question_segments PRIMARY KEY (id),
                    CONSTRAINT FK_question_segments_document_document_id
                        FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE,
                    CONSTRAINT FK_question_segments_page_page_id
                        FOREIGN KEY (page_id) REFERENCES document_pages(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_question_segments_document_id_question_id ON question_segments (document_id, question_id);",

            "document_occurrences" => @"
                CREATE TABLE IF NOT EXISTS document_occurrences (
                    id uuid NOT NULL,
                    document_id uuid NOT NULL,
                    segment_id uuid NULL,
                    question_segment_id uuid NULL,
                    token_text character varying(200) NOT NULL,
                    token_stem character varying(200) NOT NULL,
                    start_offset integer NOT NULL,
                    end_offset integer NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_document_occurrences PRIMARY KEY (id),
                    CONSTRAINT FK_document_occurrences_document_document_id
                        FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE,
                    CONSTRAINT FK_document_occurrences_segment_segment_id
                        FOREIGN KEY (segment_id) REFERENCES document_segments(id) ON DELETE SET NULL,
                    CONSTRAINT FK_document_occurrences_question_question_segment_id
                        FOREIGN KEY (question_segment_id) REFERENCES question_segments(id) ON DELETE SET NULL
                );
                CREATE INDEX IF NOT EXISTS IX_document_occurrences_document_id_token_text ON document_occurrences (document_id, token_text);
                CREATE INDEX IF NOT EXISTS IX_document_occurrences_document_id_token_stem ON document_occurrences (document_id, token_stem);",

            "document_ingestion_jobs" => @"
                CREATE TABLE IF NOT EXISTS document_ingestion_jobs (
                    id uuid NOT NULL,
                    document_id uuid NOT NULL,
                    status character varying(20) NOT NULL DEFAULT 'pending',
                    parser_version character varying(20) NULL,
                    ocr_version character varying(20) NULL,
                    error_message text NULL,
                    started_at timestamp with time zone NULL,
                    finished_at timestamp with time zone NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_document_ingestion_jobs PRIMARY KEY (id),
                    CONSTRAINT FK_document_ingestion_jobs_document_document_id
                        FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_document_ingestion_jobs_status ON document_ingestion_jobs (status);",

            _ => null
        };
    }
}

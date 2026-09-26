# Migration History

## Migration Strategy

This project **does not use EF Core Code-First Migration**. Table structures are created with raw SQL by `DatabaseInitializer` at application startup (`CREATE TABLE IF NOT EXISTS`).

## Current Database Version

All tables are defined in the `GetTableCreationSql` method of [DatabaseInitializer.cs](../../src/Database/DatabaseInitializer.cs).

## Column-Level Migrations (EnsureColumnsAsync)

Executed automatically at startup via `DatabaseInitializer.EnsureColumnsAsync`, compatible with both new and existing databases:

| SQL | Description |
|-----|------|
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list jsonb NULL` | Add content_list (phase 2) |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS zip_path character varying(500) NULL` | Add zip_path (phase 2) |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_version character varying(20) NOT NULL DEFAULT 'vlm'` | Add model version column |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS content_list_v2 jsonb NULL` | Add MinerU pipeline output v2 |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS model_json jsonb NULL` | Add model inference results |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS layout_json jsonb NULL` | Add layout analysis data |
| `ALTER TABLE document_parses DROP COLUMN IF EXISTS layout_pdf_path` | Drop old column (replaced by layout_json) |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject character varying(50) NULL` | Add subject metadata column (DocumentMetadataAnalysis feature) |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade character varying(20) NULL` | Add grade metadata column (DocumentMetadataAnalysis feature) |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year character varying(10) NULL` | Add year metadata column (DocumentMetadataAnalysis feature) |
| `ALTER TABLE document_files ADD COLUMN IF NOT EXISTS structadoc_document_id uuid NULL` | StructaDoc migration (ADR-0009): remote reference for new documents |
| `ALTER TABLE document_files ALTER COLUMN file_path DROP NOT NULL` | StructaDoc migration: originals are no longer stored in local OSS; `file_path` is for legacy records only |
| `ALTER TABLE document_parses ADD COLUMN IF NOT EXISTS structadoc_parse_run_id uuid NULL` | StructaDoc migration: Parse Run reference; non-null means a new-pipeline record |

## Change Log

| Date | Change | Impact |
|------|------|------|
| 2026-07-04 | Added document_files.subject/grade/year columns | Document metadata analysis feature, supporting manual setting and LLM auto-fill |
| 2026-07-04 | Added document_parses.content_list_v2 / model_json / layout_json columns | Structured data output for MinerU pipeline/vlm modes |
| 2026-07-04 | Added document_parse_blocks and document_parse_images tables | OpenSearch block-level indexing and image management |
| 2026-07-04 | Dropped document_parses.layout_pdf_path column | Replaced by layout_json (JSONB stores the full layout data) |
| 2026-07-04 | Added document_parses.model_version column (default 'vlm') | Supports dual model versions vlm / pipeline |
| 2026-09-21 | Added document_files.structadoc_document_id and document_parses.structadoc_parse_run_id; file_path made nullable | StructaDoc parse pipeline migration (ADR-0009): primary ownership of originals and parse artifacts transferred to StructaDoc; legacy data kept read-only |

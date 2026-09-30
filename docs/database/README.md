# database — Database Documentation

> **This directory is the single source of truth for the Doctheca service database schema.** When other documents need to reference table structures, link to the corresponding files under `tables/`.

## Table Inventory

| Table | Description | Documentation |
|------|------|------|
| `document_files` | Document file table (parse pipeline) | [tables/document_files.md](tables/document_files.md) |
| `document_parses` | Parse record table (parse pipeline) | [tables/document_parses.md](tables/document_parses.md) |
| `document_parse_blocks` | Parse block table (parse pipeline) | [tables/document_parse_blocks.md](tables/document_parse_blocks.md) |
| `document_parse_images` | Parse image table (parse pipeline) | [tables/document_parse_images.md](tables/document_parse_images.md) |

The legacy `document_parse_imports` table has been removed from the runtime model; upgrades do not automatically drop existing tables. For compatibility notes see [tables/document_parse_imports.md](tables/document_parse_imports.md).

## Entity Relationships

See [relations.md](relations.md).

## Migration History

The schema is managed with EF Core migrations through [DocthecaMigrationExecutor](../../src/Database/DocthecaMigrationExecutor.cs): empty databases migrate from the `20260930161548_InitialCreate` baseline, verified legacy databases are taken over (safe backfills plus baseline registration), and unknown or conflicting structures fail startup with the fixed error code `DOCTHECA_DB_SCHEMA_INCOMPATIBLE`. See [migrations.md](migrations.md) for the full rules and upgrade notes.

## Database Configuration

- **Database name**: `doctheca`
- **Engine**: PostgreSQL
- **Connection string**: `ConnectionStrings:Default`

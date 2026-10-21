# database — Database Documentation

> **This directory is the single source of truth for the Doctheca service database schema.** When other documents need to reference table structures, link to the corresponding files under `tables/`.

## Table Inventory

| Table | Description | Documentation |
|------|------|------|
| `document_files` | Document file table (parse pipeline) | [tables/document_files.md](tables/document_files.md) |
| `document_parses` | Parse record table (parse pipeline) | [tables/document_parses.md](tables/document_parses.md) |
| `document_parse_blocks` | Parse block table (parse pipeline) | [tables/document_parse_blocks.md](tables/document_parse_blocks.md) |
| `document_parse_images` | Parse image table (parse pipeline) | [tables/document_parse_images.md](tables/document_parse_images.md) |
| `structure_definitions` | Runtime-configured structure definitions (document structure) | [tables/structure_definitions.md](tables/structure_definitions.md) |
| `structure_levels` | Configured levels of a structure definition (document structure) | [tables/structure_levels.md](tables/structure_levels.md) |
| `document_structure_nodes` | Hand-marked structure nodes of a parse (document structure) | [tables/document_structure_nodes.md](tables/document_structure_nodes.md) |

The legacy `document_parse_imports` table has been removed from the runtime model; upgrades do not automatically drop existing tables. For compatibility notes see [tables/document_parse_imports.md](tables/document_parse_imports.md).

## Entity Relationships

See [relations.md](relations.md).

## Migration History

The schema is managed with EF Core migrations through [DocthecaMigrationExecutor](../../src/Database/DocthecaMigrationExecutor.cs): empty databases migrate from the `20260930161548_InitialCreate` baseline, databases at an earlier known version receive the pending migrations (currently `20261021094612_AddDocumentStructure`), verified legacy databases are taken over (safe backfills plus baseline registration, then the later migrations), and unknown or conflicting structures fail startup with the fixed error code `DOCTHECA_DB_SCHEMA_INCOMPATIBLE`. See [migrations.md](migrations.md) for the full rules and upgrade notes.

## Database Configuration

- **Database name**: `doctheca`
- **Engine**: PostgreSQL
- **Connection string**: `ConnectionStrings:Default`

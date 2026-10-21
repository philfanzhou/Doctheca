# structure_definitions — Structure Definition Table

> This file is the single source of truth for the `structure_definitions` table (issue #89).

## Design Purpose

A **Structure Definition** is a runtime-configured kind of document structure, for example "English textbook" or "Paper". Administrators create and edit definitions in the admin console; the code knows no definition or level name. Its levels live in [`structure_levels`](structure_levels.md), and a document chooses one through `document_files.structure_definition_id`.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Definition identifier |
| `name` | `VARCHAR(200)` | NOT NULL | | Display name, for example "English textbook" |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Creation time (set by the service) |
| `updated_at` | `TIMESTAMP WITH TIME ZONE` | NOT NULL | | Last change of the name or the levels (set by the service) |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_structure_definitions` | `id` | Primary key |

## Foreign Keys

- Referenced by `structure_levels.definition_id` (ON DELETE CASCADE)
- Referenced by `document_files.structure_definition_id` (ON DELETE SET NULL)

## Rules

- A definition used by marked structure nodes (through any of its levels) cannot be deleted: the API answers `409 DOCTHECA_STRUCTURE_DEFINITION_IN_USE`.
- Deleting an unused definition deletes its levels and clears the choice of every document that selected it.

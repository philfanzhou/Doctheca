# structure_levels — Structure Level Table

> This file is the single source of truth for the `structure_levels` table (issue #89).

## Design Purpose

A **Structure Level** is one configured level of a [structure definition](structure_definitions.md), for example "Unit" or "Section". Levels form a hierarchy through `parent_level_id`; a level without a parent is a top level. Level names are data only: no level name appears in code, migrations, or table names.

## Field Inventory

| Field | Type | Constraints | Default | Description |
|--------|------|------|--------|------|
| `id` | `UUID` | PRIMARY KEY | | Level identifier (stable across definition edits) |
| `definition_id` | `UUID` | NOT NULL, FK → `structure_definitions(id)` ON DELETE CASCADE | | Owning definition |
| `name` | `VARCHAR(100)` | NOT NULL | | Display name, for example "Unit" |
| `parent_level_id` | `UUID` | NULL, FK → `structure_levels(id)` ON DELETE RESTRICT | | Parent level; NULL for a top level |
| `required` | `BOOLEAN` | NOT NULL | `false` | Recorded audio requirement policy; all ancestors of a required level must also be required |
| `sort_order` | `INT` | NOT NULL | | Display order within the definition (0-based) |

## Indexes

| Index | Columns | Description |
|--------|-----|------|
| `PK_structure_levels` | `id` | Primary key |
| `IX_structure_levels_definition_id` | `definition_id` | Levels of a definition |
| `IX_structure_levels_parent_level_id` | `parent_level_id` | Child levels |

## Rules (enforced by `StructureDefinitionService`)

- A level's parent belongs to the same definition; a level cannot be its own parent and parent cycles are rejected (400 with a field-level reason such as `levels[1].parentLevelId`).
- Every definition has at least one level and at least one top level.
- A level used by marked nodes may be renamed or reordered, but deleting it or changing its parent answers `409 DOCTHECA_STRUCTURE_LEVEL_IN_USE`.
- Referenced by `document_structure_nodes.level_id` (ON DELETE RESTRICT) as a last line of defense behind the 409 rule.

## Required-level policy (#129)

`20261024021500_AddRequiredStructureLevels` adds `required` with database default false, including
for all existing rows. The administrator API exposes it as `required`; it grants no permissions. Audio commits enforce the graph against every retained recording (#134), while policy edits themselves preserve metadata/bytes for review. Optional descendants and separate all-optional root branches are valid.
The service rejects a required level with an optional ancestor at `levels[i].required`, after
validating IDs, parents and cycles. A complete PUT that omits the boolean replaces it with false.
Definition replacements lock the definition row before reading its levels and commit atomically.
See [migration and recovery](../migrations.md#required-level-policy-issue-129) before upgrading or reverting.

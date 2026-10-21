-- Rollback of migration 20261021094612_AddDocumentStructure (issue #89).
--
-- Run only after stopping every instance of the new version and taking a backup. It deletes all
-- structure definitions, levels and marked structure nodes, and the global block order, then
-- removes the migration's history row so the previous image classifies the database as its own
-- current version again. Business rows of the four baseline tables are kept unchanged.
BEGIN;

ALTER TABLE document_files DROP CONSTRAINT "FK_document_files_structure_definitions_structure_definition_id";

DROP TABLE document_structure_nodes;
DROP TABLE structure_levels;
DROP TABLE structure_definitions;

DROP INDEX "IX_document_parse_blocks_parse_id_sequence";
DROP INDEX "IX_document_files_structure_definition_id";

ALTER TABLE document_parse_blocks DROP COLUMN sequence;
ALTER TABLE document_files DROP COLUMN structure_definition_id;

DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261021094612_AddDocumentStructure';

COMMIT;

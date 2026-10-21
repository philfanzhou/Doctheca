-- Rollback of migration 20261021153535_AddStructureAudio (issue #90).
--
-- Run only after stopping every instance of the new version and taking a backup. It deletes the
-- structure audio table (every imported audio file's link is lost) and removes the migration's
-- history row, so the previous image classifies the database as its own current version again.
-- Every other table is kept unchanged.
--
-- The stored audio objects are NOT deleted by this script. Before running it, either delete every
-- parse's audio in the admin console, or remove the objects under the "audio/" prefix of the Oss
-- bucket with the storage's own tools afterwards; the previous image never reads that prefix.
BEGIN;

DROP TABLE document_structure_audio;

DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261021153535_AddStructureAudio';

COMMIT;

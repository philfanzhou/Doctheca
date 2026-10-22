-- Optional destructive maintenance for 20261021153535_AddStructureAudio (#107 / #90).
-- Standard version recovery restores the verified pre-upgrade database/configuration/storage
-- backup; this script is NOT that restore and is never run automatically when images change.
-- Stop every instance and take a verified backup before deliberately discarding all audio rows.
-- This drops the audio table and its history row only; every other current row remains unchanged.
-- Stored objects are NOT deleted. Account for retained backups, then use storage tools to remove
-- only verified abandoned audio/ objects. No import/review console exists in this delivery.
BEGIN;

DROP TABLE document_structure_audio;

DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261021153535_AddStructureAudio';

COMMIT;

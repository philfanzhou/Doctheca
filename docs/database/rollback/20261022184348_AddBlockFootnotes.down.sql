-- Destructive annotation maintenance for #105 / #96; never run automatically.
-- Stop every instance and take a verified backup first. New footnotes are discarded;
-- caption, page labels, structure/audio metadata and every other value are retained.
-- Apply any later down scripts first. Rebuild/reindex with the previous image's mapping.
BEGIN;

ALTER TABLE document_parse_blocks DROP COLUMN footnote;

DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261022184348_AddBlockFootnotes';

COMMIT;

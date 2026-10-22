-- Stop mixed-version writers and verify a backup before this maintenance.
-- This collapses title -2 to -1; it cannot reconstruct malformed historical levels.
-- Reindex local blocks through the administrator repair path after starting the chosen image.
BEGIN;
UPDATE document_parse_blocks SET text_level = -1
WHERE block_type = 'title' AND text_level = -2;
DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261022194526_ClassifyLevelLessTitles';
COMMIT;

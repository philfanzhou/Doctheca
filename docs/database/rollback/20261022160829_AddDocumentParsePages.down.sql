-- Destructive page-label maintenance for #102 / #96; never run automatically.
-- Stop every instance and take a verified backup first. Only page labels and this history row
-- are discarded; all pre-existing business/structure/audio rows and stored objects are retained.
-- The previous image can verify its exact audio-version schema after this script completes.
BEGIN;

DROP TABLE document_parse_pages;

DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261022160829_AddDocumentParsePages';

COMMIT;

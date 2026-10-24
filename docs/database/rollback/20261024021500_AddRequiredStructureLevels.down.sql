-- Stop every application instance and take a verified backup before running.
-- Apply newer down scripts first. This permanently discards required-level policy values.
BEGIN;
ALTER TABLE public.structure_levels DROP COLUMN required;
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" = '20261024021500_AddRequiredStructureLevels';
COMMIT;

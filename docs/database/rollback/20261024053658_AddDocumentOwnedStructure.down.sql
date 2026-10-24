-- Stop every writer and verify a consistent backup plus matching image/configuration first.
-- Apply newer down scripts first. Nonrepresentable state is refused without any writes.
BEGIN;
DO $ownership_down$
BEGIN
    IF EXISTS (
        SELECT 1 FROM document_structure_trees t LEFT JOIN document_parses p ON p.id = t.origin_parse_id
        WHERE p.id IS NULL OR p.document_file_id <> t.document_file_id
           OR (NOT EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.tree_id = t.id)
               AND NOT EXISTS (SELECT 1 FROM document_structure_audio a WHERE a.tree_id = t.id)))
       OR EXISTS (SELECT 1 FROM document_structure_nodes n JOIN document_structure_trees t ON t.id = n.tree_id
                  WHERE n.parse_id IS DISTINCT FROM t.origin_parse_id OR n.start_sequence IS NULL OR n.end_sequence IS NULL)
       OR EXISTS (SELECT 1 FROM document_structure_audio a JOIN document_structure_trees t ON t.id = a.tree_id
                  WHERE a.parse_id IS DISTINCT FROM t.origin_parse_id)
       OR EXISTS (
           SELECT 1 FROM document_files f WHERE f.current_structure_tree_id IS DISTINCT FROM (
               SELECT t.id FROM document_structure_trees t JOIN document_parses p ON p.id = t.origin_parse_id
               WHERE t.document_file_id = f.id AND p.status = 'parsed'
                 AND EXISTS (SELECT 1 FROM document_structure_nodes n WHERE n.tree_id = t.id)
               ORDER BY p.parsed_at DESC NULLS LAST, p.id DESC LIMIT 1))
    THEN
        RAISE EXCEPTION 'Document ownership cannot be downgraded losslessly. Restore a verified backup with its matching image and configuration.';
    END IF;
END
$ownership_down$;
ALTER TABLE document_files DROP CONSTRAINT "FK_document_files_document_structure_trees_id_current_structur~";
ALTER TABLE document_structure_audio DROP CONSTRAINT "FK_document_structure_audio_document_structure_trees_document_~";
ALTER TABLE document_structure_nodes DROP CONSTRAINT "FK_document_structure_nodes_document_parses_document_file_id_p~";
ALTER TABLE document_structure_nodes DROP CONSTRAINT "FK_document_structure_nodes_document_structure_trees_document_~";
DROP TABLE document_structure_trees;
ALTER TABLE document_structure_nodes DROP CONSTRAINT "CK_document_structure_nodes_content_binding";
DROP INDEX "IX_document_structure_nodes_document_file_id_parse_id";
DROP INDEX "IX_document_structure_nodes_document_file_id_tree_id";
DROP INDEX "IX_document_structure_audio_document_file_id_tree_id";
DROP INDEX "IX_document_structure_audio_tree_id_source_path";
DROP INDEX "IX_document_files_id_current_structure_tree_id";
ALTER TABLE document_parses DROP CONSTRAINT "AK_document_parses_document_file_id_id";
ALTER TABLE document_structure_nodes DROP COLUMN document_file_id, DROP COLUMN tree_id,
    ALTER COLUMN parse_id SET NOT NULL, ALTER COLUMN start_sequence SET NOT NULL, ALTER COLUMN end_sequence SET NOT NULL;
ALTER TABLE document_structure_audio DROP COLUMN document_file_id, DROP COLUMN tree_id, ALTER COLUMN parse_id SET NOT NULL;
ALTER TABLE document_parses DROP COLUMN expected_binding_generation;
ALTER TABLE document_files DROP COLUMN current_structure_tree_id, DROP COLUMN structure_binding_generation;
CREATE UNIQUE INDEX "IX_document_structure_audio_parse_id_source_path" ON document_structure_audio (parse_id, source_path);
ALTER TABLE document_structure_audio ADD CONSTRAINT "FK_document_structure_audio_document_parses_parse_id"
    FOREIGN KEY (parse_id) REFERENCES document_parses(id) ON DELETE CASCADE;
ALTER TABLE document_structure_nodes ADD CONSTRAINT "FK_document_structure_nodes_document_parses_parse_id"
    FOREIGN KEY (parse_id) REFERENCES document_parses(id) ON DELETE CASCADE;
DELETE FROM public."__EFMigrationsHistory" WHERE "MigrationId" = '20261024053658_AddDocumentOwnedStructure';
COMMIT;

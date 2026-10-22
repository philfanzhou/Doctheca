# Entity Relationship Diagram

## Cascade Behavior

| Operation | Affected scope |
|------|---------|
| Delete document_file | CASCADE to parses, parse_blocks, parse_images |
| Delete document_parse | CASCADE to parse_blocks, parse_images |
| Delete document_parse_image | SET NULL on parse_blocks' image_id |
| Delete document_parse | CASCADE to document_structure_nodes |
| Delete document_structure_node | CASCADE to its child nodes |
| Delete structure_definition | Refused with 409 while marked nodes use it; otherwise CASCADE to structure_levels and SET NULL on document_files.structure_definition_id |
| Delete structure_level | Refused with 409 while marked nodes use it (FK RESTRICT behind the service rule) |

## Document Structure (issue #89)

```
structure_definitions 1 ──< structure_levels (parent_level_id → structure_levels)
        │                          │
        │ 0..1                     │ level_id
document_files ──< document_parses ──< document_structure_nodes (parent_id → document_structure_nodes)
                          │                      ╎ start_sequence..end_sequence
                          └──< document_parse_blocks (sequence)
```

A node's range is a range of `document_parse_blocks.sequence` values of the same parse; it is not a foreign key.

# Entity Relationship Diagram

## Cascade Behavior

| Operation | Affected scope |
|------|---------|
| Delete document_file | CASCADE to parses, parse_blocks, parse_images |
| Delete document_parse | CASCADE to parse_blocks, parse_images |
| Delete document_parse_image | SET NULL on parse_blocks' image_id |

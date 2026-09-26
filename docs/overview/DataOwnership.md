# DataOwnership — Data Ownership and Reference Boundaries

## Data Entities Owned by This Service (Primary Ownership)

The following data is managed independently by the Doctheca service; other services must not write to it directly:

| Entity table | Description | Operation permissions |
|--------|------|---------|
| `document_files` | Document file table | Written exclusively by this service |
| `document_parses` | Parse record table | Written exclusively by this service |
| `document_parse_blocks` | Parse block table | Written exclusively by this service |
| `document_parse_images` | Parse image table | Written exclusively by this service |

## External Data Referenced by This Service (Read-Only References)

| External data | Source | Read method | Description |
|---------|------|---------|------|
| Identity JWT | SignaCore | OIDC/JWKS + HTTP token API | Only consumes administrator identity; does not persist Identity accounts |
| Document originals and parse artifacts | StructaDoc | Versioned API (ApiKey authentication) | Originals, Markdown, images, and ZIPs are stored under StructaDoc's primary ownership; Doctheca only keeps documentId/parseRunId references and local blocks/images synchronized copies (ADR-0009) |

## External System Dependencies

| System | Write | Read | Description |
|------|------|------|------|
| PostgreSQL | CRUD on all tables | Full-text search, document queries | Self-hosted database |
| StructaDoc | Upload documents, create/cancel/delete Parse Runs | Blocks/Markdown/Assets/image content | External parsing service with its own storage |
| MinIO / SeaweedFS | Delete cleanup only | Reading legacy objects | Files and parse images from before the migration (read-only compatibility) |
| OpenSearch | Create/delete indexes | Search queries | External retrieval engine |

## Dual-Write Forbidden Zones

- This service does not write to the databases of any other Ruoyu microservice, nor does it connect directly to StructaDoc's database or object storage
- Other services should not write directly to this service's 4 business tables
- Quaestura's import status and question IDs are persisted by Quaestura itself; Doctheca keeps no copies
- The legacy OSS file path format is controlled internally by this service; other services should not manipulate it directly
- StructaDoc's internal storageRef and resource lifecycle are shielded behind its API; Doctheca does not persist its storage details

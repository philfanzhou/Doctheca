# Entity Relationships

| Operation | Result |
| --- | --- |
| Delete document | Clear current pointer and bindings under document lock; cascade parses/content and all owned trees/chapters/audio; clean exact unreferenced owned keys after commit |
| Delete parse | Clear affected binding triples; delete parse-owned blocks/images/pages; retain trees/chapters/audio and immutable origin provenance |
| Successful parse completion | Atomically replace local pages/images/blocks and publish Parsed; conditionally detach current bindings by captured generation |
| Failed/cancelled parse | Preserve committed tree, bindings and audio |
| Explicit tree replacement | Keep node IDs; only removed IDs unlink audio for review |
| Delete definition/level | Refuse while marked nodes reference its levels; otherwise existing cascade/restrict rules apply |
| Delete parse image | Clear matching block image references |

```
document_files ──< document_structure_trees ──< document_structure_nodes
      │     current_structure_tree_id              ╎ node_id (application-maintained)
      │                         └──────────< document_structure_audio
      └──< document_parses ──< blocks / images / pages
                   ╎ optional same-document parse + inclusive sequence pair on nodes
```

Composite ownership FKs prevent cross-document tree pointers, node bindings and audio owners. Node-to-audio linkage has no simple FK because complete replacement deletes/reinserts kept IDs; the transaction validates membership and unlinks only removed IDs. `origin_parse_id` on trees and `parse_id` on audio are immutable provenance, not live-parse lifetime FKs. Exact legacy keys and object bytes are retained; no migration calls storage or indexing.

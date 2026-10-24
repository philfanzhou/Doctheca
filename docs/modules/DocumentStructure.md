# DocumentStructure — Hand-Marked Document Structure

> Issue #89. StructaDoc parses generically (pages, page labels, table HTML, captions); Doctheca owns the business structure of a document. Structure levels are runtime-configured data and nodes are marked by hand on a continuous view of the parse — there is no LLM or rule-based detection, and no level name appears in code, migrations, or table names.

## Capability Overview

- **Structure Definitions** (for example "English textbook" with the levels Unit → Section, or "Paper" with Chapter → Section → Subsection) are created and edited in the admin console after the system starts. See [structure_definitions](../database/tables/structure_definitions.md) and [structure_levels](../database/tables/structure_levels.md).
- A **Document File** chooses one definition (`document_files.structure_definition_id`) through the existing metadata update.
- A **Structure Node** belongs to a document-owned tree and may bind an inclusive range of block `sequence` values of one same-document parse at one configured level; children lie inside their parent and siblings do not overlap. See [document_structure_nodes](../database/tables/document_structure_nodes.md).
- Each block's node and its ancestors (`structure_node_ids`) and the readable path (`structure_path`, for example "Unit 3 > Section B") are indexed in OpenSearch, so search can be narrowed to a node and its whole subtree.
- Out of scope: automatic detection, automatic content binding, audio (#90, see [StructureAudio](StructureAudio.md)), the internal read API (#91), and StructaDoc changes.

## Global Block Order

`document_parse_blocks.sort_index` restarts on every page, so ranges use the global `sequence` column: written from StructaDoc's `sequence` by `StructaDocParseResultSync`, backfilled by migration `20261021094612_AddDocumentStructure` (StructaDoc rows keep their `block_data.sequence`; older rows are numbered by `(page_id, sort_index)`), unique per parse.

## Endpoints

All routes require the `DocthecaAdmin` policy (401 without a session, 403 without `role=admin`), answer the unified JSON envelope, and carry the ServiceMantle security response headers. Validation failures answer `400` with `errorCode: DOCTHECA_STRUCTURE_INVALID` and `errors: [{ field, reason }]`, where `field` is a JSON path such as `nodes[0].children[1].endSequence`.

### Structure definitions

| Method | Path | Result |
|------|------|------|
| GET | `/admin/structure-definitions` | All definitions with their levels |
| GET | `/admin/structure-definitions/{id}` | One definition; 404 `DOCTHECA_STRUCTURE_DEFINITION_NOT_FOUND` |
| POST | `/admin/structure-definitions` | 201 with the created definition |
| PUT | `/admin/structure-definitions/{id}` | Replaces the name and the level list; 404 when missing |
| DELETE | `/admin/structure-definitions/{id}` | 409 `DOCTHECA_STRUCTURE_DEFINITION_IN_USE` while marked nodes use it; otherwise deletes it and clears documents that chose it |

Request body of POST and PUT (levels in display order; ids are optional for new levels, but a level referenced as a parent must carry an id in the same body; the console generates ids):

```json
{
  "name": "English textbook",
  "levels": [
    { "id": "8f1c…", "name": "Unit", "parentLevelId": null, "required": true },
    { "id": "2b7e…", "name": "Section", "parentLevelId": "8f1c…", "required": false }
  ]
}
```

Rules: name required (≤ 200), 1–50 levels, level names required (≤ 100), a parent must be a level of the same body, no self-parent or cycle, at least one top level, a level id owned by another definition is rejected. A level used by marked nodes may be renamed or reordered; removing it or changing its parent answers 409 `DOCTHECA_STRUCTURE_LEVEL_IN_USE`.

The additive `required` boolean appears on every administrator definition level, including list,
GET, POST/PUT results and the definition embedded in an administrator parse tree. Omission defaults
to false; null and non-boolean values are invalid. POST and PUT replace the complete intended
definition: an older PUT caller omitting `required` resets previously true flags to false.

For each required level, **every ancestor must also be required**. A required prefix followed by
optional descendants and an independent all-optional root branch are valid. ID ownership/duplicates,
parent membership, self-parent and cycle validation finish before policy traversal. Violations return
`400 DOCTHECA_STRUCTURE_INVALID` with a field error at each offending `levels[i].required`; no
automatic repair or write occurs. Replacements lock the definition row inside the transaction before
reading tracked levels; concurrent complete saves serialize and the later complete request wins.
A pre-commit failure rolls back all name, hierarchy and flag changes. Existing definition APIs do
not accept cancellation tokens; disconnecting is not a guarantee that a valid save is undone.

This is recorded policy only: audio import/review, unlinked audio, playback, nodes, storage objects
and search remain usable independently of flags. Policy changes are allowed even with existing
nodes/audio. The internal v1 API does not expose `required`. Administrator/CSRF boundaries remain
unchanged; callers must await the later #128 enforcement work before relying on audio validation.
See [upgrade and recovery](../database/migrations.md#required-level-policy-issue-129).

### Choosing a document's definition

`PUT /admin/document-files/{id}/metadata` gains the optional field `structureDefinitionId` (the existing metadata update, extended — no new route): a definition id sets it, `""` clears it, a missing/null field leaves it unchanged. An unknown id answers 400; changing the definition while any parse of the document has marked nodes answers 409 `DOCTHECA_DOCUMENT_STRUCTURE_IN_USE` (clear the tree first). The definition is validated before `subject`/`grade`/`year` are applied, so a rejected request changes nothing. The response and `GET /admin/document-files/{id}` gain `structureDefinitionId`.

### Ordered blocks

`GET /admin/document-parses/{parseId}/blocks?afterSequence=&limit=` (limit 1–1000, default 200) returns one page in `sequence` order:

```json
{
  "success": true,
  "data": {
    "items": [
      { "id": "…", "sequence": 0, "pageNumber": 1, "pageLabel": "iv", "blockType": "title", "subType": "heading-1",
        "content": "Unit 1 The Changing World", "contentFormat": "plain", "imageId": null, "imageUrl": null }
    ],
    "nextAfterSequence": 199,
    "hasMore": true
  }
}
```

`pageNumber` is 1-based (`page_id + 1`). `imageUrl` is the image proxy URL for StructaDoc-backed parses (a presigned OSS URL for legacy parses). Pass `nextAfterSequence` as `afterSequence` until `hasMore` is false; pages never overlap or skip a block. 404 `DOCTHECA_PARSE_NOT_FOUND` for an unknown parse.

Both this route and the node-block route below add nullable `pageLabel`, read from the locally
synchronized [printed page data](../database/tables/document_parse_pages.md). Labels retain
their exact Unicode, repeated or empty text and are untrusted display text, never page identities.
An unlabeled/missing page, an older parse, or a block with unknown upstream page provenance
returns null. In particular, an unknown page stored with the legacy `page_id=0` fallback cannot
borrow physical page 1's label. Physical numbering, existing fields, bounds and the console's
physical-page display remain unchanged.

Only the returned blocks' trusted parse/page pairs are read, once per response after removing
the cursor's extra row. Each batch sees one committed page set without mixing parses; reads
never write page data or the search index and make no StructaDoc request. If that optional lookup
fails, every label in the response is null and one fixed, content-free Warning is emitted.
Request cancellation propagates to the page lookup and stops response publication instead of
becoming a successful fallback. Existing block/image error behavior is unchanged. No schema,
configuration or index migration is required; restoring the previous image removes the optional
field and leaves stored labels intact.

### Structure tree

- `GET /admin/document-parses/{parseId}/structure` returns `{ parseId, documentFileId, fileName, definition, nodes }`, where `definition` is the document's definition with its levels (or null) and `nodes` is the nested tree; each node carries `id`, `levelId`, `levelName`, `name`, `startSequence`, `endSequence`, `sortOrder`, `children`.
- `PUT /admin/document-parses/{parseId}/structure` replaces the whole tree. Body: `{ "nodes": [ { "id"?, "levelId", "name", "startSequence", "endSequence", "children": [ … ] } ] }`. Node ids are kept when sent, so a node keeps its id across saves; nodes without an id get a new one. The response is the saved tree plus `searchIndexUpdated` (false when the OpenSearch write failed — the save itself still succeeded).
- `GET /admin/document-parses/{parseId}/structure/nodes/{nodeId}/blocks?afterSequence=&limit=` pages through exactly the blocks between the node's start and end sequence; 404 `DOCTHECA_STRUCTURE_NODE_NOT_FOUND` for a node of another parse.

**Validation on save** (a failed save changes nothing): every start/end sequence exists in the parse; start ≤ end; a root node uses a top level and a child node a level whose parent is its parent node's level; every level belongs to the document's definition (a document without a definition accepts only an empty tree); children lie inside their parent; siblings do not overlap (siblings are stored ordered by `start_sequence`); node names are required (≤ 500); ids are unique and not owned by another tree; at most 5000 nodes.

**Concurrency:** the replacement runs in one transaction that locks the document row first, then validates committed ownership/levels/ranges, so two saves of the same tree serialize and **the last complete save wins**; a mixed tree cannot be stored.

### Search

- Index mapping version 3 adds `structure_node_ids` (keyword, the block's deepest node and all ancestors) and `structure_path` (keyword, node names joined by ` > `). A tree save re-indexes that parse (best-effort: a failure logs a warning and is reported as `searchIndexUpdated: false`).
- `GET /admin/documents/search` gains the optional `structureNodeId` filter (`term` on `structure_node_ids`): a child node returns only hits inside it, a parent returns hits from all its descendants. Every hit gains `structurePath` (null outside every node). Without the filter, the query is unchanged.
- Repair: `POST /admin/document-parses/{parseId}/search-index` re-indexes one parse (502 `DOCTHECA_SEARCH_INDEX_FAILED` when OpenSearch rejects it); `POST /admin/search-index/reindex` re-indexes every parsed parse and returns `{ total, indexed, failed }` — run it once after the mapping step recreates the index.

## Admin Console

- **结构定义** (sidebar, `#structures`): list, create, edit and delete definitions; each level has a name, optional parent and default-false 音频必需 checkbox, can be reordered, and the hierarchy is previewed. Saved required levels carry a visible policy indicator; errors appear beside the level. The editor explains that audio enforcement is not active yet.
- **Document detail** (`#detail/:fileId`): the file info card has a 结构定义 select (clearing it removes the choice).
- **结构标注** (button on a parsed parse record, `#structure/:parseId`): all blocks of the parse in one continuous view (headings, text, tables, images, with sequence and page). Select a range by clicking a start block and shift-clicking the end block, or by dragging; the selection bar offers only the levels allowed at that position (the child levels of the deepest node containing the range, or the top levels) and pre-fills the name from the first block. The tree panel edits names, selects or replaces a node's range, deletes nodes, and scrolls to each range. 保存 sends the whole tree; 重新加载 discards local edits; 重建索引 re-indexes the parse.
- **Untrusted content:** table HTML from parse results is never inserted into the page. It is parsed into an inert `DOMParser` document (no scripts run, no resources load), `script`/`style`/`template`/`noscript` elements are dropped, and only cell text (with row/column spans) is rendered by Vue as escaped text. No sanitizer dependency was added.

## Validation

- Unit: `Structure/DocumentStructureServiceTests` (every invalid save, stable ids, definition switching), `Structure/StructureDefinitionServiceTests`, `Structure/StructurePathResolverTests`, `OpenSearch/*` (filter, mapping v3, `structurePath`), `Parsing/StructaDocParseResultSyncTests` (sequence written).
- PostgreSQL: `Database/StructureMigrationTests` (populated upgrade, rollback script, refused histories), `Integration/DocumentStructureEndpointTests` (end-to-end API, 401/403/404/409, concurrent saves), `Integration/DocumentStructurePageLabelTests` (both block views, exact labels and old fields, cursor bounds, legacy/missing data, optional lookup failure, cancellation and concurrent page replacement).
- OpenSearch: `OpenSearch/OpenSearchStructureSearchTests` against a real `opensearchproject/opensearch:2.19.1` container.
- Browser: `Integration/AdminHostedFrontendTests.StructureIsConfiguredMarkedSavedAndReloadedInTheConsole` (Playwright) and `frontend/tests/structure-tree.mjs`.

Required-policy acceptance is exercised by `RequiredPolicyMigrationTests` (populated/fresh schema,
refusal, failure/cancellation, concurrent startup and both down paths), `RequiredPolicyRepositoryTests`
(controlled real PostgreSQL replacement locking/rollback), and the policy cases in
`DocumentStructureEndpointTests` and `AdminHostedFrontendTests`.

## Ownership and reparse (#132)

Migration [20261024053658](../database/migrations.md#document-owned-structure-132) preserves every legacy tree separately and initializes one current pointer. Chapters, their order and audio links survive successful reparse and parse deletion. Successful parse completion detaches only current content bindings when its captured document generation is still current; a late/repeated completion cannot erase a newer explicit binding. Upstream fetch, best-effort indexing and metadata analysis run outside that atomic local transition. Historical parse routes resolve preserved origin trees; a new parse's explicit tree save binds the existing current tree, without replacing its identity.

When content is absent, `parseId`, sequence bounds and page bounds are nullable. Existing bound field names/routes stay. Internal content returns an empty terminal page; images and node-filtered search cannot return a former binding's content. The existing editor displays unbound ranges without highlighting unrelated blocks. New no-file book creation and unbound authoring/import interfaces remain separate work under #128.

# Structure Audio Import, Review and Playback

Administrators can select recordings for a logical book, review suggested chapter targets, edit existing links/removals, and commit the complete draft with one Save (#134). A source-free book needs no parse, content binding or source file. Open `#document-audio/:documentFileId` from book details/current chapters; the compatible `#audio/:parseId` entry keeps its real parse identity. Administrators can preview imported audio and list a node’s direct audio through the authenticated APIs below (#109). Downstream current-node audio uses expiring same-origin proxy links (#101), described below.

## Authority and transactions

PostgreSQL [document_structure_audio](../database/tables/document_structure_audio.md) owns metadata,
the current node link and the internal object key. `document_structure_nodes` owns the tree. S3 owns
the bytes under `audio/documents/{documentFileId:N}/{audioId:N}{extension}` for new document imports, or the unchanged legacy `audio/{parseId:N}/{audioId:N}{extension}` for parse imports. The parse prefix is immutable provenance, not a live parse dependency.

Audio commits and whole-tree replacement lock the document row first. Audio commits then take a shared lock on the definition row before reading the current tree, configured levels and **all retained audio**, including unchanged rows. Definition replacement takes the definition row exclusively and never then acquires a document lock. No audio commit advances content-binding generation.

The commit applies additions, explicit link changes and removals in memory and validates that final set before changing metadata. With no required levels, valid links and unlinked files are allowed. Otherwise every retained file must be linked; a target above any required descendant on its selected definition branch is rejected, even if that descendant has no current node. Names/depth are not policy. Policy/tree edits retain bytes and metadata; GET derives `requiresReview`/`reviewReason`. A later import/review Save must fix or remove every violation. Explicit single-file DELETE is a recovery exception and can remove one file while other violations remain.

Whole-tree saves unlink only removed nodes and preserve kept IDs/audio. Successful reparse clears old content bindings, while source attachment/parse deletion preserve chapter and recording identities, links and bytes. Unknown/foreign explicit nodes are rejected; a stale suggestion is never silently persisted to a different chapter. Historical trees remain isolated and cannot receive new imports or be promoted through a parse adapter.

## Storage boundary and configuration

`StructureAudioStore` alone writes/deletes `audio/` through the existing `IOssService`/`S3OssService`.
It refuses non-audio keys. Legacy upload/image prefixes remain read-only except for existing cleanup.
Audio storage is available when the internal S3 endpoint,
access/secret keys and bucket are complete; local-file storage is unsupported. A public storage URL is not required for import or cleanup. Administrator playback additionally requires an explicitly valid `Oss:PublicBaseUrl` and `DocumentAudio:PlaybackLinkSeconds` (default 300, allowed 60–3600); missing/invalid public addressing disables only administrator playback, which never falls back to the internal endpoint. These settings are acquired at startup; restart to apply changes. Invalid numeric settings refuse startup before database initialization or HTTP listening, even without S3. Missing S3 settings disable recovery without
constructing a client; existing document/structure reads remain usable, and legacy storage operations
keep their existing S3 configuration requirements. See
[deployment](../development/Deployment.md#structure-audio-storage-issue-90).

## Deletion and recovery

| Event | Database | Object storage |
| --- | --- | --- |
| A node disappears during tree replacement | Keep metadata, clear its link, mark `node_removed` | Keep bytes |
| Parse deletion | Clear content bindings; retain chapters/audio metadata | Keep all referenced bytes, including legacy origin prefixes |
| Document deletion | Atomically gather exact keys across every owned historical/current tree, then delete metadata | After commit, lease each exact key and recheck all references before cleanup |
| Object cleanup fails | The successful database deletion stays committed | Safe Warning records failure counts/types; recover with orphan sweep |

`POST /admin/structure-audio/orphan-sweep` performs administrator recovery. It requires `DocthecaAdmin`, uses
the existing security headers and returns `{ success: true, data: { scanned, deleted, failed } }`.
Missing identity returns 401, a non-admin returns 403, and unavailable storage returns a
fixed safe 503 (`DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED` / `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE`).
Audio cleanup uses complete-or-fail S3 listing: a rejected or interrupted listing returns the
unavailable error, never a successful zero-object count. Existing legacy listing callers retain
their best-effort behavior. Provider messages and storage locations are discarded at the audio boundary.
It never returns storage locations. It removes only unreferenced `audio/` objects older than one
hour, preserving recent writes, referenced objects and all legacy prefixes. Database and S3 do
not share a transaction: a successful deletion during an outage can leave objects for later recovery.

## Upgrade and previous-version recovery

Migration `20261021153535_AddStructureAudio` adds one table and indexes without altering existing
rows or the search mapping. Before upgrading, take and verify a consistent PostgreSQL backup with
the matching image/configuration and storage recovery set. Stop all instances and restore that set
to return to the previous version; post-backup writes are lost. An old image need not understand
the new schema. Image rollback never automatically executes Down migrations. The supplied down SQL
is a separate destructive maintenance option that loses all audio metadata and needs explicit object
cleanup. See [migration procedure](../database/migrations.md#structure-audio-upgrade-issue-90).

## Import limits and validation

| Private configuration key | Default | Validation |
| --- | --- | --- |
| `DocumentAudio:MaxFileBytes` | `20971520` (20 MiB) | Positive; each audio upload or ZIP entry |
| `DocumentAudio:PlaybackLinkSeconds` | `300` | Whole seconds, 60–3600; invalid/malformed values refuse startup |
| `DocumentAudio:MaxArchiveBytes` | `209715200` (200 MiB) | At least MaxFileBytes, and no greater than Int64.MaxValue minus 1048576 |

Environment variables replace `:` with `__`. The request's uploaded file bytes and the actual expanded total across all loose files and ZIPs must each fit MaxArchiveBytes. Before buffering, the whole multipart body is capped at MaxArchiveBytes plus 1 MiB for boundaries, paths and headers, including requests without Content-Length. There are at most 1000 audio files and 4000 ZIP entries per request. Oversized request or expanded total returns 413; a per-file excess returns 400. Absent numeric keys use defaults; empty, non-integer, overflowing or invalid limits/TTL refuse host startup with key-name-only diagnostics. The snapshot is read after Consul loading and requires a restart to change.

Supported extensions/signatures: `.mp3` (ID3 or MPEG frame sync), `.m4a` (ftyp), `.wav` (RIFF/WAVE), `.ogg` (OggS), `.flac` (fLaC). This is format screening, not media decoding, malware detection, transcoding or permission to distribute the content. Callers are responsible for authorized content. ZIPs are streamed without extracting files to disk. Nested/broken archives, unsupported extensions/signatures, unsafe paths and duplicates reject the entire batch. `.DS_Store`, `__MACOSX/`, `._*`, `Thumbs.db`, `desktop.ini` and directory entries are skipped.

Paths use NFC Unicode and `/` separators, at most 1000 characters and 255 per segment. Absolute/drive paths, control characters, invalid Unicode, empty/whitespace segments and `.`/`..` segments are refused. Path equality is ordinal and case-sensitive, matching the existing database unique index. A multipart `paths` value for each file preserves browser folder names; otherwise multipart filenames are used.

The bounded reader validates file formats/paths before storage. Preview performs no row/object/index/source writes and reserves nothing. Save re-reads the same files, uploads outside document locks under sorted exact-key leases on the metadata connection, then revalidates targets and the complete final set in one document/definition-locked transaction. Missing owners/removed targets or changed Required policy reject the batch; the unique tree/path index rejects concurrent duplicates. Reading, uploading and pre-commit boundaries observe request cancellation. Failed or cancelled imports compensate only objects confirmed unreferenced. Unknown commit/reference outcomes retain recoverable objects for the grace sweep; committed rows/bytes survive a cancelled response and are visible on reload. No cancelled operation is reported as successful.

## Matching

Folder and filename are compared separately against marked node names at every tree level. Case, spaces, punctuation and leading zeroes are ignored; labels match at token boundaries. No level name is hard-coded. The deepest agreed node is suggested; sibling ambiguity or folder/name conflicts stay unlinked. A child without its ancestor is not independently matched.

The 58 PEP Grade 9 fixture paths yield 49 Section suggestions, 8 Unit vocabulary suggestions and exactly one `unit3/9s04Unit4SectionA1e.mp3` conflict. `unit1/SectionB.mp3` links to B; `unit1/sectiona/SectionB.mp3`, `unit1/sectiona/Unit1SectionB.mp3` and `sectiona/Unit1SectionB.mp3` stay unlinked as `folder_name_mismatch`. Isolated `SectionB.mp3` is `no_match`.

## Administrator API

All routes require `DocthecaAdmin`, existing Cookie CSRF protection for writes and security response headers. Anonymous/non-admin calls return 401/403. Existing routes, authorization and envelopes remain unchanged.

| Method and path | Body / behavior |
| --- | --- |
| `GET /admin/document-files/{documentFileId}/audio` | Current-tree safe metadata, `documentFileId`, nullable `parseId`, `treeId`, storage availability/limits and items |
| `POST /admin/document-files/{documentFileId}/audio?preview=true` | Bounded read-only multipart proposal: `items` contain sourcePath/contentType/sizeBytes/matchStatus/suggestedNodeId/suggestedNodePath, plus skipped paths; no durable staging |
| `POST /admin/document-files/{documentFileId}/audio` | Multipart `files`, optional one `paths` per upload, optional `targetNodeId`, `targets`, `links`, `removeIds`; one mixed metadata commit |
| `PUT /admin/document-files/{documentFileId}/audio` | `{ "links": [{ "id": "UUID", "nodeId": null }], "removeIds": ["UUID"] }`; shared complete-set policy validation |
| `DELETE /admin/document-files/{documentFileId}/audio/{audioId}` | Explicit single-file removal, including recovery from invalid retained policy |
| `GET /admin/document-files/{documentFileId}/structure/nodes/{nodeId}/audio` | Direct current-node audio only |
| `GET /admin/document-files/{documentFileId}/audio/{audioId}/playback-link` | Existing temporary administrator signing; no-store |
| The corresponding `/admin/document-parses/{parseId}/audio` GET/POST/PUT/DELETE and node/playback paths | Existing parse identity and historical reads; same limits, preview/explicit targets and Required enforcement; new historical-only imports return structure-required 409 |
| `POST /admin/structure-audio/orphan-sweep` | Existing recovery described above |

`targets` is a JSON array `[{ "sourcePath": "unit1/track.wav", "nodeId": "UUID" }]`. Normalize each expanded path exactly as the reader does; duplicate/unknown paths reject the batch. An explicit per-path null overrides a common target and clears the selection. Omitted targets mean unlinked, never automatic confirmation of filename matching. `links` and `removeIds` are JSON arrays with the same meaning as PUT. A single Save can replace a removed path with new bytes atomically. Each selection form field appears at most once. Preview does not assert policy validity or reserve paths; all commit validation runs again after upload.

Responses use `{ "success": true, "data": ... }`; errors use `{ "success": false, "message": "...", "errorCode": "..." }` and optional `{file,reason}` errors. The metadata allow-list is `id`, `sourcePath`, `folder`, `fileName`, `contentType`, `sizeBytes`, `nodeId`, `nodePath`, `matchStatus`, `linkSource`, `createdAt`, `updatedAt`, `requiresReview`, `reviewReason`. The parse-audio metadata GET retains `parseId`, `storage` and `items`, with additive `documentFileId`/`treeId`. POST adds `imported`, `linked`, `flagged`, `skipped`; PUT returns `parseId`/`items`, and DELETE returns `id`/`deleted`. Storage contains only `available`, key-name-only `reason`, `maxFileBytes`, `maxArchiveBytes`, `maxFiles`, `maxArchiveEntries`, `maxPathLength`, `maxSegmentLength`, `multipartOverheadBytes`, and `extensions`. No object key, bucket, internal/public endpoint or storage credential appears in these responses or logs.

| Failure | HTTP / error code |
| --- | --- |
| Unknown document, parse, audio or node (including foreign/historical targets) | 404 / `DOCTHECA_FILE_NOT_FOUND`, `DOCTHECA_PARSE_NOT_FOUND`, `DOCTHECA_AUDIO_NOT_FOUND`, `DOCTHECA_STRUCTURE_NODE_NOT_FOUND` |
| Malformed/unsupported batch | 400 / `DOCTHECA_AUDIO_IMPORT_INVALID` |
| Invalid targets or complete-set Required policy, including duplicate link IDs; file policy errors list exact paths | 400 / `DOCTHECA_AUDIO_INVALID` |
| Request or expanded total over limit | 413 / `DOCTHECA_AUDIO_IMPORT_TOO_LARGE` |
| Existing source path / no structure nodes | 409 / `DOCTHECA_AUDIO_ALREADY_IMPORTED`, `DOCTHECA_AUDIO_STRUCTURE_REQUIRED` |
| Unconfigured or unavailable storage | 503 / `DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED`, `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE` |

Missing owner, unavailable storage or missing/current-empty/historical-only structure is refused before reading the multipart body. Removal commits before best-effort object deletion, with sweep recovery for cleanup outages. The last complete valid save wins; invalid review batches change nothing. This adds no database schema or dependency. The foundation upgrade/backup recovery procedure above still applies; rollback may lose post-backup work and does not promise old-image/new-schema compatibility.

## Console workflow

Choose a folder, multiple files or ZIPs. File selection invokes only read-only preview and keeps files/proposals in memory. Check suggested targets in the same table, optionally set one common target and override individual rows, then press Save once. Existing link edits/removals and new files commit together; no import-then-fix step or extra approval dialog is used. Cancel selected files or leave/discard to store nothing. Failures retain the editable draft and per-file policy errors. Reload displays only committed rows. Configured level names and Required flags appear as data. Reload, sidebar/back/hash navigation and logout require discard confirmation for a dirty draft; tab close uses the browser's unload confirmation. In-flight import/save prevents navigation and duplicate writes. Saving also disables node selection/clearing and removal/undo from the deletion confirmation until the request finishes; cancelling confirmation or a failed request keeps the draft editable. Paths/node names render as escaped text. Drafts stay in memory; neither payloads nor credentials are persisted in browser storage. Authentication failures never replay a write automatically. Each play click obtains a fresh link, including an unlinked imported file. The player handles missing files, expired links, storage/network and decoding failures with fixed messages. Switching, closing, reloading the list, saving, or leaving releases the media resource (pause, remove src, load); pending link requests are aborted and late responses ignored. URLs stay in component memory/DOM only. Text/exercise alignment and transcripts remain excluded from the administrator UI.

## Temporary administrator playback

The [Lexarbor model](https://github.com/philfanzhou/Lexarbor/blob/d91451c8094215d783b76497f44e76515d669d4e/docs/api/Audio.md#administrator-playback) uses an S3 presigned GET URL. Only `url` may contain the configured public host, bucket/object path, access-key identifier and temporary signature. There are no separate storage-location fields, secret keys, internal endpoint or application/session credentials. Metadata/errors keep the stricter no-storage-fields guarantee. Never log the URL, store it in a database/browser storage, capture it in HAR/traces or send it to analytics. Responses set `Cache-Control: no-store`, including authorization failures.

The referenced parse/audio must exist and belong together (404 otherwise); no node link is required for administrator preview. The service first checks the object through the internal S3 client, then signs with a separate public client. Public signing uses the standard `us-east-1` signing region without network discovery through the public host; all byte operations continue to use the internal endpoint. Missing, denied, unavailable or timed-out storage and missing/invalid public addressing return fixed 503 `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE` / `Audio storage is unavailable`. Provider messages are discarded. Stat/signing has a combined 30-second bound; caller cancellation propagates without success or writes.

`expiresAt` is the UTC S3 request timestamp plus `expiresInSeconds`. The browser must be able to reach `Oss:PublicBaseUrl` with its protocol/host. HTTPS consoles need HTTPS media to avoid mixed-content blocking. A configured path-prefix reverse proxy must strip that prefix before forwarding and preserve the signed Host and query. Native cross-origin media playback and fetch/WebAudio have different CORS requirements: configure bucket/proxy rules for the actual console origins and needed GET/HEAD/Range behavior; no global API CORS change is made here.

Deletion, replacement or changing storage/public-address configuration cannot immediately revoke a previously issued credential. It may remain usable until S3 expiry; deletion may also make it fail earlier. An object can disappear after Stat and normal media failures are expected. Expiry is enforced for new S3 requests, not necessarily an already-open stream or bytes already buffered in the browser. Request another link on play after expiry. Administrator playback has no permanent link, media proxy, transcoding or new settings UI.

No schema, package, port or deployment topology change is introduced. Reverting to the pre-playback image removes the routes/player while preserving the audio table; recovery to before #107 follows the verified backup/restore procedure above.

## Verification

`StructureAudioMatcherTests` covers all 58 paths, folder/name conflicts at multiple depths, ambiguous labels and token boundaries. `AudioImportReaderTests` covers signatures, normalized/unsafe paths, cancellation and invalid limits. `StructureAudioEndpointTests` uses the real host, PostgreSQL and isolated MinIO to check import/review/reload, authorization, unchanged rows/objects after rejected batches, cross-archive limits, concurrent duplicates, atomic save and interrupted read/upload/database/commit outcomes. `AdminHostedFrontendTests.OfficialAudioIsImportedReviewedAndSavedInTheConsole` imports the official fixture, reviews/clears/removes links and reloads via Chromium against real PostgreSQL/S3; synthetic ID3 bytes prove import, not media decoding. It also checks dirty navigation. Existing `AudioMigrationTests` and `StructureAudioLifecycleTests` retain schema, link integrity, parse/document deletion and grace-period/outage recovery coverage. The playback endpoint tests download valid WAV bytes through real MinIO before expiry and verify 403 after the configured 60-second lifetime, direct-only lists/ownership, no-store, exact fields, configuration isolation, public-prefix signing, object rejection and cancellation. `AdminHostedFrontendTests.NodeAudioPlaysRealWaveHandlesFailuresAndReleasesResources` verifies real Chromium playback/currentTime, switching/closing/unmount/reload, fresh requests and fixed 404/503/network/expiry/media failures against PostgreSQL/S3.

## Downstream audio proxy

For a recording directory, first read `GET /internal/v1/documents?hasAudio=true`, then the opened
document's `/internal/v1/documents/{documentFileId}/structure`. Every document and tree node
has an integer `audioCount`; document totals count current-tree links, while node counts are
direct and exclude descendants. Links require an existing node in that tree and the file's
selected definition. Unlinked/removed/dangling references and historical-tree rows are excluded;
historical match status does not suppress a real current link. False/omitted `hasAudio` retains
the unfiltered catalog and old v1 cursors; true uses a distinct cursor identity.

These reads query relational metadata only, without playback tokens or S3 access, even when
storage is unavailable. Counts are not playback availability guarantees. Each response has its
own repeatable-read snapshot; there is no shared snapshot across catalog pages, tree reads or
audio lists during concurrent changes. The next request reflects committed relinks, unlinks,
deletions and tree replacement. The ownership migration is required; restoring a verified matching backup and previous image removes
the additive filter/counts, so consumers must tolerate their absence (#122).

The [internal API](../Integration/InternalApi.md#direct-node-audio-and-temporary-playback)
provides anonymous direct-node metadata and same-origin expiring byte links. It reuses the
persisted current tree in read-only repeatable-read snapshots, independently of administrator
preview (which may play unlinked audio). Proxy links require internal S3 and a valid existing
TTL, but no public S3 address. They expose no key, endpoint, bucket or storage credential.
A process-local purpose-specific protector contains IDs only; restart invalidates links early.

Each byte request validates membership before bounded scratch buffering and again in a fresh
snapshot after buffering, including expiry and exact key/type/size identity. The internal SDK
callback is cancellable with a 30-second deadline. Random DeleteOnClose seekable scratch files
are bounded by stored size and MaxFileBytes, and disposed on completion, failure and disconnect.
GET supports 200/206/416; binary errors are fixed before response start and abort after start.
No token-bearing URL enters diagnostics or persistence. Deployer access logs must omit it.

A deletion/unlink before final admission refuses playback; a post-admission race may complete
an open stream, and the next request always rechecks. Links provide neither caller authentication
nor guaranteed availability. Callers re-list after expiry/restart. Internal routes grant no admin
permission. The proxy adds no schema, object layout or package changes. These routes are always
mapped; apply the configuration upgrade and rollback notes below. Existing audio import/review,
admin signing and backup/restore remain unchanged.

### Configuration upgrade and rollback (#127)

`InternalApi:Enabled` and `DocumentAudio:Enabled` are obsolete: any values are ignored and only the
present key names are logged together once at startup Warning level. Internal routes are always
mapped; deployers must block the whole case-insensitive `/internal` path segment at public ingress
and prevent direct public access to port 5012. Missing internal S3 settings disable import/sweep
and playback, while relational metadata, review/removal, direct-node metadata and catalog/tree
counts retain their existing behavior and missing-resource precedence. The optional public S3
address controls administrator preview only. No schema/data migration is required. Rollback to
the previous image needs both old switches set to `true` for the same behavior, with ingress
restrictions retained. See [deployment](../development/Deployment.md#upgrade-and-rollback-127).

## Retained bytes and exact-key concurrency (#132)

Audio lifetime belongs to its document/tree, independently of the node's optional content parse. Stored `parse_id` is immutable key provenance; a missing original parse does not invalidate playback. Successful reparse preserves node/audio IDs, links, metadata and exact object bytes. Internal capabilities use v2 document/tree/node/audio identity and both admission snapshots; their public `parseId` may be null. Existing process-local expiry, 30-second bounded scratch, cancellation/ranges/disposal and fixed diagnostics remain.

Imports generate immutable random keys and obtain PostgreSQL advisory session leases on the same connection used for metadata in ordinal key order before upload. The lease remains through metadata outcome and compensation; a backend identity check refuses a reconnected metadata writer, and uncertain reference checks defer cleanup. Removal, document cleanup and sweep use the same exact-key lease and recheck all current/historical references immediately before deletion. No document row lock waits for a key lease: metadata deletion commits first. An uncertain reference query defers cleanup; the one-hour grace sweep repairs recoverable orphans and is additional protection, not the concurrency proof. No parse deletion enumerates an origin prefix. Backup/matching-image recovery, including refusal of nonrepresentable Down, is documented in [migrations](../database/migrations.md#document-owned-structure-132).

## Document import compatibility and verification (#134)

No migration, backfill, key rewrite, dependency, setting, port or deployment change is required: #132 already represents null immutable provenance. Old rows/keys remain byte-for-byte unchanged. New document keys are checked against the actual server-owned document ID and recording ID in signing/buffering; legacy keys use immutable parse provenance even after parse deletion. Internal v2 tokens and both membership snapshots remain unchanged and never carry storage identity.

An older image cannot validate the new document prefix. Preserve these bytes and use a compatible image, or stop writers and restore a verified backup with its matching image/configuration and storage set. Image-only downgrade is not a data conversion. Ownership Down still refuses unrepresentable state before writes; never synthesize a parse or rewrite keys for recovery.

`DocumentAudioEndpointTests` verifies separate folder/ZIP books with 58 original generated 3-second WAV tones, explicit targets/manual provenance, zero parses, exact MinIO/downloaded hashes, 200/206/416 and direct counts/lists. Original official MP3 filenames remain separate matcher assertions; generated WAVs are not the copyrighted recordings. `DocumentAudioUploadRaceTests` and `DocumentAudioConcurrencyTests` use controlled uploads/PostgreSQL barriers for policy, tree, duplicate, full-save, deletion and parse transitions. Hosted Chromium verifies one-save drafts, all 58 real internal URLs decoding/playing/currentTime without public S3 addressing, then same-book source/explicit parse/bind/search/reparse while retaining chapters and audio. Existing import, legacy lifecycle and capability/scratch/cancellation safety remain required coverage.

# Structure Audio Import and Review

Administrators can import official audio for one parse, review pre-filled node links, clear or change them, and save removals atomically (#107/#108). The audio page is reached from the structure editor at `#audio/:parseId`. Playback and node-audio read routes remain deferred to [#109](https://github.com/philfanzhou/Doctheca/issues/109); the feature parent [#90](https://github.com/philfanzhou/Doctheca/issues/90) remains open.

## Authority and transactions

PostgreSQL [document_structure_audio](../database/tables/document_structure_audio.md) owns metadata,
the current node link and the internal object key. `document_structure_nodes` owns the tree. S3 owns
the bytes under `audio/{parseId:N}/{audioId:N}{extension}`; it is not a second metadata authority.

Internal insert/link repository operations and whole-tree replacement hold the same parse-row
lock. An inserted link whose node was removed is stored unlinked (`node_removed`). Invalid link
batches commit nothing. Tree replacement unlinks removed IDs in the same transaction, preserving
retained node IDs even though the tree is deleted and reinserted. `node_id` deliberately has no
foreign key for that replacement model; the repository checks same-parse membership.

## Storage boundary and configuration

`StructureAudioStore` alone writes/deletes `audio/` through the existing `IOssService`/`S3OssService`.
It refuses non-audio keys. Legacy upload/image prefixes remain read-only except for existing cleanup.
`DocumentAudio:Enabled` defaults to false. Enabling the lifecycle requires the internal S3 endpoint,
access/secret keys and bucket; local-file storage is unsupported. A public storage URL is not required for import or cleanup; playback settings are not introduced here. Invalid `DocumentAudio` settings only disable audio. Missing S3 settings disable recovery without
constructing a client; existing document/structure reads remain usable, and legacy storage operations
keep their existing S3 configuration requirements. See
[deployment](../development/Deployment.md#structure-audio-storage-issue-90).

## Deletion and recovery

| Event | Database | Object storage |
| --- | --- | --- |
| A node disappears during tree replacement | Keep metadata, clear its link, mark `node_removed` | Keep bytes |
| Parse deletion | Cascade-delete its metadata | After commit, delete known keys and all objects under its exact parse prefix |
| Document deletion | Cascade-delete metadata for every parse | Same cleanup, per parse |
| Object cleanup fails | The successful database deletion stays committed | Safe Warning records failure counts/types; recover with orphan sweep |

`POST /admin/structure-audio/orphan-sweep` performs administrator recovery. It requires `DocthecaAdmin`, uses
the existing security headers and returns `{ success: true, data: { scanned, deleted, failed } }`.
Missing identity returns 401, a non-admin returns 403, and disabled/unavailable storage returns a
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
| `DocumentAudio:Enabled` | `false` | Enables import/storage only with complete internal S3 settings |
| `DocumentAudio:MaxFileBytes` | `20971520` (20 MiB) | Positive; each audio upload or ZIP entry |
| `DocumentAudio:MaxArchiveBytes` | `209715200` (200 MiB) | At least MaxFileBytes, and no greater than Int64.MaxValue minus 1048576 |

Environment variables replace `:` with `__`. The request's uploaded file bytes and the actual expanded total across all loose files and ZIPs must each fit MaxArchiveBytes. Before buffering, the whole multipart body is capped at MaxArchiveBytes plus 1 MiB for boundaries, paths and headers, including requests without Content-Length. There are at most 1000 audio files and 4000 ZIP entries per request. Oversized request or expanded total returns 413; a per-file excess returns 400. Invalid limits disable audio without stopping the host.

Supported extensions/signatures: `.mp3` (ID3 or MPEG frame sync), `.m4a` (ftyp), `.wav` (RIFF/WAVE), `.ogg` (OggS), `.flac` (fLaC). This is format screening, not media decoding, malware detection, transcoding or permission to distribute the content. Callers are responsible for authorized content. ZIPs are streamed without extracting files to disk. Nested/broken archives, unsupported extensions/signatures, unsafe paths and duplicates reject the entire batch. `.DS_Store`, `__MACOSX/`, `._*`, `Thumbs.db`, `desktop.ini` and directory entries are skipped.

Paths use NFC Unicode and `/` separators, at most 1000 characters and 255 per segment. Absolute/drive paths, control characters, invalid Unicode, empty/whitespace segments and `.`/`..` segments are refused. Path equality is ordinal and case-sensitive, matching the existing database unique index. A multipart `paths` value for each file preserves browser folder names; otherwise multipart filenames are used.

Every batch is validated before any object or row write. Uploads are followed by one insert transaction under the parse lock. The unique parse/path index rejects concurrent duplicates; nodes deleted since matching become unlinked (`node_removed`), and a missing parse cannot insert. Reading, uploading and pre-commit boundaries observe request cancellation. Failed or cancelled imports compensate only objects confirmed unreferenced. Unknown commit/reference outcomes retain recoverable objects for the grace sweep; committed rows/bytes survive a cancelled response and are visible on reload. No cancelled operation is reported as successful.

## Matching

Folder and filename are compared separately against marked node names at every tree level. Case, spaces, punctuation and leading zeroes are ignored; labels match at token boundaries. No level name is hard-coded. The deepest agreed node is pre-filled; sibling ambiguity or folder/name conflicts stay unlinked. A child without its ancestor is not independently matched.

The 58 PEP Grade 9 fixture paths yield 49 Section links, 8 Unit vocabulary links and exactly one `unit3/9s04Unit4SectionA1e.mp3` conflict. `unit1/SectionB.mp3` links to B; `unit1/sectiona/SectionB.mp3`, `unit1/sectiona/Unit1SectionB.mp3` and `sectiona/Unit1SectionB.mp3` stay unlinked as `folder_name_mismatch`. Isolated `SectionB.mp3` is `no_match`.

## Administrator API

All routes require `DocthecaAdmin`, existing Cookie CSRF protection for writes and security response headers. Anonymous/non-admin calls return 401/403. Existing routes, authorization and envelopes remain unchanged.

| Method and path | Body / behavior |
| --- | --- |
| `GET /admin/document-parses/{parseId}/audio` | Safe metadata, storage availability/reason and exact import limits/extensions |
| `POST /admin/document-parses/{parseId}/audio` | Multipart `files` (audio/ZIP), optional one `paths` value per file; validates all, imports once |
| `PUT /admin/document-parses/{parseId}/audio` | `{ "links": [{ "id": "UUID", "nodeId": null }], "removeIds": ["UUID"] }`; validates all IDs before one transaction |
| `DELETE /admin/document-parses/{parseId}/audio/{audioId}` | Removes one file using the same atomic save path |
| `POST /admin/structure-audio/orphan-sweep` | Recovery described above |

Responses use `{ "success": true, "data": ... }`; errors use `{ "success": false, "message": "...", "errorCode": "..." }` and optional `{file,reason}` errors. The metadata allow-list is `id`, `sourcePath`, `folder`, `fileName`, `contentType`, `sizeBytes`, `nodeId`, `nodePath`, `matchStatus`, `linkSource`, `createdAt`, `updatedAt`. GET data contains `parseId`, `storage` and `items`. POST adds `imported`, `linked`, `flagged`, `skipped`; PUT returns `parseId`/`items`, and DELETE returns `id`/`deleted`. Storage contains only `available`, key-name-only `reason`, `maxFileBytes`, `maxArchiveBytes`, `maxFiles`, `maxArchiveEntries`, `maxPathLength`, `maxSegmentLength`, `multipartOverheadBytes`, and `extensions`. No object key, bucket, internal/public endpoint or storage credential appears in these responses or logs.

| Failure | HTTP / error code |
| --- | --- |
| Unknown parse, audio or node (including IDs belonging to another parse) | 404 / `DOCTHECA_PARSE_NOT_FOUND`, `DOCTHECA_AUDIO_NOT_FOUND`, `DOCTHECA_STRUCTURE_NODE_NOT_FOUND` |
| Malformed/unsupported batch | 400 / `DOCTHECA_AUDIO_IMPORT_INVALID` |
| Invalid save, including duplicate link IDs | 400 / `DOCTHECA_AUDIO_INVALID` |
| Request or expanded total over limit | 413 / `DOCTHECA_AUDIO_IMPORT_TOO_LARGE` |
| Existing source path / no structure nodes | 409 / `DOCTHECA_AUDIO_ALREADY_IMPORTED`, `DOCTHECA_AUDIO_STRUCTURE_REQUIRED` |
| Disabled or unavailable storage | 503 / `DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED`, `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE` |

Missing parse or disabled storage is refused before buffering uploads. Removal commits before best-effort object deletion, with sweep recovery for cleanup outages. The last complete valid save wins; invalid review batches change nothing. This adds no database schema or dependency. The foundation upgrade/backup recovery procedure above still applies; rollback may lose post-backup work and does not promise old-image/new-schema compatibility.

## Console workflow

Choose a folder, multiple files or ZIPs, then review flagged files or all files. Change/clear links and mark removals, then save once and reload persisted results. Import merges new rows while keeping an existing unsaved draft. Reload, sidebar/back/hash navigation and logout require discard confirmation for a dirty draft; tab close uses the browser's unload confirmation. In-flight import/save prevents navigation and duplicate writes. Paths/node names render as escaped text. Drafts stay in memory; neither payloads nor credentials are persisted in browser storage. Authentication failures never replay a write automatically. There is no player UI, text/exercise alignment, transcript or downstream audio read API.

## Deferred playback

The maintainer selected the current
[Lexarbor playback model](https://github.com/philfanzhou/Lexarbor/blob/d91451c8094215d783b76497f44e76515d669d4e/docs/api/Audio.md):
a browser receives a public S3 short-lived signed URL, with `Cache-Control: no-store`. Such a URL
contains its public host, bucket/object path, access-key identifier and temporary signature; it is
itself a temporary playback credential. #109 must keep those locations out of separate response
fields and all non-playback responses, never return internal endpoints/secret keys/application
credentials, and never log/persist/send signed URLs to analytics. Issued links may survive deletion,
replacement or disablement until expiry. No proxy or immediate revocation is promised. No playback
code or URL is shipped in this delivery.

## Verification

`StructureAudioMatcherTests` covers all 58 paths, folder/name conflicts at multiple depths, ambiguous labels and token boundaries. `AudioImportReaderTests` covers signatures, normalized/unsafe paths, cancellation and invalid limits. `StructureAudioEndpointTests` uses the real host, PostgreSQL and isolated MinIO to check import/review/reload, authorization, unchanged rows/objects after rejected batches, cross-archive limits, concurrent duplicates, atomic save and interrupted read/upload/database/commit outcomes. `AdminHostedFrontendTests.OfficialAudioIsImportedReviewedAndSavedInTheConsole` imports the official fixture, reviews/clears/removes links and reloads via Chromium against real PostgreSQL/S3; synthetic ID3 bytes prove import, not media decoding. It also checks dirty navigation. Existing `AudioMigrationTests` and `StructureAudioLifecycleTests` retain schema, link integrity, parse/document deletion and grace-period/outage recovery coverage. Playback routes remain absent.

# Structure Audio Import, Review and Playback

Administrators can import official audio for one parse, review pre-filled node links, clear or change them, and save removals atomically (#107/#108). The audio page is reached from the structure editor at `#audio/:parseId`. Administrators can preview imported audio and list a node’s direct audio through the authenticated APIs below (#109). Downstream current-node audio uses expiring same-origin proxy links (#101), described below.

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
Audio storage is available when the internal S3 endpoint,
access/secret keys and bucket are complete; local-file storage is unsupported. A public storage URL is not required for import or cleanup. Administrator playback additionally requires an explicitly valid `Oss:PublicBaseUrl` and `DocumentAudio:PlaybackLinkSeconds` (default 300, allowed 60–3600); missing/invalid public addressing disables only administrator playback, which never falls back to the internal endpoint. These settings are acquired at startup; restart to apply changes. Invalid numeric settings refuse startup before database initialization or HTTP listening, even without S3. Missing S3 settings disable recovery without
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
| `GET /admin/document-parses/{parseId}/structure/nodes/{nodeId}/audio` | `{parseId,nodeId,items}` containing only direct links, with the same safe item projection; no descendants |
| `GET /admin/document-parses/{parseId}/audio/{audioId}/playback-link` | `{url,expiresAt,expiresInSeconds}` in the existing success envelope; no-store on success/failure |

Responses use `{ "success": true, "data": ... }`; errors use `{ "success": false, "message": "...", "errorCode": "..." }` and optional `{file,reason}` errors. The metadata allow-list is `id`, `sourcePath`, `folder`, `fileName`, `contentType`, `sizeBytes`, `nodeId`, `nodePath`, `matchStatus`, `linkSource`, `createdAt`, `updatedAt`. The parse-audio metadata GET contains `parseId`, `storage` and `items`. POST adds `imported`, `linked`, `flagged`, `skipped`; PUT returns `parseId`/`items`, and DELETE returns `id`/`deleted`. Storage contains only `available`, key-name-only `reason`, `maxFileBytes`, `maxArchiveBytes`, `maxFiles`, `maxArchiveEntries`, `maxPathLength`, `maxSegmentLength`, `multipartOverheadBytes`, and `extensions`. No object key, bucket, internal/public endpoint or storage credential appears in these responses or logs.

| Failure | HTTP / error code |
| --- | --- |
| Unknown parse, audio or node (including IDs belonging to another parse) | 404 / `DOCTHECA_PARSE_NOT_FOUND`, `DOCTHECA_AUDIO_NOT_FOUND`, `DOCTHECA_STRUCTURE_NODE_NOT_FOUND` |
| Malformed/unsupported batch | 400 / `DOCTHECA_AUDIO_IMPORT_INVALID` |
| Invalid save, including duplicate link IDs | 400 / `DOCTHECA_AUDIO_INVALID` |
| Request or expanded total over limit | 413 / `DOCTHECA_AUDIO_IMPORT_TOO_LARGE` |
| Existing source path / no structure nodes | 409 / `DOCTHECA_AUDIO_ALREADY_IMPORTED`, `DOCTHECA_AUDIO_STRUCTURE_REQUIRED` |
| Unconfigured or unavailable storage | 503 / `DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED`, `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE` |

Missing parse or unconfigured storage is refused before buffering uploads. Removal commits before best-effort object deletion, with sweep recovery for cleanup outages. The last complete valid save wins; invalid review batches change nothing. This adds no database schema or dependency. The foundation upgrade/backup recovery procedure above still applies; rollback may lose post-backup work and does not promise old-image/new-schema compatibility.

## Console workflow

Choose a folder, multiple files or ZIPs, then review flagged files or all files. Change/clear links and mark removals, then save once and reload persisted results. Import merges new rows while keeping an existing unsaved draft. Reload, sidebar/back/hash navigation and logout require discard confirmation for a dirty draft; tab close uses the browser's unload confirmation. In-flight import/save prevents navigation and duplicate writes. Saving also disables node selection/clearing and removal/undo from the deletion confirmation until the request finishes; cancelling confirmation or a failed request keeps the draft editable. Paths/node names render as escaped text. Drafts stay in memory; neither payloads nor credentials are persisted in browser storage. Authentication failures never replay a write automatically. Each play click obtains a fresh link, including an unlinked imported file. The player handles missing files, expired links, storage/network and decoding failures with fixed messages. Switching, closing, reloading the list, saving, or leaving releases the media resource (pause, remove src, load); pending link requests are aborted and late responses ignored. URLs stay in component memory/DOM only. Text/exercise alignment and transcripts remain excluded from the administrator UI.

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
has an integer `audioCount`; document totals count current-parse links, while node counts are
direct and exclude descendants. Links require an existing node in that parse and the file's
selected definition. Unlinked/removed/dangling references and old-parse rows are excluded;
historical match status does not suppress a real current link. False/omitted `hasAudio` retains
the unfiltered catalog and old v1 cursors; true uses a distinct cursor identity.

These reads query relational metadata only, without playback tokens or S3 access, even when
storage is unavailable. Counts are not playback availability guarantees. Each response has its
own repeatable-read snapshot; there is no shared snapshot across catalog pages, tree reads or
audio lists during concurrent changes. The next request reflects committed relinks, unlinks,
deletions and tree replacement. No migration is needed; restoring the previous image removes
the additive filter/counts, so consumers must tolerate their absence (#122).

The optional [internal API](../Integration/InternalApi.md#direct-node-audio-and-temporary-playback)
provides anonymous direct-node metadata and same-origin expiring byte links. It reuses the
current parsed tree rule in read-only repeatable-read snapshots, independently of administrator
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

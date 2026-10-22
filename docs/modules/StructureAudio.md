# Structure Audio Persistence and Object Lifecycle

The foundation (#107) adds audio metadata and object ownership for the feature parent [#90](https://github.com/philfanzhou/Doctheca/issues/90).
Import/matching/administrator review [#108](https://github.com/philfanzhou/Doctheca/issues/108) and
node listing/playback [#109](https://github.com/philfanzhou/Doctheca/issues/109) are pending. No audio
page, import/list/edit route or playback route is available in this delivery.

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
access/secret keys and bucket; local-file storage is unsupported. The public URL, import limits and
playback TTL are not prerequisites or settings for this foundation. Invalid `DocumentAudio` settings only disable audio. Missing S3 settings disable recovery without
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

`POST /admin/structure-audio/orphan-sweep` is the only new route. It requires `DocthecaAdmin`, uses
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

## Deferred import and playback

The removed full implementation remains reference material at
[eaa608b](https://github.com/philfanzhou/Doctheca/tree/eaa608bc65566ac2c7cc33f3714ccc2aa5959721).
#108 must preserve its reviewed folder/name conflict repair and regressions; removing it from this
foundation does not deliver that behavior to users. #90 remains open until all children pass.

The maintainer selected the current
[Lexarbor playback model](https://github.com/philfanzhou/Lexarbor/blob/d91451c8094215d783b76497f44e76515d669d4e/docs/api/Audio.md):
a browser receives a public S3 short-lived signed URL, with `Cache-Control: no-store`. Such a URL
contains its public host, bucket/object path, access-key identifier and temporary signature; it is
itself a temporary playback credential. #109 must keep those locations out of separate response
fields and all non-playback responses, never return internal endpoints/secret keys/application
credentials, and never log/persist/send signed URLs to analytics. Issued links may survive deletion,
replacement or disablement until expiry. No proxy or immediate revocation is promised. No playback
code or URL is shipped in this foundation.

## Verification

`AudioMigrationTests` verifies populated additive upgrades, schema refusal and the separate
destructive down operation against PostgreSQL. `StructureAudioLifecycleTests` exercises real S3
writes, link integrity (including concurrent tree replacement/insertion), parse/document deletion,
orphan grace period, outage recovery, authorization and disabled/malformed configuration through
the real host. Real S3 listing rejection is distinguished from a successful empty bucket, while
legacy listing behavior stays unchanged. It verifies that deferred audio routes are absent. Import's 58-path and browser
review criteria and playback expiry/browser tests belong to #108/#109 and are not claimed here.

# StructureAudio — Official Audio Linked to Structure Nodes

> Issue #90. Official audio files (for example the recordings of a textbook) are imported for one parse that has hand-marked [structure nodes](DocumentStructure.md) and linked to those nodes at node level — a Section for most files, a Unit for vocabulary files. Links are pre-filled from folder and file names and adjusted by hand. No level name or file naming convention is known to the code.

## Capability Overview

- Import a folder, several files, or ZIP archives of audio for one parse that has marked structure nodes.
- Every file's node is pre-filled by matching its folder and file name against the node names; files whose folder and name disagree, that match nothing, or whose match is ambiguous are left unlinked and flagged.
- The admin console lists the files with their links and flags; an administrator changes or clears links, removes files, and saves.
- Admin endpoints list a node's audio and hand out short-lived playback links.
- Out of scope: linking audio to text ranges or exercises, transcripts, audio processing or transcoding, the downstream (non-admin) read API (#91), and StructaDoc changes.

## Storage

Audio is stored in the existing S3-compatible object storage through `IOssService` / `S3OssService` (the Minio SDK), in the configured `Oss:BucketName`, under the dedicated prefix `audio/` (`OssPathPrefixConstants.Audio`). Each object key is `audio/{parseId:N}/{audioId:N}{extension}`; it contains no file name. Only `StructureAudioStore` writes, reads or deletes these keys, and it refuses any key outside the prefix, so legacy upload objects and parse images stay read-only. The table is [document_structure_audio](../database/tables/document_structure_audio.md).

**Never exposed:** object keys, the bucket, the internal endpoint, and credentials appear in no response and no log line (storage failures log only the exception type). Playback uses an S3 presigned GET link built on `Oss:PublicBaseUrl`; by the nature of S3 signing, that link's path names the bucket and object and its query carries the access key id (never the secret) and the signature, and it stops working after the configured period.

## Configuration

Section `DocumentAudio` (Doctheca-private; environment form `DocumentAudio__Enabled` and so on):

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `false` | Turns structure audio on |
| `MaxFileBytes` | `20971520` (20 MiB) | Largest accepted audio file, uploaded or inside an archive |
| `MaxArchiveBytes` | `209715200` (200 MiB) | Largest import: the uploaded bytes of one request (a ZIP or the sum of the files) and the total uncompressed audio of one import |
| `PlaybackLinkSeconds` | `300` | Lifetime of a playback link, 60–3600 seconds |

Audio storage is **available** only when `Enabled` is true, the limits are valid, S3 storage is in use (not `USE_LOCAL_OSS=1`, whose local file store cannot sign links), and `Oss:InternalEndpoint`, `Oss:AccessKey`, `Oss:SecretKey`, `Oss:BucketName` and `Oss:PublicBaseUrl` are all set. The decision is made once at startup and logged as `Document audio: Available=…, Reason=…` (the reason names missing keys, never values). When it is not available, import, playback and the orphan sweep answer `503 DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED` with that reason, listing and link changes keep working, and the rest of Doctheca is unaffected.

Fixed limits: at most 1000 audio files per import; paths at most 1000 characters, segments at most 255.

## Import

`POST /admin/document-parses/{parseId}/audio`, `multipart/form-data`, any number of file parts (field name `files` by convention):

- Each part is an audio file (`.mp3`, `.m4a`, `.wav`, `.ogg`, `.flac`) or a `.zip` archive of them (folders inside are kept; nested archives are not audio and are rejected).
- A part's relative path is its multipart file name, or — when the form carries exactly one `paths` value per file part, in the same order — that value. The console sends `paths` because browsers drop folders from file names.
- Paths must be relative: absolute paths, drive or stream names (`:`), `.`/`..` segments, empty segments and control characters are rejected; backslashes count as separators; names are stored in Unicode NFC. ZIP entry names without the UTF-8 flag are read as UTF-8 (macOS Finder archives store UTF-8 names without the flag).
- An audio file's extension must agree with its signature (ID3 or an MPEG frame sync for MP3, `ftyp` for M4A, `RIFF…WAVE`, `OggS`, `fLaC`); empty files are rejected. Sizes are counted while reading, never trusted from the archive header alone.
- Operating system metadata is skipped and reported as `skipped`: anything under `__MACOSX/`, `.DS_Store`, `._*`, `Thumbs.db`, `desktop.ini`, and directory entries.

**Everything is validated before anything is stored.** Any rejected file rejects the whole import with nothing stored:

| Status | errorCode | When |
|--------|-----------|------|
| 400 | `DOCTHECA_AUDIO_IMPORT_INVALID` | A non-audio or disguised file, an oversized file, an unsafe path, an unreadable archive, a path twice in one import, no audio at all; `errors: [{ file, reason }]` names every file |
| 413 | `DOCTHECA_AUDIO_IMPORT_TOO_LARGE` | The request, the sum of the files, or an archive's uncompressed audio exceeds `MaxArchiveBytes` |
| 409 | `DOCTHECA_AUDIO_STRUCTURE_REQUIRED` | The parse has no marked structure nodes |
| 409 | `DOCTHECA_AUDIO_ALREADY_IMPORTED` | A path is already imported for this parse (remove it first) |
| 404 | `DOCTHECA_PARSE_NOT_FOUND` | Unknown parse (checked before the body is read) |
| 503 | `DOCTHECA_AUDIO_STORAGE_NOT_CONFIGURED` / `DOCTHECA_AUDIO_STORAGE_UNAVAILABLE` | Storage not configured, or a storage write failed (objects already written by this import are deleted again) |

Success answers `{ parseId, imported, linked, flagged, items, skipped }`; `items` use the shape below.

## Pre-filling Links

Implemented by `StructureAudioMatcher`; only the node names of the parse's marked tree are compared.

1. **Tokens.** Names are split at separators, at letter/digit changes and at lower-to-upper case changes, lower-cased, and numbers lose leading zeros: `9s01Unit1SectionA1b` → `9 s 1 unit 1 section a 1 b`. Case, spaces and punctuation are therefore ignored, and `Unit 01` equals `Unit1`.
2. **Labels.** A node is matched by its label: the words of its name up to and including the first identifier after a word — a number (with directly following numbers, so `Part 2.1` → `part21`), a single Latin letter, or a Roman numeral. `Unit 1 The Changing World` → `unit1`, `Section A` → `sectiona`, `Project` → `project`. Names in uncased scripts (for example Chinese) use the whole name.
3. **Matches.** A label matches when it occurs in the compared text starting and ending at token boundaries (`unit1` matches `9s01Unit1SectionA1b` but not `Unit10`).
4. **Descent.** At each level, folder (the directory part of the path) and file name (without extension) are compared separately against the children of the current node, starting at the root. Two matching siblings in either source make the result ambiguous, unless one sibling's matches all lie inside a longer sibling label's match (`Section` versus `Section A`).
5. **Agreement.** If only one of folder and name matches a child, descent continues through that child. If both match, they must identify the same child; otherwise the file is flagged `folder_name_mismatch`. This checks names that omit their parent labels: `unit1/SectionB.mp3` reaches Unit 1's Section B, while `unit1/sectiona/SectionB.mp3` is a conflict. A file named only `SectionB.mp3`, without evidence of its parent, remains unmatched. Descent stops at the deepest agreed node when neither source matches a child.
6. **Result.** A found node is stored with `matchStatus` `matched` and `linkSource` `prefill`; otherwise the file is unlinked with `no_match`, `ambiguous` or `folder_name_mismatch`.

With the PEP Grade 9 English textbook marked with eight Unit nodes and their Section nodes, the 58 official files pre-link every Section file whose folder and name agree to its Section, the eight `9sVocabularyNNUnitN.mp3` files to their Units, and flag `unit3/9s04Unit4SectionA1e.mp3` (folder Unit 3, name Unit 4).

## Reviewing and Changing Links

| Method | Path | Result |
|--------|------|--------|
| GET | `/admin/document-parses/{parseId}/audio` | `{ parseId, storage, items }`; `storage` is `{ available, reason, maxFileBytes, maxArchiveBytes, maxFiles, playbackLinkSeconds, extensions }` |
| PUT | `/admin/document-parses/{parseId}/audio` | Body `{ "links": [{ "id", "nodeId" | null }], "removeIds": [ … ] }`; applies all changes in one transaction and answers the saved list |
| DELETE | `/admin/document-parses/{parseId}/audio/{audioId}` | Removes one file (row and object) |
| GET | `/admin/document-parses/{parseId}/structure/nodes/{nodeId}/audio` | The files linked directly to that node (not its descendants) |
| GET | `/admin/document-parses/{parseId}/audio/{audioId}/playback-link` | `{ url, expiresAt, expiresInSeconds }` — a presigned link valid for `PlaybackLinkSeconds` |
| POST | `/admin/structure-audio/orphan-sweep` | Deletes stored audio objects that no row references and that are older than one hour; `{ scanned, deleted, failed }` |

Item shape:

```json
{
  "id": "…", "sourcePath": "unit3/9s04Unit4SectionA1e.mp3", "folder": "unit3", "fileName": "9s04Unit4SectionA1e.mp3",
  "contentType": "audio/mpeg", "sizeBytes": 2347719,
  "nodeId": null, "nodePath": null,
  "matchStatus": "folder_name_mismatch", "linkSource": null,
  "createdAt": "2026-10-22T08:00:00.000Z", "updatedAt": "2026-10-22T08:00:00.000Z"
}
```

A link change sets `linkSource` to `manual` (also when a link is cleared); `matchStatus` keeps the pre-fill outcome. In a PUT, an unknown file id answers 404 `DOCTHECA_AUDIO_NOT_FOUND` and a node id outside the parse's tree 404 `DOCTHECA_STRUCTURE_NODE_NOT_FOUND`, and nothing changes; a link entry without an id or a file listed twice answers 400 `DOCTHECA_AUDIO_INVALID`. Imports and link saves take the same `document_parses` row lock as a structure save, so links never point into a tree that is being replaced.

All routes require the `DocthecaAdmin` policy (401 without a session, 403 without `role=admin`), answer the unified JSON envelope, and carry the ServiceMantle security response headers. An unknown parse, node or file answers 404.

## Deletion

| Event | Audio rows | Stored objects |
|-------|-----------|----------------|
| A file is removed (DELETE or `removeIds`) | Deleted | Deleted after the commit |
| A structure save no longer contains a node | Kept, unlinked (`node_id` NULL, `matchStatus` `node_removed`, `linkSource` NULL) | Kept |
| A parse is deleted (`DELETE /admin/document-parses/{id}`) | Deleted by CASCADE | The keys the rows referenced and, when storage is available, everything else under `audio/{parseId}/` (for example objects of an interrupted import) are deleted after the rows |
| A document file is deleted | Deleted by CASCADE for every parse | As for each of its parses |

Object deletion after a committed row deletion is best-effort, like the existing legacy-object cleanup: a storage failure is logged as a warning with the parse id and never fails the request. `POST /admin/structure-audio/orphan-sweep` then removes whatever is left — any object under `audio/` that no row references and that is older than one hour (younger objects may belong to an import in flight).

## Admin Console

- **官方音频** (`#audio/:parseId`), reached from the 结构标注 page and from a parsed parse record on the document detail page.
- 导入音频: 选择文件夹 (the whole folder, with its subfolders) or 选择文件或 ZIP. The page states the accepted types and limits, shows upload progress, and lists every rejected file when an import is refused.
- The table lists every file with its folder, size, linked node (a searchable select of all nodes by path, clearable), and its state: 已自动匹配, 已手动关联, 已手动取消关联, or a flag — 文件夹与文件名不一致, 匹配不唯一, 未匹配到节点, 原节点已删除. Filters: 全部 / 待处理 / 已关联; after an import with flagged files the page switches to 待处理.
- Edits stay local (marked 未保存 / 待删除) until 保存, which sends one PUT; 重新加载 discards them. 试听 fetches a playback link and plays it in the page.

## Validation

- Unit: `Structure/StructureAudioMatcherTests` (the 58 real file paths, labels, tokens, boundaries, folder/name agreement, ambiguity, three levels).
- PostgreSQL + S3 (`pgsty/minio` container): `Integration/StructureAudioEndpointTests` (the textbook import end to end, link changes surviving a reload, node removal, file/parse/document deletion with object counts, every rejected import storing nothing, 401/403/404, storage not configured, link expiry after 60 s, orphan sweep, no storage details in any response); `Database/AudioMigrationTests` (in-place upgrade, refused states, rollback script).
- Browser: `Integration/AdminHostedFrontendTests.OfficialAudioIsImportedReviewedAndSavedInTheConsole` (Playwright: folder import, review, change, clear, remove, save, reload, playback link) and `frontend/tests/audio-links.mjs`.

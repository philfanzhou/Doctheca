# Internal read API (v1)

This read-only API is for downstream business services inside the internal network.
`InternalApi:Enabled` defaults to `false`; missing, false or malformed values disable it.
Only explicit true at process startup registers these anonymous routes:

- `GET /internal/v1/structure-definitions`
- `GET /internal/v1/documents`
- `GET /internal/v1/documents/{documentFileId}/structure`

There is no caller authentication or document-level authorization. The deployer must restrict
the entire `/internal` path segment, case insensitively, to internal callers and prevent direct
public access to port 5012. Request parameters, Cookies and Bearer tokens cannot enable it.
Existing `/admin/*` authentication and CSRF rules are unchanged. See
[deployment](../development/Deployment.md#internal-api).

Catalogs (#97) and the current document tree (#98) are implemented. Content/images (#99),
search (#100) and audio (#101) are separate tasks under [#91](https://github.com/philfanzhou/Doctheca/issues/91).
Unimplemented paths return 404 even when the setting is enabled.

## Lists and inputs

Both lists return `200` with `{"success":true,"data":{"items":[],"nextPageToken":null}}`.
`pageSize` defaults to 20 and accepts decimal integers from 1 through 100.
`pageToken` is optional on the first request; use the returned token for subsequent requests.
An empty token is invalid. Tokens are at most 2048 characters, canonical unpadded base64url of a
versioned last UUID and normalized filter identity. Treat tokens as opaque cursors; they are
neither secrets nor authorization. A malformed token, unsupported version, wrong list/filter,
duplicate parameter, unknown parameter or malformed value returns the fixed 400 below.
The page size may change between requests.

Items are ordered by UUID ascending and selected with `id > last UUID`, with no offsets.
An unchanged dataset yields every matching item once, including duplicate names/timestamps.
Concurrent additions/removals affect later pages; there is no snapshot across requests.
Each response uses a PostgreSQL repeatable-read snapshot for its related reads. Reads make no
database, search-index or object-storage writes. Request cancellation aborts the read.

### Structure definitions

Only `pageSize` and `pageToken` are accepted. Each item contains exactly:

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "name": "English textbook",
  "levels": [
    {"id": "00000000-0000-0000-0000-000000000002", "name": "Unit", "parentLevelId": null, "sortOrder": 0}
  ]
}
```

Levels are ordered by `sortOrder`, then UUID. Names are configured administrator data, not
fixed application enums. No creation/update metadata is included.

### Documents

Optional filters are `structureDefinitionId` (canonical hyphenated UUID), `subject` and `grade`.
Filter input is trimmed; empty/whitespace-only filters are absent. Subject/grade accept up to
100 characters after trimming and match stored values exactly, including case. Stored values
are never rewritten. A valid UUID for an unknown definition returns the fixed 404 below.

Only documents with at least one **parsed** parse containing saved structure nodes qualify.
An older marked parsed parse remains sufficient when a newer parse is unmarked or pending.
Unmarked documents and trees on failed/pending parses alone are excluded. Filtering is applied
in SQL using an existence predicate, without loading full documents/parses for each row.
Definition metadata is the document's currently selected definition. Each item contains exactly:

```json
{
  "id": "00000000-0000-0000-0000-000000000003",
  "title": "Textbook.pdf",
  "subject": "English",
  "grade": "Grade 9",
  "structureDefinitionId": "00000000-0000-0000-0000-000000000001",
  "structureDefinitionName": "English textbook"
}
```

`title` is the current file name. Subject/grade and definition metadata can be null.
Treat all names/text as untrusted data. Responses omit paths, StructaDoc identifiers, creator
identity, provider/block JSON, storage/configuration fields and credentials.

## Current document structure

`GET /internal/v1/documents/{documentFileId}/structure` accepts a canonical hyphenated UUID
and no query parameters. A malformed UUID or any query parameter returns the fixed 400.
The response contains exactly the following fields, recursively for every node:

```json
{
  "success": true,
  "data": {
    "documentFileId": "00000000-0000-0000-0000-000000000003",
    "parseId": "00000000-0000-0000-0000-000000000004",
    "nodes": [
      {
        "id": "00000000-0000-0000-0000-000000000005",
        "levelId": "00000000-0000-0000-0000-000000000002",
        "levelName": "Unit",
        "name": "Unit 3",
        "path": "Unit 3",
        "startSequence": 10,
        "endSequence": 40,
        "startPageNumber": 3,
        "endPageNumber": 7,
        "children": []
      }
    ]
  }
}
```

The selected current parse has `status=parsed` and at least one saved structure node. Order
by `ParsedAt` descending with null timestamps last, then UUID descending. When every timestamp
is null, the greatest UUID wins. A newer unmarked, pending, parsing or failed parse does not
hide a marked parsed parse. A newer qualifying tree becomes current on the next request;
`parseId` identifies the actual selected parse. There is no inferred tree or mixing of parses.

The complete saved tree supports the existing definition limit of 50 nested levels.
Level names and node names are stored data; no level name has special meaning. `path` joins
ancestor names and the node name with ` > `. Siblings are ordered by `sortOrder`, then
`startSequence`, then UUID. The sequence bounds are inclusive. Physical page bounds are the
minimum/maximum `PageId + 1` among sequence-bearing Blocks within those bounds, or both null
when there are none. They are physical page numbers, not printed page labels or inferred headings.

Document existence, parse selection, levels, nodes and page aggregation share one PostgreSQL
repeatable-read transaction. A concurrent complete tree replacement or deletion returns one
committed pre- or post-change snapshot, or the documented 404; it never mixes trees. Readers
do not take the writer's parse-row update lock. The shared typed node lookup also rejects nodes
outside their owning document's selected current parse; that ownership is valid for its read
snapshot and must be resolved again by later requests. Cancellation aborts reads and cannot
produce a partial successful response. The endpoint writes no data or external objects.
No file name, administrative timestamps, raw Block/provider JSON or storage identity is included.

## Errors, confidentiality and rollback

| Condition | Status | Fixed error code |
| --- | --- | --- |
| Disabled, unknown internal path or unsupported method | 404 | `DOCTHECA_INTERNAL_NOT_FOUND` |
| Invalid identifier/list/filter/cursor input | 400 | `DOCTHECA_INTERNAL_INVALID` |
| Unknown requested definition | 404 | `DOCTHECA_INTERNAL_DEFINITION_NOT_FOUND` |
| Unknown document | 404 | `DOCTHECA_INTERNAL_DOCUMENT_NOT_FOUND` |
| Document exists without a qualifying marked parsed tree | 404 | `DOCTHECA_INTERNAL_STRUCTURE_NOT_FOUND` |
| Database/unexpected read failure on an implemented route | 500 | `http.internal_server_error` |

Business errors use `{"success":false,"errorCode":"...","message":"..."}` with fixed English
messages. Unexpected failures use the existing safe ServiceMantle Problem Details boundary
(`application/problem+json`) and a correlation id. No raw input, provider error, stack trace,
document content, storage reference or credential is added to error bodies or diagnostics.
Every `/internal` path is reserved before static/SPA handling, including nested static-looking
paths and `/internal` itself; `/internalized` remains an ordinary SPA path.

No database migration, new package, storage write or port change is introduced. Disable the
setting and restart, or restore the previous image, to remove the API without data rollback.
This API does not implement network isolation or guarantee a snapshot across list requests.

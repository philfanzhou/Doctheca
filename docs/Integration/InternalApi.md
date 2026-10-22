# Internal read API (v1)

This read-only API is for downstream business services inside the internal network.
`InternalApi:Enabled` defaults to `false`; missing, false or malformed values disable it.
Only explicit true at process startup registers these anonymous routes:

- `GET /internal/v1/structure-definitions`
- `GET /internal/v1/documents`
- `GET /internal/v1/documents/{documentFileId}/structure`
- `GET /internal/v1/structure-nodes/{nodeId}/content`
- `GET /internal/v1/structure-nodes/{nodeId}/images/{imageId}/content`
- `GET /internal/v1/search`

There is no caller authentication or document-level authorization. The deployer must restrict
the entire `/internal` path segment, case insensitively, to internal callers and prevent direct
public access to port 5012. Request parameters, Cookies and Bearer tokens cannot enable it.
Existing `/admin/*` authentication and CSRF rules are unchanged. See
[deployment](../development/Deployment.md#internal-api).

Catalogs (#97), current trees (#98), content/owned images (#99) and search (#100) are implemented.
Audio (#101) is a separate task under [#91](https://github.com/philfanzhou/Doctheca/issues/91).
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

## Node content pages

`GET /internal/v1/structure-nodes/{nodeId}/content?afterSequence=&limit=&revision=`
accepts a canonical hyphenated node UUID. Omit unused parameters; empty values are invalid.
`limit` defaults to 200 and accepts 1 through 1000. `afterSequence` is an optional nonnegative
32-bit decimal integer within this node's inclusive bounds. Only ASCII digits are accepted;
signs, whitespace, fractions, overflow, duplicate and unknown parameters return 400.
A cursor need not point to an existing block (sequence gaps are allowed). The page returns
only sequence-bearing blocks with `sequence > afterSequence` inside the inclusive node range,
ordered by global sequence. An unchanged dataset yields each block exactly once. One extra
row determines continuation; no offset or neighbor-node blocks are used.

```json
{
  "success": true,
  "data": {
    "parseId": "00000000-0000-0000-0000-000000000004",
    "nodeId": "00000000-0000-0000-0000-000000000005",
    "revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "items": [
      {
        "id": "00000000-0000-0000-0000-000000000006",
        "sequence": 10,
        "blockType": "text",
        "subType": null,
        "content": "Untrusted document text",
        "contentFormat": null,
        "pageNumber": 3,
        "imageId": null,
        "imageUrl": null
      }
    ],
    "nextAfterSequence": null,
    "hasMore": false
  }
}
```

The initial request returns a 64-character lowercase hexadecimal SHA-256 revision. Every
request with `afterSequence` must also send the preceding revision. A revision may be sent
on the initial request too. Hexadecimal input is case insensitive. Missing required or
malformed revisions return 400; a different revision returns 409
`DOCTHECA_INTERNAL_STRUCTURE_CHANGED`. Discard previous pages and restart without a cursor.
Do not silently retry an old cursor without its revision. A deleted/replaced node or a node
outside the selected current parse returns 404. Empty ranges return an empty page; a cursor
at the inclusive end returns an empty terminal page.

Each request resolves current-node/parse/definition ownership, revision and blocks inside
one PostgreSQL repeatable-read snapshot. The revision fingerprints the selected parse,
complete current tree IDs, levels, parents, names, ranges, order and creation/update times,
and ordered in-range block IDs, sequences and image IDs. Synced blocks receive new IDs;
tree replacement, current-parse changes or changed block membership cannot silently complete
an old traversal. It does not keep a server-side snapshot alive across HTTP requests or hash
document text; arbitrary direct database edits to text with unchanged IDs are outside the
supported synchronization contract. A current-parse switch normally makes old node IDs 404.
Physical `pageNumber` is `PageId + 1`. Empty formats become null. Content includes text/table
strings as data: consumers must not execute HTML. Raw `blockData`, provider JSON, administrative
metadata and storage references are excluded. Optional Markdown is not supplied.

## Owned image bytes

When a block references an image, `imageUrl` is a relative link to
`GET /internal/v1/structure-nodes/{nodeId}/images/{imageId}/content?revision=...`.
Both IDs must be canonical hyphenated UUIDs. Only the required 64-hex `revision` parameter is
accepted. The route re-resolves the current node and revision in its own repeatable-read
snapshot, then requires the image's parse ID to match and at least one in-range block to
reference that image. Unknown, cross-node, cross-parse and stale references fail before any
source is opened. The caller cannot supply a URL, object key or source location.

`ParseImageContentSource` reads the scoped versioned StructaDoc asset API for migrated parses
or the existing legacy OSS reader. The response proxies only bytes with the stored allowed
media type: `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/bmp`, `image/avif`, or
`image/tiff`. Other types, including SVG, are unavailable (404). Successful responses are full
200 streams; this route does not implement ranges or transformation. They carry
`Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. No upstream headers, redirects,
protected `/admin` links or presigned storage URLs are forwarded.

Null/broken/missing sources return fixed 404; upstream/storage/unexpected image-read failures
before response start return fixed 503. Cancellation propagates through relational reads,
StructaDoc and stream copying. The legacy buffered download cannot cancel the provider call,
but post-await cancellation disposes its result instead of returning success. Success, failure
and cancellation dispose the opened stream and its owning HTTP response. A failure after
response start aborts the connection; it is never swallowed into a successful truncated image.
Image authorization applies to the request snapshot. Deletion after that check can allow an
already-open stream to finish or fail; immediate byte-stream revocation is not guaranteed.
Error bodies and diagnostics contain no provider text, source paths, endpoints or credentials.
Existing admin image status/authentication and range behavior remain unchanged except safer
shared source diagnostics and stream ownership.

## Exact search and subtree filters

`GET /internal/v1/search?query=&structureNodeId=&phrase=&pageSize=&pageToken=`
uses the existing full-text search service. `query` is required, trimmed, nonempty and at most
200 characters after trimming. `phrase` defaults to `false` and accepts only `true` or `false`
(case insensitive, without whitespace). False uses the existing English stemming analyzer;
true uses the existing exact phrase analyzer. No filter preserves the existing unfiltered
search semantics. `structureNodeId` is optional and must be a canonical hyphenated UUID.
It must resolve to a node in its document's current structured parse, with a level in the
document's selected definition, before the search engine is called. Missing, stale or
incompatible nodes return the fixed resource 404. PostgreSQL resolves ownership in one
repeatable-read snapshot; the search filter combines that parse ID and the node ID. Ancestor
IDs indexed on each Block include descendants, so a Unit includes its Sections and a Section
selects the narrower subtree. No name, text or inferred page-range matching is used.

`pageSize` defaults to 20 and accepts ASCII decimal integers from 1 through 100. Omit
`pageToken` for the first page; an explicitly empty token is invalid. A token is at most
4096 characters and is canonical padded standard base64 of the existing JSON `search_after`
array `[finiteNumber,"blockId"]`. The array must have exactly two elements, and `blockId`
must be a nonblank string of at most 256 characters without control characters. It must
decode to valid Unicode; isolated or incomplete surrogate escapes are invalid. Malformed
base64, JSON, values, unknown/duplicate parameters or bounds return fixed 400 **before**
searching; an invalid token never silently restarts internal paging. Treat returned tokens
as opaque. URL-encode them (including `+`, `/` and `=`); keep the query, phrase and node filter
unchanged while traversing. The existing admin endpoint retains its token fallback behavior.

```json
{
  "success": true,
  "data": {
    "results": [
      {
        "documentName": "Textbook.pdf",
        "pageNumber": 0,
        "associatedText": "The <em>changing</em> world",
        "score": 1.25,
        "matchType": "stemmed",
        "segmentId": "00000000-0000-0000-0000-000000000006",
        "startOffset": 0,
        "endOffset": 0,
        "structurePath": "Unit 3 > Section B"
      }
    ],
    "totalCount": 1,
    "nextPageToken": null
  }
}
```

These are the complete hit fields. `pageNumber` preserves the existing **zero-based** search
convention, unlike the one-based physical page numbers on tree/content routes. Offsets retain
the existing `0` sentinel (unspecified); `matchType` is `stemmed` or `exact_phrase`.
`structurePath` is nullable. Highlight tags and all document text are untrusted data, not safe
HTML. Administrative timestamps, raw `blockData`, provider JSON, geometry, extraction metadata,
parse/upload/storage fields and credentials are omitted.

Results sort by score descending and Block ID ascending; stable data/ranking yields each hit
once across cursor pages. A full final page may provide a token followed by an empty terminal
page. The existing engine total count semantics are retained. There is no cross-request search
snapshot or query/filter identity embedded in the token. The derived index is eventually
consistent: a concurrent tree change, block synchronization or reindex can alter later pages
and can temporarily leave stale membership/path data. Relational ownership checking does not
pin the index; reindex is the recovery for stale indexed data. No ranking/index rewrite or
stronger concurrent-search guarantee is introduced.

An engine or response-read failure returns 200 with empty results, total 0 and a null token,
and records a fixed Warning without query text, request JSON, upstream body or raw exception.
The endpoint observes request cancellation before/after awaited reads, and PostgreSQL reads
receive the cancellation token. Cancellation propagates rather than becoming an empty success.
The existing search interface has no cancellation parameter: an already-sent OpenSearch read
can finish after the caller disconnects. No database, index or object-storage writes occur.

## Errors, confidentiality and rollback

| Condition | Status | Fixed error code |
| --- | --- | --- |
| Disabled, unknown internal path or unsupported method | 404 | `DOCTHECA_INTERNAL_NOT_FOUND` |
| Invalid identifier/list/filter/search/cursor input | 400 | `DOCTHECA_INTERNAL_INVALID` |
| Unknown requested definition | 404 | `DOCTHECA_INTERNAL_DEFINITION_NOT_FOUND` |
| Unknown document | 404 | `DOCTHECA_INTERNAL_DOCUMENT_NOT_FOUND` |
| Document exists without a qualifying marked parsed tree | 404 | `DOCTHECA_INTERNAL_STRUCTURE_NOT_FOUND` |
| Missing/deleted/stale node, unowned image or missing/unsupported image source | 404 | `DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND` |
| Content revision changed | 409 | `DOCTHECA_INTERNAL_STRUCTURE_CHANGED` |
| Image source/read failure before response start | 503 | `DOCTHECA_INTERNAL_IMAGE_UNAVAILABLE` |
| Database/unexpected JSON read failure on an implemented route | 500 | `http.internal_server_error` |

Business errors use `{"success":false,"errorCode":"...","message":"..."}` with fixed English
messages. Unexpected JSON failures use the existing safe ServiceMantle Problem Details boundary
(`application/problem+json`) and a correlation id. No raw input, provider error, stack trace,
document content, storage reference or credential is added to error bodies or diagnostics.
Every `/internal` path is reserved before static/SPA handling, including nested static-looking
paths and `/internal` itself; `/internalized` remains an ordinary SPA path.

No database migration, new package, storage write or port change is introduced. Disable the
setting and restart, or restore the previous image, to remove the API without data rollback.
This API does not implement network isolation or guarantee a snapshot across list requests.

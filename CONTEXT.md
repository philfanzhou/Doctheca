# Document Library

Document Library defines educational documents and their searchable parse results. It does not own Question Catalog import status or questions.

## Language

**Document File**:
A logical book or document with a display title and metadata. It may exist without a source file; only an attached real source can be parsed.
_Avoid_: Question, Upload Record

**Document Parse**:
The result of one conversion of a Document File into structured content.
_Avoid_: Document File, Import Job

**Parse Block**:
A content unit within a Document Parse that can be independently retrieved and located.
_Avoid_: Question Content, Page

**Parse Image**:
An image extracted by a Document Parse from the original document with its association preserved.
_Avoid_: Learner Image, Question Image

**Block Sequence**:
The global reading-order position of a Parse Block within its Document Parse; unlike the per-page sort index it does not restart on each page.
_Avoid_: Sort Index, Page Order

**Structure Definition**:
A kind of document structure configured by an administrator at runtime, such as "English textbook", made of Structure Levels.
_Avoid_: Document Type, Template

**Structure Level**:
One configured level of a Structure Definition, such as "Unit" or "Section", optionally under a parent level of the same definition.
_Avoid_: Heading Level, Text Level

**Structure Node**:
A custom chapter in a document-owned structure tree, at one configured Structure Level. It may explicitly bind a contiguous range of Block Sequences from one same-document Parse, or remain unbound. Node identity, sibling order and audio links survive parsing and reparsing.
_Avoid_: Chapter, Heading, Outline Item

**Structure Audio**:
An audio file owned by a document and its structure tree, optionally linked to one Structure Node. Legacy parse IDs are immutable storage provenance; content binding does not own the audio. Existing parse-scoped import remains a compatibility entry point until document audio authoring is available.
_Avoid_: Attachment, Media File, Track

**Audio Link**:
The association of a Structure Audio file with one Structure Node; pre-filled from the file's folder and file name and confirmed or changed by an administrator (#108). It is correct at node level only, never at text or exercise level.
_Avoid_: Alignment, Timestamp

# Document Library

Document Library defines educational documents and their searchable parse results. It does not own Question Catalog import status or questions.

## Language

**Document File**:
A complete document that enters the document library and serves as the source for parsing.
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
A hand-marked part of one Document Parse: a contiguous range of Block Sequences at one Structure Level, inside its parent node, such as "Unit 1 The Changing World" or "Section A".
_Avoid_: Chapter, Heading, Outline Item

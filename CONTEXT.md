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

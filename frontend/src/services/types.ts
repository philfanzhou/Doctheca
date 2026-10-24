// Shared API types for doctheca frontend.

export interface DocPagedResponse<T> {
  success: boolean
  data: T[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface ApiResponse<T> {
  success: boolean
  data: T
}

export interface SearchResult {
  documentName: string
  pageNumber: number
  associatedText: string
  score: number
  matchType: string
  segmentId: string
  startOffset: number
  endOffset: number
  createdAt: string | null
  // [Gen-2] minerU block-level fields (optional, absent when V1 zero-regression path)
  blockData?: string
  bbox?: number[]
  mineruScore?: number
  subType?: string
  textLevel?: number
  textFormat?: string
  caption?: string
  // Readable path of the hand-marked structure nodes containing the block (null when unmarked)
  structurePath?: string | null
}

export interface DocumentFile {
  id: string
  fileName: string
  contentType: string
  hasSource: boolean
  sourceState: 'structure-only' | 'with-source'
  subject: string | null
  grade: string | null
  year: string | null
  createdAt: string
  createdBy: string | null
  parseStatus: string | null
  parsedAt: string | null
}

export interface DocumentFileDetail {
  id: string
  fileName: string
  contentType: string
  hasSource: boolean
  sourceState: 'structure-only' | 'with-source'
  subject: string | null
  grade: string | null
  year: string | null
  createdAt: string
  // The runtime structure definition chosen for this document (null when none)
  structureDefinitionId?: string | null
  parses: Array<{
    id: string
    modelVersion: string
    status: string
    markdownContent: string | null
    contentList: string | null
    contentListV2: string | null
    modelJson: string | null
    layoutJson: string | null
    errorMessage: string | null
    parsedAt: string | null
    images: Array<{
      id: string
      imageName: string
      imageUrl: string
    }>
  }>
}

export interface DocumentParse {
  id: string
  fileId: string
  fileName: string
  modelVersion: string // 'vlm' or 'pipeline'
  status: string
  parsedAt: string | null
  errorMessage: string | null
}

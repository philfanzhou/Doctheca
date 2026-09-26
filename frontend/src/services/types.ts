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
}

export interface DocumentFile {
  id: string
  fileName: string
  contentType: string
  createdAt: string
  createdBy: string | null
  parseStatus: string | null
  parsedAt: string | null
}

export interface DocumentFileDetail {
  id: string
  fileName: string
  contentType: string
  createdAt: string
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

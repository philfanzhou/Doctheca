import { createAuthenticatedClient } from './authService'

export interface DocPagedResponse<T> {
  success: boolean
  data: T[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface Document {
  id: string
  title: string
  sourceType: string
  fileHash: string
  filePath: string
  fileSize: number
  language: string
  grade: string
  subject: string
  year: string
  tags: string | null
  status: string
  createdAt: string
  updatedAt: string | null
}

export interface CreateDocumentRequest {
  title: string
  subject: string
  grade: string
  year: string
  tags?: string[]
}

export interface UpdateMetadataRequest {
  subject?: string
  grade?: string
  year?: string
  tags?: string[]
}

export interface DocumentStatus {
  documentId: string
  title: string
  status: string
  jobs: Job[]
}

export interface Job {
  jobId: string
  status: string
  progress: number
  progressStage: string | null
  parserVersion: string | null
  ocrVersion: string | null
  errorMessage: string | null
  startedAt: string | null
  finishedAt: string | null
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
}

// ========== Segment Refinement Types ==========

export interface DocumentProfile {
  subject: string
  docType: string
  segmentStrategy: string
  structure: {
    hasChapters: boolean
    hasQuestions: boolean
    hasWordList: boolean
    hasFormulas: boolean
  }
}

export interface SegmentDto {
  id: string
  sentenceId: string
  segmentType: string
  text: string
  startOffset: number
  endOffset: number
  pageNumber: number
}

export interface DocumentSegmentsData {
  documentId: string
  title: string
  status: string
  profile: DocumentProfile | null
  segments: SegmentDto[]
  totalCount: number
}

export interface CorrectionDto {
  originalSentenceIds: string[]
  action: 'merge' | 'split' | 'retype' | 'splitMerge'
  newText?: string
  splitPosition?: number
  newSegmentType?: string
  mergeFirstWithPrevious?: boolean
  mergeSecondWithNext?: boolean
}

export interface RefinementResult {
  documentId: string
  backupId: string
  correctionCount: number
  message: string
}

export interface ConsistencyScanResult {
  orphanOssFiles: string[]
  brokenDocuments: Array<{
    id: string
    title: string
    filePath: string
    status: string
  }>
  summary: {
    totalDocuments: number
    brokenCount: number
    orphanCount: number
  }
}

class DocApiClient {
  private client: ReturnType<typeof createAuthenticatedClient>

  constructor() {
    this.client = createAuthenticatedClient()
  }

  async uploadDocument(
    file: File,
    title: string,
    subject: string,
    grade: string,
    year?: string,
    tags?: string[]
  ): Promise<ApiResponse<{ documentId: string; title: string; jobId: string; status: string }>> {
    const formData = new FormData()
    formData.append('file', file)
    formData.append('title', title)
    formData.append('subject', subject)
    formData.append('grade', grade)
    if (year) {
      formData.append('year', year)
    }
    if (tags && tags.length > 0) {
      formData.append('tags', JSON.stringify(tags))
    }

    const response = await this.client.post('/admin/documents/upload', formData, {
      headers: {
        'Content-Type': 'multipart/form-data'
      }
    })
    return response.data
  }

  async listDocuments(
    page: number = 1,
    pageSize: number = 20,
    status?: string,
    subject?: string,
    grade?: string,
    keyword?: string,
    year?: string
  ): Promise<DocPagedResponse<Document>> {
    const params: Record<string, unknown> = { page, pageSize }
    if (status) params.status = status
    if (subject) params.subject = subject
    if (grade) params.grade = grade
    if (keyword) params.keyword = keyword
    if (year) params.year = year

    const response = await this.client.get('/admin/documents', { params })
    return response.data
  }

  async searchTest(
    query: string,
    phrase: boolean = false,
    pageSize: number = 20,
    pageToken?: string
  ): Promise<{ results: SearchResult[]; totalCount: number; nextPageToken: string }> {
    const response = await this.client.get('/admin/documents/search-test', {
      params: { query, phrase, pageSize, pageToken }
    })
    return response.data
  }

  async getDocumentStatus(id: string): Promise<ApiResponse<DocumentStatus>> {
    const response = await this.client.get(`/admin/documents/${id}/status`)
    return response.data
  }

  async deleteDocument(title: string): Promise<ApiResponse<{ title: string; deleted: boolean }>> {
    const response = await this.client.delete(`/admin/documents/by-title/${encodeURIComponent(title)}`)
    return response.data
  }

  async updateMetadata(
    title: string,
    data: UpdateMetadataRequest
  ): Promise<ApiResponse<{ id: string; title: string; subject: string; grade: string; year: string; tags: string[] | null }>> {
    const response = await this.client.put(`/admin/documents/${title}/metadata`, data)
    return response.data
  }

  // ========== Segment Refinement ==========

  async getDocumentSegments(documentId: string): Promise<ApiResponse<DocumentSegmentsData>> {
    const response = await this.client.get(`/admin/documents/${documentId}/segments`)
    return response.data
  }

  async refineDocumentSegments(documentId: string, corrections: CorrectionDto[]): Promise<ApiResponse<RefinementResult>> {
    const response = await this.client.post(`/admin/documents/${documentId}/refine`, { corrections })
    return response.data
  }

  async scanConsistency(): Promise<ApiResponse<ConsistencyScanResult>> {
    const response = await this.client.get('/admin/documents/scan-consistency')
    return response.data
  }

  async forceDeleteDocument(id: string): Promise<ApiResponse<{ message: string }>> {
    const response = await this.client.delete(`/admin/documents/${id}/force`)
    return response.data
  }
}

export function createDocApiClient(): DocApiClient {
  return new DocApiClient()
}

export function getDocErrorMessage(error: unknown): string {
  if (error && typeof error === 'object' && 'isAxiosError' in error) {
    const axiosError = error as unknown as { response?: { data?: { message?: string } }; message: string }
    const data = axiosError.response?.data as { message?: string } | undefined
    if (data?.message) {
      return data.message
    }
    return axiosError.message
  }

  if (error instanceof Error) {
    return error.message
  }

  return 'An unknown error occurred'
}

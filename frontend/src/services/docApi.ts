import { createAuthenticatedClient } from './authService'

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
  parse: {
    id: string
    status: string
    markdownContent: string | null
    errorMessage: string | null
    parsedAt: string | null
    images: Array<{
      id: string
      imageName: string
      imageUrl: string
    }>
  } | null
}

export interface DocumentParse {
  id: string
  fileName: string
  status: string
  parsedAt: string | null
  errorMessage: string | null
}

class DocApiClient {
  private client: ReturnType<typeof createAuthenticatedClient>

  constructor() {
    this.client = createAuthenticatedClient()
  }

  async searchTest(
    query: string,
    phrase: boolean = false,
    pageSize: number = 20,
    pageToken?: string
  ): Promise<{ results: SearchResult[]; totalCount: number; nextPageToken: string }> {
    const response = await this.client.get('/admin/documents/search', {
      params: { query, phrase, pageSize, pageToken }
    })
    return response.data
  }

  // ========== Document Files (Persistent MinerU Flow) ==========

  async uploadDocumentFile(
    file: File,
    onUploadProgress?: (progressEvent: { loaded: number; total?: number }) => void
  ): Promise<ApiResponse<{ id: string; fileName: string }>> {
    const formData = new FormData()
    formData.append('file', file)
    const response = await this.client.post('/admin/document-files/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
      onUploadProgress
    })
    return response.data
  }

  async listDocumentFiles(
    page: number = 1,
    pageSize: number = 20,
    parseStatus?: string,
    fileName?: string
  ): Promise<DocPagedResponse<DocumentFile>> {
    const params: Record<string, unknown> = { page, pageSize }
    if (parseStatus) params.parseStatus = parseStatus
    if (fileName) params.fileName = fileName
    const response = await this.client.get('/admin/document-files', { params })
    return response.data
  }

  async getDocumentFile(id: string): Promise<ApiResponse<DocumentFileDetail>> {
    const response = await this.client.get(`/admin/document-files/${id}`)
    return response.data
  }

  async parseDocumentFile(id: string): Promise<ApiResponse<{ id: string; status: string }>> {
    const response = await this.client.post(`/admin/document-files/${id}/parse`)
    return response.data
  }

  async deleteDocumentFile(id: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
    const response = await this.client.delete(`/admin/document-files/${id}`)
    return response.data
  }

  getExportMarkdownUrl(id: string): string {
    return `/admin/document-files/${id}/export/markdown`
  }

  getExportHtmlUrl(id: string): string {
    return `/admin/document-files/${id}/export/html`
  }

  async exportMarkdown(id: string): Promise<{ blob: Blob; fileName: string }> {
    const response = await this.client.get(`/admin/document-files/${id}/export/markdown`, {
      responseType: 'blob'
    })
    const fileName = extractFileName(response, 'document_markdown.zip')
    return { blob: response.data as Blob, fileName }
  }

  async exportHtml(id: string): Promise<{ blob: Blob; fileName: string }> {
    const response = await this.client.get(`/admin/document-files/${id}/export/html`, {
      responseType: 'blob'
    })
    const fileName = extractFileName(response, 'document.html')
    return { blob: response.data as Blob, fileName }
  }

  // ========== Document Parses ==========

  async listDocumentParses(
    page: number = 1,
    pageSize: number = 20,
    search?: string
  ): Promise<DocPagedResponse<DocumentParse>> {
    const params: Record<string, unknown> = { page, pageSize }
    if (search) params.search = search
    const response = await this.client.get('/admin/document-parses', { params })
    return response.data
  }

  async deleteDocumentParse(parseId: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
    const response = await this.client.delete(`/admin/document-parses/${parseId}`)
    return response.data
  }

  async exportParseMarkdown(parseId: string): Promise<{ blob: Blob; fileName: string }> {
    const response = await this.client.get(`/admin/document-parses/${parseId}/export/markdown`, {
      responseType: 'blob'
    })
    const fileName = extractFileName(response, 'document_markdown.zip')
    return { blob: response.data as Blob, fileName }
  }

  async exportParseHtml(parseId: string): Promise<{ blob: Blob; fileName: string }> {
    const response = await this.client.get(`/admin/document-parses/${parseId}/export/html`, {
      responseType: 'blob'
    })
    const fileName = extractFileName(response, 'document.html')
    return { blob: response.data as Blob, fileName }
  }
}

function extractFileName(response: { headers: Record<string, unknown> }, fallback: string): string {
  const disposition = response.headers['content-disposition'] as string | undefined
  if (disposition) {
    const match = disposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/)
    if (match && match[1]) {
      return match[1].replace(/['"]/g, '')
    }
  }
  return fallback
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

import { httpClient } from './httpClient'
import type { ApiResponse, DocPagedResponse, DocumentParse } from './types'

export async function listDocumentParses(
  page: number = 1,
  pageSize: number = 20,
  search?: string,
  status?: string
): Promise<DocPagedResponse<DocumentParse>> {
  const params: Record<string, unknown> = { page, pageSize }
  if (search) params.search = search
  if (status && status !== 'all') params.status = status
  const response = await httpClient.get('/admin/document-parses', { params })
  return response.data
}

export async function deleteDocumentParse(parseId: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
  const response = await httpClient.delete(`/admin/document-parses/${parseId}`)
  return response.data
}

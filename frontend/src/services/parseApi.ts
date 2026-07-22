import axios from 'axios'
import type { ApiResponse, DocPagedResponse, DocumentParse } from './types'

const client = axios.create({ timeout: 30000 })

export async function listDocumentParses(
  page: number = 1,
  pageSize: number = 20,
  search?: string,
  status?: string
): Promise<DocPagedResponse<DocumentParse>> {
  const params: Record<string, unknown> = { page, pageSize }
  if (search) params.search = search
  if (status && status !== 'all') params.status = status
  const response = await client.get('/admin/document-parses', { params })
  return response.data
}

export async function deleteDocumentParse(parseId: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
  const response = await client.delete(`/admin/document-parses/${parseId}`)
  return response.data
}

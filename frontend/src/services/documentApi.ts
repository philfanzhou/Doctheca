import { httpClient } from './httpClient'
import type { ApiResponse, DocPagedResponse, DocumentFile, DocumentFileDetail } from './types'

export async function uploadDocumentFile(
  file: File,
  onUploadProgress?: (progressEvent: { loaded: number; total?: number }) => void
): Promise<ApiResponse<{ id: string; fileName: string }>> {
  const formData = new FormData()
  formData.append('file', file)
  const response = await httpClient.post('/admin/document-files/upload', formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
    onUploadProgress,
  })
  return response.data
}

export async function listDocumentFiles(
  page: number = 1,
  pageSize: number = 20,
  parseStatus?: string,
  fileName?: string,
  sourceState?: string
): Promise<DocPagedResponse<DocumentFile>> {
  const params: Record<string, unknown> = { page, pageSize }
  if (parseStatus) params.parseStatus = parseStatus
  if (fileName) params.fileName = fileName
  if (sourceState) params.sourceState = sourceState
  const response = await httpClient.get('/admin/document-files', { params })
  return response.data
}

export async function getDocumentFile(id: string): Promise<ApiResponse<DocumentFileDetail>> {
  const response = await httpClient.get(`/admin/document-files/${id}`)
  return response.data
}

export async function parseDocumentFile(
  id: string,
  modelVersion: string = 'vlm'
): Promise<ApiResponse<{ id: string; status: string; modelVersion: string }>> {
  const response = await httpClient.post(`/admin/document-files/${id}/parse`, null, {
    params: { modelVersion },
  })
  return response.data
}

export async function deleteDocumentFile(id: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
  const response = await httpClient.delete(`/admin/document-files/${id}`)
  return response.data
}

export interface DocumentMetadataInput {
  fileName: string
  subject: string
  grade: string
  year: string
  structureDefinitionId: string
}
export async function createBook(input: DocumentMetadataInput): Promise<ApiResponse<DocumentFileDetail>> {
  return (await httpClient.post('/admin/document-files', input)).data
}
export async function updateBook(id: string, input: DocumentMetadataInput): Promise<ApiResponse<DocumentFileDetail>> {
  return (await httpClient.put(`/admin/document-files/${id}/metadata`, input)).data
}
export async function attachFirstSource(id: string, file: File): Promise<ApiResponse<DocumentFileDetail>> {
  const form = new FormData()
  form.append('file', file)
  return (await httpClient.post(`/admin/document-files/${id}/source`, form)).data
}

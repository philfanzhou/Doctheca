import { httpClient } from './httpClient'
import type { ApiResponse } from './types'

// Runtime-configured document structure (issue #89). Level names are data, never code.

export interface StructureLevel {
  id: string
  name: string
  parentLevelId: string | null
  sortOrder: number
  required: boolean
}

export interface StructureDefinition {
  id: string
  name: string
  createdAt: string
  updatedAt: string
  levels: StructureLevel[]
}

export interface StructureLevelInput {
  id: string
  name: string
  parentLevelId: string | null
  required: boolean
}

export interface StructureNode {
  parseId?: string | null
  id: string
  levelId: string
  levelName: string | null
  name: string
  startSequence: number | null
  endSequence: number | null
  sortOrder: number
  children: StructureNode[]
}

export interface StructureTree {
  parseId: string | null
  documentFileId: string
  fileName: string
  definition: StructureDefinition | null
  nodes: StructureNode[]
}

export interface StructureNodeInput {
  id: string
  levelId: string
  name: string
  startSequence: number | null
  endSequence: number | null
  children: StructureNodeInput[]
}

export interface ParseBlock {
  id: string
  sequence: number
  pageNumber: number
  blockType: string
  subType: string | null
  content: string | null
  contentFormat: string | null
  imageId: string | null
  imageUrl: string | null
}

export interface BlockPage {
  items: ParseBlock[]
  nextAfterSequence: number | null
  hasMore: boolean
}

export interface StructureFieldError {
  field: string
  reason: string
}

export async function listStructureDefinitions(): Promise<ApiResponse<StructureDefinition[]>> {
  const response = await httpClient.get('/admin/structure-definitions')
  return response.data
}

export async function createStructureDefinition(
  name: string,
  levels: StructureLevelInput[]
): Promise<ApiResponse<StructureDefinition>> {
  const response = await httpClient.post('/admin/structure-definitions', { name, levels })
  return response.data
}

export async function updateStructureDefinition(
  id: string,
  name: string,
  levels: StructureLevelInput[]
): Promise<ApiResponse<StructureDefinition>> {
  const response = await httpClient.put(`/admin/structure-definitions/${id}`, { name, levels })
  return response.data
}

export async function deleteStructureDefinition(id: string): Promise<ApiResponse<{ id: string; deleted: boolean }>> {
  const response = await httpClient.delete(`/admin/structure-definitions/${id}`)
  return response.data
}

/** Sets ('' clears) the document's structure definition through the metadata update. */
export async function setDocumentStructureDefinition(
  documentFileId: string,
  structureDefinitionId: string
): Promise<ApiResponse<{ id: string; structureDefinitionId: string | null }>> {
  const response = await httpClient.put(`/admin/document-files/${documentFileId}/metadata`, { structureDefinitionId })
  return response.data
}

export async function getParseBlocks(
  parseId: string,
  afterSequence?: number,
  limit: number = 500
): Promise<ApiResponse<BlockPage>> {
  const params: Record<string, unknown> = { limit }
  if (afterSequence !== undefined) params.afterSequence = afterSequence
  const response = await httpClient.get(`/admin/document-parses/${parseId}/blocks`, { params })
  return response.data
}

/** Pages through every block of a parse in sequence order. */
export async function getAllParseBlocks(parseId: string): Promise<ParseBlock[]> {
  const blocks: ParseBlock[] = []
  let after: number | undefined
  for (;;) {
    const page = (await getParseBlocks(parseId, after, 1000)).data
    blocks.push(...page.items)
    if (!page.hasMore || page.nextAfterSequence === null) return blocks
    after = page.nextAfterSequence
  }
}

export async function getParseStructure(parseId: string): Promise<ApiResponse<StructureTree>> {
  const response = await httpClient.get(`/admin/document-parses/${parseId}/structure`)
  return response.data
}

export async function saveParseStructure(
  parseId: string,
  nodes: StructureNodeInput[]
): Promise<ApiResponse<StructureTree> & { searchIndexUpdated: boolean }> {
  const response = await httpClient.put(`/admin/document-parses/${parseId}/structure`, { nodes })
  return response.data
}

export async function reindexParse(parseId: string): Promise<ApiResponse<{ parseId: string; indexed: boolean }>> {
  const response = await httpClient.post(`/admin/document-parses/${parseId}/search-index`)
  return response.data
}

export async function reindexAllParses(): Promise<ApiResponse<{ total: number; indexed: number; failed: number }>> {
  const response = await httpClient.post('/admin/search-index/reindex', null, { timeout: 600000 })
  return response.data
}

/** Reads the field-level reasons of a 400 structure response, if any. */
export function getStructureFieldErrors(error: unknown): StructureFieldError[] {
  const data = (error as { response?: { data?: { errors?: unknown } } })?.response?.data
  return Array.isArray(data?.errors) ? (data!.errors as StructureFieldError[]) : []
}

/** The backend's message of a 4xx JSON envelope, falling back to the transport message. */
export function getApiMessage(error: unknown, fallback: string): string {
  const data = (error as { response?: { data?: { message?: unknown } } })?.response?.data
  if (typeof data?.message === 'string' && data.message.length > 0) return data.message
  return error instanceof Error && error.message ? error.message : fallback
}

/** RFC 4122 v4 id from getRandomValues, which unlike randomUUID also works on plain-HTTP origins. */
export function newId(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export interface DocumentNodeInput extends StructureNodeInput {
  parseId: string | null
  children: DocumentNodeInput[]
}
export async function getDocumentStructure(documentId: string): Promise<ApiResponse<StructureTree>> {
  return (await httpClient.get(`/admin/document-files/${documentId}/structure`)).data
}
export async function saveDocumentStructure(documentId: string, nodes: DocumentNodeInput[]): Promise<ApiResponse<StructureTree> & { searchIndexUpdated: boolean }> {
  return (await httpClient.put(`/admin/document-files/${documentId}/structure`, { nodes })).data
}

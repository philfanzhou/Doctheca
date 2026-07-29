import { httpClient } from './httpClient'
import type { SearchResult } from './types'

export async function searchTest(
  query: string,
  phrase: boolean = false,
  pageSize: number = 20,
  pageToken?: string,
  // [Gen-2] minerU block-level filters (all optional; undefined/null → not sent → zero regression)
  blockType?: string,
  blockSubType?: string,
  pageNumber?: number,
  textLevel?: number,
  textFormat?: string,
  parseId?: string,
  documentFileId?: string,
  hasImage?: boolean
): Promise<{ results: SearchResult[]; totalCount: number; nextPageToken: string }> {
  const params: Record<string, unknown> = { query, phrase, pageSize }
  if (pageToken) params.pageToken = pageToken
  // Only forward minerU filters that are actually set (avoid backend treating empty string as filter)
  if (blockType) params.blockType = blockType
  if (blockSubType) params.blockSubType = blockSubType
  if (pageNumber !== undefined && pageNumber !== null) params.pageNumber = pageNumber
  if (textLevel !== undefined && textLevel !== null) params.textLevel = textLevel
  if (textFormat) params.textFormat = textFormat
  if (parseId) params.parseId = parseId
  if (documentFileId) params.documentFileId = documentFileId
  if (hasImage !== undefined && hasImage !== null) params.hasImage = hasImage
  const response = await httpClient.get('/admin/documents/search', { params })
  return response.data
}

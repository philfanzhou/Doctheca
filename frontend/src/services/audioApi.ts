import { httpClient } from './httpClient'
import type { ApiResponse } from './types'

// Official audio linked to structure nodes (issue #90). Object keys and storage locations are
// only present inside a temporary playback URL, never separate metadata fields.

export interface StructureAudio {
  id: string
  sourcePath: string
  folder: string
  fileName: string
  contentType: string
  sizeBytes: number
  nodeId: string | null
  nodePath: string | null
  matchStatus: string
  linkSource: string | null
  createdAt: string
  updatedAt: string
}

export interface AudioStorageInfo {
  available: boolean
  reason: string | null
  maxFileBytes: number
  maxArchiveBytes: number
  maxFiles: number
  maxArchiveEntries: number
  maxPathLength: number
  maxSegmentLength: number
  multipartOverheadBytes: number
  extensions: string[]
}

export interface ParseAudioList {
  parseId: string
  storage: AudioStorageInfo
  items: StructureAudio[]
}

export interface AudioImportResult {
  parseId: string
  imported: number
  linked: number
  flagged: number
  items: StructureAudio[]
  skipped: string[]
}

export interface AudioFileError {
  file: string
  reason: string
}

export interface AudioLinkInput {
  id: string
  nodeId: string | null
}

export async function listParseAudio(parseId: string): Promise<ApiResponse<ParseAudioList>> {
  const response = await httpClient.get(`/admin/document-parses/${parseId}/audio`)
  return response.data
}

/** Uploads audio files and ZIP archives; each file carries its relative path (folders included). */
export async function importParseAudio(
  parseId: string,
  files: { file: File; path: string }[],
  onUploadProgress?: (progressEvent: { loaded: number; total?: number }) => void
): Promise<ApiResponse<AudioImportResult>> {
  const formData = new FormData()
  for (const { file, path } of files) {
    formData.append('files', file, file.name)
    formData.append('paths', path)
  }
  const response = await httpClient.post(`/admin/document-parses/${parseId}/audio`, formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
    timeout: 600000,
    onUploadProgress,
  })
  return response.data
}

export async function saveParseAudio(
  parseId: string,
  links: AudioLinkInput[],
  removeIds: string[]
): Promise<ApiResponse<{ parseId: string; items: StructureAudio[] }>> {
  const response = await httpClient.put(`/admin/document-parses/${parseId}/audio`, { links, removeIds })
  return response.data
}

/** The per-file reasons of a rejected import, if any. */
export function getAudioFileErrors(error: unknown): AudioFileError[] {
  const data = (error as { response?: { data?: { errors?: unknown } } })?.response?.data
  return Array.isArray(data?.errors) ? (data!.errors as AudioFileError[]) : []
}

export interface AudioPlaybackLink {
  url: string
  expiresAt: string
  expiresInSeconds: number
}

/** A fresh temporary credential for one play action; keep only in component memory. */
export async function getAudioPlaybackLink(parseId: string, audioId: string, signal: AbortSignal): Promise<ApiResponse<AudioPlaybackLink>> {
  const response = await httpClient.get(`/admin/document-parses/${parseId}/audio/${audioId}/playback-link`, { signal })
  return response.data
}

export async function listNodeAudio(parseId: string, nodeId: string): Promise<ApiResponse<{ parseId: string; nodeId: string; items: StructureAudio[] }>> {
  const response = await httpClient.get(`/admin/document-parses/${parseId}/structure/nodes/${nodeId}/audio`)
  return response.data
}

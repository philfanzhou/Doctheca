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
  requiresReview?: boolean
  reviewReason?: string | null
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
  parseId: string | null
  storage: AudioStorageInfo
  items: StructureAudio[]
}

export interface AudioImportResult {
  parseId: string | null
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

export interface AudioProposal {
  sourcePath: string
  contentType: string
  sizeBytes: number
  matchStatus: string
  suggestedNodeId: string | null
  suggestedNodePath: string | null
}
export type AudioFiles = { file: File; path: string }[]
function audioBase(id: string, document: boolean): string {
  return `/admin/${document ? 'document-files' : 'document-parses'}/${id}/audio`
}
export async function listAudio(id: string, document: boolean): Promise<ApiResponse<ParseAudioList>> {
  return (await httpClient.get(audioBase(id, document))).data
}
export async function previewAudio(id: string, document: boolean, files: AudioFiles, signal: AbortSignal,
  onUploadProgress?: (event: { loaded: number; total?: number }) => void): Promise<ApiResponse<{ items: AudioProposal[]; skipped: string[] }>> {
  return (await httpClient.post(audioBase(id, document) + '?preview=true', audioForm(files), {
    timeout: 600000, signal, onUploadProgress,
  })).data
}
function audioForm(files: AudioFiles): FormData {
  const form = new FormData()
  for (const { file, path } of files) { form.append('files', file, file.name); form.append('paths', path) }
  return form
}
export async function commitAudio(id: string, document: boolean, files: AudioFiles,
  targets: { sourcePath: string; nodeId: string | null }[], links: AudioLinkInput[], removeIds: string[], signal: AbortSignal,
  onUploadProgress?: (event: { loaded: number; total?: number }) => void): Promise<ApiResponse<AudioImportResult>> {
  const form = audioForm(files)
  form.append('targets', JSON.stringify(targets)); form.append('links', JSON.stringify(links)); form.append('removeIds', JSON.stringify(removeIds))
  return (await httpClient.post(audioBase(id, document), form, { timeout: 600000, signal, onUploadProgress })).data
}
export async function saveAudio(id: string, document: boolean, links: AudioLinkInput[], removeIds: string[], signal: AbortSignal): Promise<ApiResponse<{ items: StructureAudio[] }>> {
  return (await httpClient.put(audioBase(id, document), { links, removeIds }, { signal })).data
}
/** A fresh temporary credential for one play action; keep only in component memory. */
export async function getOwnerAudioPlaybackLink(id: string, document: boolean, audioId: string, signal: AbortSignal): Promise<ApiResponse<AudioPlaybackLink>> {
  return (await httpClient.get(`${audioBase(id, document)}/${audioId}/playback-link`, { signal })).data
}

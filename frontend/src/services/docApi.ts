// Compatibility facade for the legacy DocApiClient.
// New code may import directly from the domain modules below; this file keeps
// existing imports working by re-exporting the same names and createDocApiClient().

import * as documentApi from './documentApi'
import * as parseApi from './parseApi'
import * as searchApi from './searchApi'
import * as exportApi from './exportApi'
import { getDocErrorMessage } from './error'

export * from './types'
export * from './authApi'
export * from './documentApi'
export * from './parseApi'
export * from './searchApi'
export * from './exportApi'
export { getDocErrorMessage }

class DocApiClient {
  // Search
  searchTest = searchApi.searchTest

  // Document files
  uploadDocumentFile = documentApi.uploadDocumentFile
  listDocumentFiles = documentApi.listDocumentFiles
  getDocumentFile = documentApi.getDocumentFile
  parseDocumentFile = documentApi.parseDocumentFile
  deleteDocumentFile = documentApi.deleteDocumentFile

  // Document parses
  listDocumentParses = parseApi.listDocumentParses
  deleteDocumentParse = parseApi.deleteDocumentParse

  // Exports
  exportMarkdown = exportApi.exportMarkdown
  exportHtml = exportApi.exportHtml
  exportParseMarkdown = exportApi.exportParseMarkdown
  exportParseHtml = exportApi.exportParseHtml
}

export function createDocApiClient(): DocApiClient {
  return new DocApiClient()
}

import axios from 'axios'

function extractFileName(response: { headers: Record<string, unknown> }, fallback: string): string {
  const disposition = response.headers['content-disposition'] as string | undefined
  if (disposition) {
    const match = disposition.match(/filename[^;=\n]*=(([']).*?\2|[^;\n]*)/)
    if (match && match[1]) {
      return match[1].replace(/['"]/g, '')
    }
  }
  return fallback
}

const client = axios.create({ timeout: 30000 })

export async function exportMarkdown(id: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await client.get(`/admin/document-files/${id}/export/markdown`, {
    responseType: 'blob',
  })
  const fileName = extractFileName(response, 'document_markdown.zip')
  return { blob: response.data as Blob, fileName }
}

export async function exportHtml(id: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await client.get(`/admin/document-files/${id}/export/html`, {
    responseType: 'blob',
  })
  const fileName = extractFileName(response, 'document.html')
  return { blob: response.data as Blob, fileName }
}

export async function exportParseMarkdown(parseId: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await client.get(`/admin/document-parses/${parseId}/export/markdown`, {
    responseType: 'blob',
  })
  const fileName = extractFileName(response, 'document_markdown.zip')
  return { blob: response.data as Blob, fileName }
}

export async function exportParseHtml(parseId: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await client.get(`/admin/document-parses/${parseId}/export/html`, {
    responseType: 'blob',
  })
  const fileName = extractFileName(response, 'document.html')
  return { blob: response.data as Blob, fileName }
}

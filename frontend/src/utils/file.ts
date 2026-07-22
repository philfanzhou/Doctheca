export function fileIconCls(contentType: string): string {
  const t = contentType.toLowerCase()
  if (t.includes('pdf')) return 'pdf'
  if (t.includes('ppt')) return 'ppt'
  if (t.includes('word') || t.includes('docx') || t.includes('doc')) return 'docx'
  return 'docx'
}

export function fileExtLabel(contentType: string): string {
  const t = contentType.toLowerCase()
  if (t.includes('pdf')) return 'PDF'
  if (t.includes('ppt')) return 'PPT'
  if (t.includes('word') || t.includes('docx')) return 'DOCX'
  return 'FILE'
}

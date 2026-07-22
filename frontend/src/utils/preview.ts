// Safe preview helpers — replaces document.write with Blob URLs.
// All rendered HTML is escaped; opened windows use rel=noopener+noreferrer.

export function escHtml(s: string): string {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

export function escAttr(s: string): string {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}

function openBlobHtml(html: string): void {
  const blob = new Blob([html], { type: 'text/html;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const win = window.open(url, '_blank', 'noopener,noreferrer')
  if (win) {
    win.opener = null
  }
  // Clean up the object URL after the window has had a chance to load.
  setTimeout(() => URL.revokeObjectURL(url), 60000)
}

export function openMarkdown(fileName: string, markdownContent: string): void {
  const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(fileName)} - Markdown</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:900px;margin:0 auto;padding:24px;line-height:1.7;color:#333}pre{white-space:pre-wrap;word-break:break-word;background:#f4f4f4;padding:16px;border-radius:6px;font-size:13px}</style></head><body><h1>${escHtml(fileName)}</h1><pre>${escHtml(markdownContent)}</pre></body></html>`
  openBlobHtml(html)
}

export async function openHtmlPreview(blobPromise: Promise<Blob>): Promise<void> {
  const blob = await blobPromise
  const url = URL.createObjectURL(blob)
  const win = window.open(url, '_blank', 'noopener,noreferrer')
  if (win) {
    win.opener = null
  }
  setTimeout(() => URL.revokeObjectURL(url), 60000)
}

export function openRawJson(fileName: string, jsonName: string, rawContent: string): void {
  let formatted: string
  try {
    const parsed = JSON.parse(rawContent)
    if (Array.isArray(parsed) && parsed.length === 0) {
      throw new Error(`${jsonName} 数据为空数组。`)
    }
    formatted = JSON.stringify(parsed, null, 2)
  } catch {
    formatted = rawContent
  }
  const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(fileName)} - ${escHtml(jsonName)}</title>
<style>body{font-family:'SF Mono',Menlo,Monaco,Consolas,monospace;max-width:1200px;margin:0 auto;padding:24px;line-height:1.5;color:#333;background:#fafafa}pre{white-space:pre-wrap;word-break:break-word;background:#fff;padding:16px;border-radius:6px;font-size:12px;border:1px solid #e0e0e0}h1{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}</style></head><body><h1>${escHtml(fileName)} - ${escHtml(jsonName)}</h1><pre>${escHtml(formatted)}</pre></body></html>`
  openBlobHtml(html)
}

export function openImages(
  fileName: string,
  images: Array<{ imageName: string; imageUrl: string }>
): void {
  const imageCards = images
    .map(
      (img) =>
        `<div class="card"><a href="${escAttr(img.imageUrl)}" target="_blank" rel="noopener noreferrer"><img src="${escAttr(img.imageUrl)}" alt="${escAttr(img.imageName)}" /></a><div class="name">${escHtml(img.imageName)}</div></div>`
    )
    .join('')
  const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(fileName)} - Images</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;padding:24px;background:#f9f9f9}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px}.card{border:1px solid #ddd;border-radius:6px;overflow:hidden;background:#fff}.card img{width:100%;aspect-ratio:3/4;object-fit:cover}.card .name{padding:8px;font-size:11px;word-break:break-all;color:#666}</style></head><body><h1>${escHtml(fileName)} - 提取图片 (${images.length})</h1><div class="grid">${imageCards}</div></body></html>`
  openBlobHtml(html)
}

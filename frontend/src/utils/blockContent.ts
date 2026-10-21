// Safe rendering helpers for parse blocks. Parse results are untrusted: table HTML is never
// inserted into the page. It is parsed into an inert document (DOMParser never runs scripts or
// loads resources) and only the cells' text is read; Vue then renders that text escaped.

export interface TableCell {
  text: string
  header: boolean
  rowSpan: number
  colSpan: number
}

const span = (value: string | null) => {
  const n = Number.parseInt(value ?? '', 10)
  return Number.isFinite(n) && n > 1 && n <= 100 ? n : 1
}

// Elements whose text is code or metadata, not readable content.
const NON_TEXT = 'script, style, template, noscript'

function inertDocument(html: string): Document {
  const doc = new DOMParser().parseFromString(html, 'text/html')
  doc.querySelectorAll(NON_TEXT).forEach((element) => element.remove())
  return doc
}

/** Rows of plain-text cells from table HTML; null when the content has no table. */
export function tableRows(html: string | null): TableCell[][] | null {
  if (!html || typeof DOMParser === 'undefined') return null
  const doc = inertDocument(html)
  const table = doc.querySelector('table')
  if (!table) return null
  const rows: TableCell[][] = []
  table.querySelectorAll('tr').forEach((tr) => {
    const cells: TableCell[] = []
    tr.querySelectorAll(':scope > th, :scope > td').forEach((cell) => {
      cells.push({
        text: (cell.textContent ?? '').trim(),
        header: cell.tagName === 'TH',
        rowSpan: span(cell.getAttribute('rowspan')),
        colSpan: span(cell.getAttribute('colspan')),
      })
    })
    rows.push(cells)
  })
  return rows
}

/** Plain text of HTML content (tags dropped, never interpreted as markup). */
export function htmlToText(html: string | null): string {
  if (!html) return ''
  if (typeof DOMParser === 'undefined') return html
  return (inertDocument(html).body.textContent ?? '').trim()
}

/** Heading depth from a "heading-N" subtype, or 0 when the block is not a heading. */
export function headingLevel(subType: string | null, blockType: string): number {
  const match = /^heading-(\d+)$/i.exec(subType ?? '')
  if (match) return Math.min(Math.max(Number(match[1]), 1), 6)
  return /^(title|heading)$/i.test(blockType) ? 1 : 0
}

export function isTableBlock(blockType: string, contentFormat: string | null): boolean {
  return /table/i.test(blockType) || (contentFormat ?? '').toLowerCase() === 'html'
}

/** A short default node name taken from the first block of a selection. */
export function suggestName(content: string | null, contentFormat: string | null): string {
  const text = (contentFormat ?? '').toLowerCase() === 'html' ? htmlToText(content) : (content ?? '')
  const line = text.replace(/^[#>\s*-]+/, '').split(/\r?\n/)[0].trim()
  return line.length > 80 ? line.slice(0, 80) : line
}

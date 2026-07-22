import { escapeHtml } from '../../composables/useToast'

export function formatBlockData(raw?: string): string {
  if (!raw) return ''
  try {
    const parsed = JSON.parse(raw)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return raw
  }
}

export function extractBlockType(raw?: string): string {
  if (!raw) return ''
  try {
    const parsed = JSON.parse(raw)
    return typeof parsed === 'object' && parsed !== null && 'type' in parsed
      ? String((parsed as { type: unknown }).type)
      : ''
  } catch {
    return ''
  }
}

export function formatBbox(bbox?: number[]): string {
  if (!bbox || bbox.length < 4) return '-'
  return `[${bbox[0]}, ${bbox[1]}, ${bbox[2]}, ${bbox[3]}]`
}

export function captionPreview(caption?: string): string {
  if (!caption) return '-'
  return caption.length > 30 ? caption.slice(0, 30) + '…' : caption
}

export function matchTypeLabel(matchType: string): string {
  if (matchType === 'exact_phrase') return '精确短语'
  if (matchType === 'exact_word') return '精确词'
  if (matchType === 'stem_match') return '词干匹配'
  return matchType
}

export function matchTypeBadgeHtml(matchType: string): string {
  const label = matchTypeLabel(matchType)
  if (matchType === 'exact_phrase') return `<span class="badge green"><span class="dot"></span>${label}</span>`
  if (matchType === 'exact_word') return `<span class="badge blue"><span class="dot"></span>${label}</span>`
  return `<span class="badge amber"><span class="dot"></span>${label}</span>`
}

export function mineruScoreText(score?: number): string {
  return score !== undefined && score !== null ? score.toFixed(2) : '-'
}

export function scorePercent(score: number): number {
  // Normalize BM25 score (typical range 0-30) to 0-100% bar
  return Math.min(100, Math.max(0, (score / 30) * 100))
}

export function highlightText(text: string, query: string): string {
  if (!query) return escapeHtml(text)
  const esc = escapeHtml(text)
  const terms = query.trim().split(/\s+/).filter((t) => t.length > 0)
  if (!terms.length) return esc
  const re = new RegExp(`(${terms.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi')
  return esc.replace(re, '<mark>$1</mark>')
}

// Pure helpers of the structure audio page (issue #90): node choices, link states and the save
// payload. Level names are configured data and never appear in this file.

export interface AudioNodeSource {
  id: string
  name: string
  children: AudioNodeSource[]
}

export interface NodeOption {
  id: string
  label: string
  depth: number
}

/** Every node in reading order with its full path ("Unit 1 … > Section A"). */
export function nodeOptions(nodes: AudioNodeSource[], prefix = '', depth = 0): NodeOption[] {
  return nodes.flatMap((node) => {
    const label = prefix ? `${prefix} > ${node.name}` : node.name
    return [{ id: node.id, label, depth }, ...nodeOptions(node.children, label, depth + 1)]
  })
}

export interface AudioRow {
  id: string
  nodeId: string | null
  matchStatus: string
  linkSource: string | null
}

export type BadgeTone = 'green' | 'amber' | 'indigo' | 'gray'

/** The badge of a file as stored: how its link came about, or why it needs attention. */
export function linkState(row: AudioRow): { label: string; tone: BadgeTone; flagged: boolean } {
  if (row.linkSource === 'manual') {
    return row.nodeId ? { label: '已手动关联', tone: 'indigo', flagged: false } : { label: '已手动取消关联', tone: 'gray', flagged: false }
  }
  if (row.nodeId) return { label: '已自动匹配', tone: 'green', flagged: false }
  switch (row.matchStatus) {
    case 'folder_name_mismatch':
      return { label: '文件夹与文件名不一致', tone: 'amber', flagged: true }
    case 'ambiguous':
      return { label: '匹配不唯一', tone: 'amber', flagged: true }
    case 'node_removed':
      return { label: '原节点已删除', tone: 'amber', flagged: true }
    default:
      return { label: '未匹配到节点', tone: 'amber', flagged: true }
  }
}

/** The changed links and the removed files between the saved rows and the edited state. */
export function savePayload(
  saved: AudioRow[],
  edited: Record<string, string | null>,
  removed: Set<string>
): { links: { id: string; nodeId: string | null }[]; removeIds: string[] } {
  const links = saved
    .filter((row) => !removed.has(row.id) && row.id in edited && edited[row.id] !== row.nodeId)
    .map((row) => ({ id: row.id, nodeId: edited[row.id] }))
  return { links, removeIds: saved.filter((row) => removed.has(row.id)).map((row) => row.id) }
}

/** Browsers' folder picks include operating system metadata; the server skips it anyway. */
export function isSystemFile(path: string): boolean {
  const segments = path.split('/')
  const name = segments[segments.length - 1]
  return segments.includes('__MACOSX') || ['.ds_store', 'thumbs.db', 'desktop.ini'].includes(name.toLowerCase()) || name.startsWith('._')
}

export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) === 0 ? 0 : 1)} MiB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${bytes} B`
}

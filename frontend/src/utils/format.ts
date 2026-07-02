export function formatTime(iso: string): string {
  return new Date(iso).toLocaleString('zh-CN')
}

export function formatDate(dateVal: string | number | null | undefined): string {
  if (!dateVal && dateVal !== 0) return '-'
  try {
    let ts: number
    if (typeof dateVal === 'number') {
      ts = dateVal
    } else {
      const parsed = Number(dateVal)
      ts = isNaN(parsed) ? new Date(dateVal).getTime() / 1000 : parsed
    }
    if (ts < 10000000000) ts *= 1000
    const d = new Date(ts)
    return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
  } catch {
    return String(dateVal)
  }
}

export function getFileStatusLabel(status: string | null): string {
  const labels: Record<string, string> = {
    pending: '等待解析',
    parsing: '解析中',
    parsed: '已解析',
    failed: '解析失败',
  }
  if (status === null) return '未解析'
  return labels[status] || status
}

export function getFileStatusClass(status: string | null): string {
  if (status === 'parsed') return 'status-success'
  if (status === 'failed') return 'status-error'
  if (status === 'parsing' || status === 'pending') return 'status-processing'
  return 'status-pending'
}

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentFileDetail } from '../services/docApi'
import { formatTime, getFileStatusLabel } from '../utils/format'
import { useToast } from '../composables/useToast'
import { iconHtml } from '../utils/icons'
import { inject } from 'vue'

interface Props {
  docId?: string | null
}
const props = defineProps<Props>()

const client = createDocApiClient()
const { success: toastSuccess, error: toastError } = useToast()

const detail = ref<DocumentFileDetail | null>(null)
const loading = ref(false)
const parsing = ref(false)
const deleting = ref<string | null>(null)
const pollTimer = ref<number | null>(null)

const backToDocs = inject<() => void>('backToDocs')

async function loadDetail() {
  if (!props.docId) return
  loading.value = true
  try {
    const resp = await client.getDocumentFile(props.docId)
    detail.value = resp.data
    // Start polling when there are active parses
    const hasActive = detail.value?.parses.some(
      (p) => p.status === 'pending' || p.status === 'parsing'
    )
    if (hasActive) startPolling()
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载文档详情失败')
  } finally {
    loading.value = false
  }
}

function startPolling() {
  if (pollTimer.value !== null) return
  pollTimer.value = window.setInterval(async () => {
    if (!props.docId) return
    try {
      const resp = await client.getDocumentFile(props.docId)
      detail.value = resp.data
      const hasActive = detail.value?.parses.some(
        (p) => p.status === 'pending' || p.status === 'parsing'
      )
      if (!hasActive) stopPolling()
    } catch {
      stopPolling()
    }
  }, 5000)
}

function stopPolling() {
  if (pollTimer.value !== null) {
    clearInterval(pollTimer.value)
    pollTimer.value = null
  }
}

async function handleParse(modelVersion: string) {
  if (!props.docId) return
  parsing.value = true
  try {
    await client.parseDocumentFile(props.docId, modelVersion)
    await loadDetail()
    toastSuccess(`已触发 ${modelVersion === 'vlm' ? 'VLM' : 'Pipeline'} 解析`)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '解析请求失败')
  } finally {
    parsing.value = false
  }
}

async function handleDeleteFile() {
  if (!props.docId) return
  try {
    await ElMessageBox.confirm('确定删除此文件？所有解析记录将一并删除。', '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await client.deleteDocumentFile(props.docId)
    toastSuccess('删除成功')
    backToDocs?.()
  } catch (e) {
    toastError(e instanceof Error ? e.message : '删除失败')
  }
}

async function handleDeleteParse(parseId: string) {
  try {
    await ElMessageBox.confirm('确定删除此解析记录？原始文件不会被删除。', '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  deleting.value = parseId
  try {
    await client.deleteDocumentParse(parseId)
    await loadDetail()
    toastSuccess('删除成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '删除失败')
  } finally {
    deleting.value = null
  }
}

function fileIconCls(contentType: string): string {
  const t = contentType.toLowerCase()
  if (t.includes('pdf')) return 'pdf'
  if (t.includes('ppt')) return 'ppt'
  if (t.includes('word') || t.includes('docx') || t.includes('doc')) return 'docx'
  return 'docx'
}

function fileExtLabel(contentType: string): string {
  const t = contentType.toLowerCase()
  if (t.includes('pdf')) return 'PDF'
  if (t.includes('ppt')) return 'PPT'
  if (t.includes('word') || t.includes('docx')) return 'DOCX'
  return 'FILE'
}

function statusBadgeHtml(status: string): string {
  if (status === 'parsed') return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed') return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending') return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}

// ===== Parse preview / export actions (mirror ParseResultsPage) =====
async function openMarkdown(parseId: string) {
  if (!detail.value) return
  const parse = detail.value.parses.find((p) => p.id === parseId)
  if (!parse?.markdownContent) {
    toastError('该解析结果没有 Markdown 内容')
    return
  }
  const w = window.open('', '_blank')
  if (w) {
    w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.value.fileName)} - Markdown</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:900px;margin:0 auto;padding:24px;line-height:1.7;color:#333}pre{white-space:pre-wrap;word-break:break-word;background:#f4f4f4;padding:16px;border-radius:6px;font-size:13px}</style></head><body><h1>${escHtml(detail.value.fileName)}</h1><pre>${escHtml(parse.markdownContent)}</pre></body></html>`)
    w.document.close()
  }
}

async function openHtmlPreview(parseId: string) {
  try {
    const { blob } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '生成 HTML 失败')
  }
}

async function openRawJson(parseId: string, field: 'contentList' | 'contentListV2' | 'modelJson' | 'layoutJson', label: string) {
  if (!detail.value) return
  const parse = detail.value.parses.find((p) => p.id === parseId)
  const rawContent = parse?.[field]
  if (!rawContent || rawContent === '[]' || rawContent === 'null') {
    toastError(`该解析结果没有 ${label} 数据`)
    return
  }
  openJsonInNewWindow(detail.value.fileName, label, rawContent)
}

function openJsonInNewWindow(fileName: string, jsonName: string, rawContent: string) {
  let formatted: string
  try {
    const parsed = JSON.parse(rawContent)
    if (Array.isArray(parsed) && parsed.length === 0) {
      toastError(`${jsonName} 数据为空数组。`)
      return
    }
    formatted = JSON.stringify(parsed, null, 2)
  } catch {
    formatted = rawContent
  }
  const w = window.open('', '_blank')
  if (w) {
    w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(fileName)} - ${escHtml(jsonName)}</title>
<style>body{font-family:'SF Mono',Menlo,Monaco,Consolas,monospace;max-width:1200px;margin:0 auto;padding:24px;line-height:1.5;color:#333;background:#fafafa}pre{white-space:pre-wrap;word-break:break-word;background:#fff;padding:16px;border-radius:6px;font-size:12px;border:1px solid #e0e0e0}h1{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}</style></head><body><h1>${escHtml(fileName)} - ${escHtml(jsonName)}</h1><pre>${escHtml(formatted)}</pre></body></html>`)
    w.document.close()
  }
}

async function openImages(parseId: string) {
  if (!detail.value) return
  const parse = detail.value.parses.find((p) => p.id === parseId)
  if (!parse?.images?.length) {
    toastError('该解析结果没有图片')
    return
  }
  const w = window.open('', '_blank')
  if (w) {
    const images = parse.images
    w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.value.fileName)} - Images</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;padding:24px;background:#f9f9f9}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px}.card{border:1px solid #ddd;border-radius:6px;overflow:hidden;background:#fff}.card img{width:100%;aspect-ratio:3/4;object-fit:cover}.card .name{padding:8px;font-size:11px;word-break:break-all;color:#666}</style></head><body><h1>${escHtml(detail.value.fileName)} - 提取图片 (${images.length})</h1><div class="grid">${images.map(img => `<div class="card"><a href="${escAttr(img.imageUrl)}" target="_blank"><img src="${escAttr(img.imageUrl)}" alt="${escAttr(img.imageName)}" /></a><div class="name">${escHtml(img.imageName)}</div></div>`).join('')}</div></body></html>`)
    w.document.close()
  }
}

async function exportMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}

async function exportHtml(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseHtml(parseId)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}

function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}

function escHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

function escAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;')
}

watch(() => props.docId, () => {
  stopPolling()
  detail.value = null
  loadDetail()
})

onMounted(loadDetail)

onUnmounted(() => {
  stopPolling()
})
</script>

<template>
  <div v-loading="loading">
    <button class="back-btn" @click="backToDocs?.()">
      <span v-html="iconHtml('back')"></span>
      返回文档列表
    </button>

    <div v-if="detail" class="page-head">
      <div style="display: flex; align-items: center; gap: 14px">
        <div class="file-ico" :class="fileIconCls(detail.contentType)">{{ fileExtLabel(detail.contentType) }}</div>
        <div>
          <div class="page-title">{{ detail.fileName }}</div>
          <div class="page-sub">
            {{ detail.contentType }} · 上传于 {{ formatTime(detail.createdAt) }}
          </div>
        </div>
      </div>
      <div class="page-actions">
        <button class="btn btn-sm" :disabled="parsing" @click="handleParse('vlm')">
          <span v-html="iconHtml('zap')"></span>VLM 解析
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="parsing" @click="handleParse('pipeline')">
          <span v-html="iconHtml('refresh')"></span>Pipeline
        </button>
        <button class="btn btn-danger btn-sm" @click="handleDeleteFile">
          <span v-html="iconHtml('trash')"></span>删除文件
        </button>
      </div>
    </div>

    <div v-if="detail" class="grid-2r" style="margin-bottom: 16px">
      <div class="card">
        <div class="card-head">
          <div>
            <div class="card-title">文件信息</div>
            <div class="card-sub">原始文档元数据</div>
          </div>
        </div>
        <div style="padding: 4px 0">
          <div class="kv-row"><span class="kv-key">文件名</span><span class="kv-val">{{ detail.fileName }}</span></div>
          <div class="kv-row"><span class="kv-key">类型</span><span class="kv-val mono">{{ detail.contentType }}</span></div>
          <div class="kv-row"><span class="kv-key">上传时间</span><span class="kv-val mono">{{ formatTime(detail.createdAt) }}</span></div>
        </div>
      </div>
      <div class="card">
        <div class="card-head">
          <div>
            <div class="card-title">解析记录</div>
            <div class="card-sub">共 {{ detail.parses.length }} 条解析</div>
          </div>
        </div>
        <div style="display: flex; gap: 18px; padding: 8px 0; flex-wrap: wrap">
          <div class="stat-mini">
            <div class="stat-num" style="font-size: 22px">{{ detail.parses.filter(p => p.status === 'parsed').length }}</div>
            <div class="stat-foot">已解析</div>
          </div>
          <div class="stat-mini">
            <div class="stat-num" style="font-size: 22px">{{ detail.parses.filter(p => p.status === 'parsing' || p.status === 'pending').length }}</div>
            <div class="stat-foot">解析中</div>
          </div>
          <div class="stat-mini">
            <div class="stat-num" style="font-size: 22px">{{ detail.parses.filter(p => p.status === 'failed').length }}</div>
            <div class="stat-foot">失败</div>
          </div>
          <div class="stat-mini">
            <div class="stat-num" style="font-size: 22px">{{ detail.parses.filter(p => p.modelVersion === 'vlm').length }}</div>
            <div class="stat-foot">VLM</div>
          </div>
          <div class="stat-mini">
            <div class="stat-num" style="font-size: 22px">{{ detail.parses.filter(p => p.modelVersion === 'pipeline').length }}</div>
            <div class="stat-foot">Pipeline</div>
          </div>
        </div>
      </div>
    </div>

    <div v-if="detail" class="card">
      <div class="card-head">
        <div>
          <div class="card-title">解析记录详情</div>
          <div class="card-sub">每条记录的产物预览与导出</div>
        </div>
      </div>

      <div v-if="!detail.parses.length" class="empty">
        <span v-html="iconHtml('doc')"></span><br />
        暂无解析记录，请点击上方按钮触发解析
      </div>

      <div v-else class="parse-list">
        <div v-for="parse in detail.parses" :key="parse.id" class="parse-item">
          <div class="parse-head">
            <div class="cell-flex" style="gap: 12px">
              <span class="badge" :class="parse.modelVersion === 'pipeline' ? 'green' : 'amber'">
                {{ parse.modelVersion === 'pipeline' ? 'Pipeline' : 'VLM' }}
              </span>
              <span v-html="statusBadgeHtml(parse.status)"></span>
              <span v-if="parse.parsedAt" class="td-sub">{{ formatTime(parse.parsedAt) }}</span>
            </div>
            <button
              class="btn btn-danger btn-sm"
              :disabled="deleting === parse.id"
              @click="handleDeleteParse(parse.id)"
            >
              <span v-html="iconHtml('trash')"></span>删除
            </button>
          </div>

          <div v-if="parse.errorMessage" class="error-box">
            <span v-html="iconHtml('warnTri')"></span>
            <span>{{ parse.errorMessage }}</span>
          </div>

          <div v-if="parse.status === 'parsed'" class="parse-actions">
            <button class="btn btn-sm" @click="openMarkdown(parse.id)">
              <span v-html="iconHtml('doc')"></span>Markdown
            </button>
            <button class="btn btn-ghost btn-sm" @click="openHtmlPreview(parse.id)">
              <span v-html="iconHtml('eye')"></span>HTML 预览
            </button>
            <button class="btn btn-ghost btn-sm" @click="openRawJson(parse.id, 'contentList', 'content_list.json')">
              <span v-html="iconHtml('file')"></span>JSON
            </button>
            <button class="btn btn-ghost btn-sm" @click="openRawJson(parse.id, 'contentListV2', 'content_list_v2.json')">
              <span v-html="iconHtml('file')"></span>V2
            </button>
            <button class="btn btn-ghost btn-sm" @click="openRawJson(parse.id, 'modelJson', 'model.json')">
              <span v-html="iconHtml('file')"></span>Model
            </button>
            <button class="btn btn-ghost btn-sm" @click="openRawJson(parse.id, 'layoutJson', 'layout.json')">
              <span v-html="iconHtml('file')"></span>Layout
            </button>
            <button class="btn btn-ghost btn-sm" @click="openImages(parse.id)">
              <span v-html="iconHtml('image')"></span>图片 ({{ parse.images?.length || 0 }})
            </button>
            <button class="btn btn-ghost btn-sm" @click="exportMarkdown(parse.id)">
              <span v-html="iconHtml('upload')"></span>导出 MD
            </button>
            <button class="btn btn-ghost btn-sm" @click="exportHtml(parse.id)">
              <span v-html="iconHtml('upload')"></span>导出 HTML
            </button>
          </div>

          <div v-else-if="parse.status === 'parsing' || parse.status === 'pending'" class="parsing-box">
            <div class="progress-track" style="max-width: 100%">
              <div class="progress-fill parsing" style="width: 100%"></div>
            </div>
            <div class="td-sub" style="margin-top: 6px">MinerU 正在解析，5 秒后自动刷新…</div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.kv-row {
  display: flex;
  padding: 7px 0;
  font-size: 13px;
  border-bottom: 1px solid var(--border-2);
}

.kv-row:last-child {
  border-bottom: none;
}

.kv-key {
  width: 90px;
  color: var(--text-3);
  flex-shrink: 0;
}

.kv-val {
  color: var(--text);
  word-break: break-all;
}

.stat-mini {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}

.parse-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.parse-item {
  border: 1px solid var(--border-2);
  border-radius: 10px;
  padding: 14px;
  background: var(--surface);
}

.parse-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}

.parse-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.error-box {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #fef2f2;
  border: 1px solid #fecaca;
  color: #b91c1c;
  padding: 8px 12px;
  border-radius: 8px;
  font-size: 12.5px;
  margin-bottom: 10px;
}

.error-box :deep(svg) {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
}

.parsing-box {
  margin-top: 4px;
}

.progress-track {
  height: 6px;
  background: var(--surface-2);
  border-radius: 6px;
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  background: var(--primary);
  border-radius: 6px;
  transition: width 0.3s var(--ease);
}

.progress-fill.parsing {
  background: linear-gradient(90deg, var(--primary) 0%, #6366f1 50%, var(--primary) 100%);
  background-size: 200% 100%;
  animation: progPulse 1.4s var(--ease) infinite;
}

@keyframes progPulse {
  0% { background-position: 0% 0%; }
  100% { background-position: -200% 0%; }
}

.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

.page-actions :deep(svg),
.parse-head :deep(svg),
.parse-actions :deep(svg),
.error-box :deep(svg),
.back-btn :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}

.back-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--text-2);
  margin-bottom: 14px;
  transition: color 0.15s;
  background: none;
  border: none;
  cursor: pointer;
  padding: 0;
}

.back-btn:hover {
  color: var(--primary);
}

.back-btn :deep(svg) {
  width: 16px;
  height: 16px;
}
</style>

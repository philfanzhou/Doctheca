<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { createDocApiClient, type DocumentParse } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusClass } from '../utils/format'

const client = createDocApiClient()

const parseList = ref<DocumentParse[]>([])
const parseTotal = ref(0)
const parsePage = ref(1)
const parsePageSize = ref(20)
const parseSearch = ref('')
const parseDeleting = ref<string | null>(null)

const parseTotalPages = computed(() => Math.ceil(parseTotal.value / parsePageSize.value))

async function loadParseList() {
  try {
    const response = await client.listDocumentParses(parsePage.value, parsePageSize.value, parseSearch.value || undefined)
    parseList.value = response.data as DocumentParse[]
    parseTotal.value = response.total
  } catch (e) {
    console.error('Failed to load parse list', e)
  }
}

function handleParsePageChange(newPage: number) {
  parsePage.value = newPage
  loadParseList()
}

async function handleDeleteParse(parseId: string) {
  if (!confirm('确定删除此解析记录？原始文件不会被删除。')) return
  parseDeleting.value = parseId
  try {
    await client.deleteDocumentParse(parseId)
    await loadParseList()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Delete failed'
    alert(msg)
  } finally {
    parseDeleting.value = null
  }
}

// Open Markdown view in new tab — fetches file detail, writes markdown to new window
async function openMarkdown(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    if (!parse?.markdownContent) {
      alert('该解析结果没有 Markdown 内容')
      return
    }
    const w = window.open('', '_blank')
    if (w) {
      w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.fileName)} - Markdown</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:900px;margin:0 auto;padding:24px;line-height:1.7;color:#333}pre{white-space:pre-wrap;word-break:break-word;background:#f4f4f4;padding:16px;border-radius:6px;font-size:13px}</style></head><body><h1>${escHtml(detail.fileName)}</h1><pre>${escHtml(parse.markdownContent)}</pre></body></html>`)
      w.document.close()
    }
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Failed to load markdown')
  }
}

// Open HTML preview in new tab
async function openHtmlPreview(parseId: string) {
  try {
    const { blob } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank')
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Failed to generate HTML')
  }
}

// Open content_list.json in new tab
async function openJson(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    const rawContent = parse?.contentList
    if (!rawContent || rawContent === '[]' || rawContent === 'null') {
      alert('该解析结果没有结构化 JSON 数据。Pipeline 模型的解析结果才包含 content_list.json。')
      return
    }
    let formatted: string
    try {
      const parsed = JSON.parse(rawContent)
      if (Array.isArray(parsed) && parsed.length === 0) {
        alert('该解析结果的结构化数据为空数组。Pipeline 模型的解析结果才包含有效的 content_list.json。')
        return
      }
      formatted = JSON.stringify(parsed, null, 2)
    } catch {
      formatted = rawContent
    }
    const w = window.open('', '_blank')
    if (w) {
      w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.fileName)} - JSON</title>
<style>body{font-family:'SF Mono',Menlo,Monaco,Consolas,monospace;max-width:1200px;margin:0 auto;padding:24px;line-height:1.5;color:#333;background:#fafafa}pre{white-space:pre-wrap;word-break:break-word;background:#fff;padding:16px;border-radius:6px;font-size:12px;border:1px solid #e0e0e0}h1{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}</style></head><body><h1>${escHtml(detail.fileName)} - content_list.json</h1><pre>${escHtml(formatted)}</pre></body></html>`)
      w.document.close()
    }
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Failed to load JSON data')
  }
}

// Open Layout PDF in new tab
async function openLayoutPdf(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    if (!parse?.layoutPdfUrl) {
      alert('该解析结果没有 Layout PDF。Pipeline 模型的解析结果才包含 layout.pdf。')
      return
    }
    window.open(parse.layoutPdfUrl, '_blank')
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Failed to load Layout PDF')
  }
}

// Open images gallery in new tab
async function openImages(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    if (!parse?.images?.length) {
      alert('该解析结果没有图片')
      return
    }
    const w = window.open('', '_blank')
    if (w) {
      const images = parse.images
      w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.fileName)} - Images</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;padding:24px;background:#f9f9f9}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px}.card{border:1px solid #ddd;border-radius:6px;overflow:hidden;background:#fff}.card img{width:100%;aspect-ratio:3/4;object-fit:cover}.card .name{padding:8px;font-size:11px;word-break:break-all;color:#666}</style></head><body><h1>${escHtml(detail.fileName)} - 提取图片 (${images.length})</h1><div class="grid">${images.map(img => `<div class="card"><a href="${escAttr(img.imageUrl)}" target="_blank"><img src="${escAttr(img.imageUrl)}" alt="${escAttr(img.imageName)}" /></a><div class="name">${escHtml(img.imageName)}</div></div>`).join('')}</div></body></html>`)
      w.document.close()
    }
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Failed to load images')
  }
}

// Export Markdown ZIP
async function exportMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    downloadBlob(blob, fileName)
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Export failed')
  }
}

// Export HTML
async function exportHtml(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseHtml(parseId)
    downloadBlob(blob, fileName)
  } catch (e) {
    alert(e instanceof Error ? e.message : 'Export failed')
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

onMounted(() => {
  loadParseList()
})
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">解析结果</h1>
    <p class="page-subtitle">查看所有文档的解析记录和数据</p>
  </div>

  <div class="card">
    <div class="card-header">
      <span>解析记录</span>
      <div style="display: flex; align-items: center; gap: 8px">
        <div class="input-wrap" style="width: 200px">
          <input v-model="parseSearch" type="text" placeholder="搜索文档名..." @keyup.enter="parsePage = 1; loadParseList()" />
        </div>
        <button class="btn btn-secondary btn-small" @click="parsePage = 1; loadParseList()">搜索</button>
      </div>
    </div>
    <div class="card-body">
      <table v-if="parseList.length > 0" class="data-table">
        <thead>
          <tr>
            <th>文件名</th>
            <th>模型</th>
            <th>状态</th>
            <th>解析时间</th>
            <th>错误信息</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in parseList" :key="p.id">
            <td>{{ p.fileName }}</td>
            <td>
              <span class="status-badge" :class="p.modelVersion === 'pipeline' ? 'status-parsed' : 'status-parsing'">
                {{ p.modelVersion === 'pipeline' ? 'Pipeline' : 'VLM' }}
              </span>
            </td>
            <td>
              <span class="status-badge" :class="getFileStatusClass(p.status)">{{ getFileStatusLabel(p.status) }}</span>
            </td>
            <td>{{ p.parsedAt ? formatTime(p.parsedAt) : '-' }}</td>
            <td style="max-width: 200px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap" :title="p.errorMessage || ''">{{ p.errorMessage || '-' }}</td>
            <td>
              <div v-if="p.status === 'parsed'" style="display: flex; gap: 6px; flex-wrap: wrap">
                <button class="btn btn-primary btn-small" @click="openMarkdown(p.id, p.fileId)">MD</button>
                <button class="btn btn-secondary btn-small" @click="openHtmlPreview(p.id)">HTML</button>
                <button v-if="p.modelVersion === 'pipeline'" class="btn btn-secondary btn-small" @click="openJson(p.id, p.fileId)">JSON</button>
                <button v-if="p.modelVersion === 'pipeline'" class="btn btn-secondary btn-small" @click="openLayoutPdf(p.id, p.fileId)">Layout</button>
                <button class="btn btn-secondary btn-small" @click="openImages(p.id, p.fileId)">图片</button>
                <button class="btn btn-secondary btn-small" @click="exportMarkdown(p.id)">导出MD</button>
                <button class="btn btn-secondary btn-small" @click="exportHtml(p.id)">导出HTML</button>
              </div>
              <button class="btn btn-secondary btn-small" style="color: var(--danger)" :disabled="parseDeleting === p.id" @click="handleDeleteParse(p.id)">
                {{ parseDeleting === p.id ? '删除中...' : '删除' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无解析记录</div>

      <div v-if="parseTotal > parsePageSize" class="pagination-bar">
        <span class="pagination-info">共 {{ parseTotal }} 条</span>
        <button class="page-btn" :disabled="parsePage <= 1" @click="handleParsePageChange(parsePage - 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
        </button>
        <button v-for="p in parseTotalPages" :key="p" class="page-btn" :class="{ active: parsePage === p }" @click="handleParsePageChange(p)">{{ p }}</button>
        <button class="page-btn" :disabled="parsePage >= parseTotalPages" @click="handleParsePageChange(parsePage + 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6" /></svg>
        </button>
      </div>
    </div>
  </div>
</template>

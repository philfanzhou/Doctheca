<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentParse } from '../services/docApi'
import { formatTime, getFileStatusLabel } from '../utils/format'
import { useToast } from '../composables/useToast'
import { iconHtml } from '../utils/icons'

const client = createDocApiClient()
const { success: toastSuccess, error: toastError } = useToast()

const parseList = ref<DocumentParse[]>([])
const parseTotal = ref(0)
const parsePage = ref(1)
const parsePageSize = ref(20)
const parseSearch = ref('')
const parseDeleting = ref<string | null>(null)
const listLoading = ref(false)
const filterStatus = ref<string>('all')

async function loadParseList() {
  listLoading.value = true
  try {
    const response = await client.listDocumentParses(parsePage.value, parsePageSize.value, parseSearch.value || undefined)
    parseList.value = response.data as DocumentParse[]
    parseTotal.value = response.total
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载解析记录失败')
  } finally {
    listLoading.value = false
  }
}

function handleParsePageChange(newPage: number) {
  parsePage.value = newPage
  loadParseList()
}

function handleParsePageSizeChange(newSize: number) {
  parsePageSize.value = newSize
  parsePage.value = 1
  loadParseList()
}

async function handleDeleteParse(parseId: string) {
  try {
    await ElMessageBox.confirm('确定删除此解析记录？原始文件不会被删除。', '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }
  parseDeleting.value = parseId
  try {
    await client.deleteDocumentParse(parseId)
    if (parseList.value.length <= 1 && parsePage.value > 1) {
      parsePage.value--
    }
    await loadParseList()
    toastSuccess('删除成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '删除失败'
    toastError(msg)
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
      toastError('该解析结果没有 Markdown 内容')
      return
    }
    const w = window.open('', '_blank')
    if (w) {
      w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.fileName)} - Markdown</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:900px;margin:0 auto;padding:24px;line-height:1.7;color:#333}pre{white-space:pre-wrap;word-break:break-word;background:#f4f4f4;padding:16px;border-radius:6px;font-size:13px}</style></head><body><h1>${escHtml(detail.fileName)}</h1><pre>${escHtml(parse.markdownContent)}</pre></body></html>`)
      w.document.close()
    }
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载 Markdown 失败')
  }
}

// Open HTML preview in new tab
async function openHtmlPreview(parseId: string) {
  try {
    const { blob } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '生成 HTML 失败')
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
      toastError('该解析结果没有结构化 JSON 数据(content_list.json)。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'content_list.json', rawContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载 JSON 数据失败')
  }
}

// Open content_list_v2.json in new tab
async function openContentListV2(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    const rawContent = parse?.contentListV2
    if (!rawContent || rawContent === '[]' || rawContent === 'null') {
      toastError('该解析结果没有 content_list_v2.json 数据。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'content_list_v2.json', rawContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载数据失败')
  }
}

// Open model.json in new tab
async function openModelJson(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    const rawContent = parse?.modelJson
    if (!rawContent || rawContent === 'null') {
      toastError('该解析结果没有 model.json 数据。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'model.json', rawContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载数据失败')
  }
}

// Open layout.json in new tab
async function openLayoutJson(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    const rawContent = parse?.layoutJson
    if (!rawContent || rawContent === 'null') {
      toastError('该解析结果没有 layout.json 数据（版面分析数据）。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'layout.json', rawContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载数据失败')
  }
}

// Shared helper: open formatted JSON in new window
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

// Open images gallery in new tab
async function openImages(parseId: string, fileId: string) {
  try {
    const response = await client.getDocumentFile(fileId)
    const detail = response.data
    const parse = detail.parses?.find(p => p.id === parseId)
    if (!parse?.images?.length) {
      toastError('该解析结果没有图片')
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
    toastError(e instanceof Error ? e.message : '加载图片失败')
  }
}

// Export Markdown ZIP
async function exportMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}

// Export HTML
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

const stripItems = computed(() => {
  const all = parseTotal.value
  const parsed = parseList.value.filter((p) => p.status === 'parsed').length
  const parsing = parseList.value.filter((p) => p.status === 'parsing' || p.status === 'pending').length
  const failed = parseList.value.filter((p) => p.status === 'failed').length
  return [
    { key: 'all', label: '全部记录', count: all, color: '#4F46E5' },
    { key: 'parsed', label: '已解析', count: parsed, color: '#10B981' },
    { key: 'parsing', label: '解析中', count: parsing, color: '#0EA5E9' },
    { key: 'failed', label: '失败', count: failed, color: '#EF4444' },
  ]
})

function selectStrip(key: string) {
  filterStatus.value = key
}

function statusBadgeHtml(status: string): string {
  if (status === 'parsed') return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed') return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending') return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}

function modelBadgeHtml(modelVersion: string): string {
  if (modelVersion === 'pipeline') return `<span class="badge green">Pipeline</span>`
  return `<span class="badge amber">VLM</span>`
}

onMounted(() => {
  loadParseList()
})
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">解析结果</div>
        <div class="page-sub">查看所有文档的解析记录与产物</div>
      </div>
      <div class="page-actions">
        <button class="btn btn-ghost btn-sm" @click="loadParseList">
          <span v-html="iconHtml('refresh')"></span>刷新
        </button>
      </div>
    </div>

    <div class="strip">
      <button
        v-for="item in stripItems"
        :key="item.key"
        class="strip-item"
        :class="{ active: filterStatus === item.key }"
        @click="selectStrip(item.key)"
      >
        <span class="strip-dot" :style="{ background: item.color }"></span>
        <span>
          <span class="strip-num">{{ item.count }}</span><br />
          <span class="strip-label">{{ item.label }}</span>
        </span>
      </button>
    </div>

    <div class="card section-gap">
      <div class="card-head">
        <div>
          <div class="card-title">解析记录</div>
          <div class="card-sub">共 {{ parseTotal }} 条记录</div>
        </div>
        <div style="position: relative; width: 240px">
          <span
            v-html="iconHtml('search')"
            style="position: absolute; left: 10px; top: 9px; color: var(--text-3); width: 15px; height: 15px"
          ></span>
          <input
            v-model="parseSearch"
            class="input"
            style="padding-left: 32px"
            placeholder="搜索文档名…"
            @keyup.enter="parsePage = 1; loadParseList()"
          />
        </div>
      </div>

      <div v-loading="listLoading" style="min-height: 200px">
        <div class="table-wrap">
          <table class="data-table">
            <thead>
              <tr>
                <th>文件名</th>
                <th>模型</th>
                <th>状态</th>
                <th>解析时间</th>
                <th>错误信息</th>
                <th style="text-align: right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in parseList" :key="row.id">
                <td>
                  <div class="cell-flex">
                    <div class="file-ico docx">DOC</div>
                    <div>
                      <div class="td-main" style="max-width: 360px">{{ row.fileName }}</div>
                      <div class="td-sub mono">{{ row.id.slice(0, 8) }}…</div>
                    </div>
                  </div>
                </td>
                <td><span v-html="modelBadgeHtml(row.modelVersion)"></span></td>
                <td><span v-html="statusBadgeHtml(row.status)"></span></td>
                <td class="mono" style="color: var(--text-3); font-size: 12.5px">
                  {{ row.parsedAt ? formatTime(row.parsedAt) : '-' }}
                </td>
                <td style="max-width: 260px">
                  <span v-if="row.errorMessage" class="error-text">{{ row.errorMessage }}</span>
                  <span v-else style="color: var(--text-3)">-</span>
                </td>
                <td style="text-align: right; white-space: nowrap">
                  <template v-if="row.status === 'parsed'">
                    <button class="btn btn-sm" @click="openMarkdown(row.id, row.fileId)">MD</button>
                    <button class="btn btn-ghost btn-sm" @click="openHtmlPreview(row.id)">HTML</button>
                    <button class="btn btn-ghost btn-sm" @click="openJson(row.id, row.fileId)">JSON</button>
                    <button class="btn btn-ghost btn-sm" @click="openContentListV2(row.id, row.fileId)">V2</button>
                    <button class="btn btn-ghost btn-sm" @click="openModelJson(row.id, row.fileId)">Model</button>
                    <button class="btn btn-ghost btn-sm" @click="openLayoutJson(row.id, row.fileId)">Layout</button>
                    <button class="btn btn-ghost btn-sm" @click="openImages(row.id, row.fileId)">图片</button>
                    <button class="btn btn-ghost btn-sm" @click="exportMarkdown(row.id)">导出MD</button>
                    <button class="btn btn-ghost btn-sm" @click="exportHtml(row.id)">导出HTML</button>
                  </template>
                  <button
                    class="btn btn-danger btn-sm"
                    :disabled="parseDeleting === row.id"
                    @click="handleDeleteParse(row.id)"
                  >
                    <span v-html="iconHtml('trash')"></span>删除
                  </button>
                </td>
              </tr>
              <tr v-if="!parseList.length && !listLoading">
                <td colspan="6">
                  <div class="empty">
                    <span v-html="iconHtml('doc')"></span><br />
                    暂无解析记录
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="parseTotal > 0" class="pager">
        <span class="total">共 {{ parseTotal }} 条记录</span>
        <el-pagination
          v-model:current-page="parsePage"
          v-model:page-size="parsePageSize"
          :total="parseTotal"
          :page-sizes="[20, 50, 100]"
          layout="sizes, prev, pager, next, jumper"
          background
          @current-change="handleParsePageChange"
          @size-change="handleParsePageSizeChange"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.page-actions :deep(svg),
.btn :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}

.error-text {
  color: #b91c1c;
  font-size: 12.5px;
  display: inline-block;
  max-width: 260px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: middle;
}
</style>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Search } from '@element-plus/icons-vue'
import { createDocApiClient, type DocumentParse } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusType } from '../utils/format'

const client = createDocApiClient()

const parseList = ref<DocumentParse[]>([])
const parseTotal = ref(0)
const parsePage = ref(1)
const parsePageSize = ref(20)
const parseSearch = ref('')
const parseDeleting = ref<string | null>(null)
const listLoading = ref(false)

async function loadParseList() {
  listLoading.value = true
  try {
    const response = await client.listDocumentParses(parsePage.value, parsePageSize.value, parseSearch.value || undefined)
    parseList.value = response.data as DocumentParse[]
    parseTotal.value = response.total
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载解析记录失败')
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
    await loadParseList()
    ElMessage.success('删除成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '删除失败'
    ElMessage.error(msg)
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
      ElMessage.warning('该解析结果没有 Markdown 内容')
      return
    }
    const w = window.open('', '_blank')
    if (w) {
      w.document.write(`<!DOCTYPE html><html><head><meta charset="utf-8"><title>${escHtml(detail.fileName)} - Markdown</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:900px;margin:0 auto;padding:24px;line-height:1.7;color:#333}pre{white-space:pre-wrap;word-break:break-word;background:#f4f4f4;padding:16px;border-radius:6px;font-size:13px}</style></head><body><h1>${escHtml(detail.fileName)}</h1><pre>${escHtml(parse.markdownContent)}</pre></body></html>`)
      w.document.close()
    }
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载 Markdown 失败')
  }
}

// Open HTML preview in new tab
async function openHtmlPreview(parseId: string) {
  try {
    const { blob } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '生成 HTML 失败')
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
      ElMessage.warning('该解析结果没有结构化 JSON 数据(content_list.json)。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'content_list.json', rawContent)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载 JSON 数据失败')
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
      ElMessage.warning('该解析结果没有 content_list_v2.json 数据。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'content_list_v2.json', rawContent)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载数据失败')
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
      ElMessage.warning('该解析结果没有 model.json 数据。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'model.json', rawContent)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载数据失败')
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
      ElMessage.warning('该解析结果没有 layout.json 数据（版面分析数据）。')
      return
    }
    openJsonInNewWindow(detail.fileName, 'layout.json', rawContent)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '加载数据失败')
  }
}

// Shared helper: open formatted JSON in new window
function openJsonInNewWindow(fileName: string, jsonName: string, rawContent: string) {
  let formatted: string
  try {
    const parsed = JSON.parse(rawContent)
    if (Array.isArray(parsed) && parsed.length === 0) {
      ElMessage.warning(`${jsonName} 数据为空数组。`)
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
      ElMessage.warning('该解析结果没有图片')
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
    ElMessage.error(e instanceof Error ? e.message : '加载图片失败')
  }
}

// Export Markdown ZIP
async function exportMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    downloadBlob(blob, fileName)
    ElMessage.success('导出成功')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '导出失败')
  }
}

// Export HTML
async function exportHtml(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseHtml(parseId)
    downloadBlob(blob, fileName)
    ElMessage.success('导出成功')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '导出失败')
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

  <el-card shadow="never" class="page-card">
    <template #header>
      <div class="card-header">
        <span>解析记录</span>
        <div class="card-header-actions">
          <el-input
            v-model="parseSearch"
            :prefix-icon="Search"
            placeholder="搜索文档名..."
            clearable
            style="width: 220px"
            @keyup.enter="parsePage = 1; loadParseList()"
          />
          <el-button size="small" @click="parsePage = 1; loadParseList()">搜索</el-button>
        </div>
      </div>
    </template>

    <el-table
      v-loading="listLoading"
      :data="parseList"
      stripe
      border
      empty-text="暂无解析记录"
    >
      <el-table-column prop="fileName" label="文件名" min-width="200" show-overflow-tooltip />
      <el-table-column label="模型" width="110">
        <template #default="{ row }">
          <el-tag :type="row.modelVersion === 'pipeline' ? 'success' : 'warning'" size="small">
            {{ row.modelVersion === 'pipeline' ? 'Pipeline' : 'VLM' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="状态" width="110">
        <template #default="{ row }">
          <el-tag :type="getFileStatusType(row.status)" size="small">{{ getFileStatusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="解析时间" width="170">
        <template #default="{ row }">{{ row.parsedAt ? formatTime(row.parsedAt) : '-' }}</template>
      </el-table-column>
      <el-table-column label="错误信息" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">{{ row.errorMessage || '-' }}</template>
      </el-table-column>
      <el-table-column label="操作" width="440">
        <template #default="{ row }">
          <div v-if="row.status === 'parsed'" class="action-group">
            <el-button type="primary" size="small" @click="openMarkdown(row.id, row.fileId)">MD</el-button>
            <el-button size="small" @click="openHtmlPreview(row.id)">HTML</el-button>
            <el-button size="small" @click="openJson(row.id, row.fileId)">JSON</el-button>
            <el-button size="small" @click="openContentListV2(row.id, row.fileId)">V2</el-button>
            <el-button size="small" @click="openModelJson(row.id, row.fileId)">Model</el-button>
            <el-button size="small" @click="openLayoutJson(row.id, row.fileId)">Layout</el-button>
            <el-button size="small" @click="openImages(row.id, row.fileId)">图片</el-button>
            <el-button size="small" @click="exportMarkdown(row.id)">导出MD</el-button>
            <el-button size="small" @click="exportHtml(row.id)">导出HTML</el-button>
          </div>
          <el-button
            type="danger"
            size="small"
            text
            :loading="parseDeleting === row.id"
            @click="handleDeleteParse(row.id)"
          >
            删除
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <div v-if="parseTotal > 0" class="pagination-wrap">
      <el-pagination
        v-model:current-page="parsePage"
        v-model:page-size="parsePageSize"
        :total="parseTotal"
        :page-sizes="[20, 50, 100]"
        layout="total, sizes, prev, pager, next, jumper"
        background
        @current-change="handleParsePageChange"
        @size-change="handleParsePageSizeChange"
      />
    </div>
  </el-card>
</template>

<style scoped>
.page-card {
  border-radius: 8px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-weight: 600;
  font-size: 13px;
}

.card-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.action-group {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
  margin-bottom: 4px;
}

.pagination-wrap {
  margin-top: 12px;
  display: flex;
  justify-content: flex-end;
}
</style>

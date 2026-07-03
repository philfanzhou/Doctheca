<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { createDocApiClient, type DocumentParse, type DocumentFileDetail } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusClass } from '../utils/format'

const client = createDocApiClient()

// List mode state
const parseList = ref<DocumentParse[]>([])
const parseTotal = ref(0)
const parsePage = ref(1)
const parsePageSize = ref(20)
const parseSearch = ref('')
const parseDeleting = ref<string | null>(null)

// Detail mode state
const viewingParseId = ref<string | null>(null)
const fileDetail = ref<DocumentFileDetail | null>(null)
const detailLoading = ref(false)
const detailError = ref('')

// Detail tabs
type DetailTab = 'markdown' | 'html' | 'layout' | 'images'
const activeDetailTab = ref<DetailTab>('markdown')

// HTML preview URL (generated from exportParseHtml blob)
const htmlPreviewUrl = ref<string | null>(null)

const parseTotalPages = computed(() => Math.ceil(parseTotal.value / parsePageSize.value))
const isDetailView = computed(() => !!viewingParseId.value)

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

async function handleViewDetail(parseId: string, fileId?: string) {
  viewingParseId.value = parseId
  activeDetailTab.value = 'markdown'
  detailLoading.value = true
  detailError.value = ''
  fileDetail.value = null
  htmlPreviewUrl.value = null

  if (!fileId) {
    detailError.value = '无法获取关联的文件信息'
    detailLoading.value = false
    return
  }

  try {
    const response = await client.getDocumentFile(fileId)
    fileDetail.value = response.data
  } catch (e: unknown) {
    detailError.value = e instanceof Error ? e.message : 'Failed to load details'
  } finally {
    detailLoading.value = false
  }
}

// Watch tab changes to load HTML preview on demand
watch(activeDetailTab, async (newTab) => {
  if (newTab === 'html' && viewingParseId.value) {
    htmlPreviewUrl.value = null
    try {
      const { blob } = await client.exportParseHtml(viewingParseId.value)
      htmlPreviewUrl.value = URL.createObjectURL(blob)
    } catch (e) {
      console.error('Failed to generate HTML preview', e)
    }
  }
})

function goBackToList() {
  viewingParseId.value = null
  fileDetail.value = null
  if (htmlPreviewUrl.value) {
    URL.revokeObjectURL(htmlPreviewUrl.value)
    htmlPreviewUrl.value = null
  }
}

async function handleExportParseMarkdown() {
  if (!viewingParseId.value) return
  try {
    const { blob, fileName } = await client.exportParseMarkdown(viewingParseId.value)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch (e: unknown) {
    alert(e instanceof Error ? e.message : 'Export failed')
  }
}

async function handleExportParseHtml() {
  if (!viewingParseId.value) return
  try {
    const { blob, fileName } = await client.exportParseHtml(viewingParseId.value)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch (e: unknown) {
    alert(e instanceof Error ? e.message : 'Export failed')
  }
}

function handlePreviewHtml() {
  if (htmlPreviewUrl.value) {
    window.open(htmlPreviewUrl.value, '_blank')
  }
}

onMounted(() => {
  loadParseList()
})
</script>

<template>
  <div v-if="!isDetailView">
    <!-- List Mode -->
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
                <span class="status-badge" :class="getFileStatusClass(p.status)">{{ getFileStatusLabel(p.status) }}</span>
              </td>
              <td>{{ p.parsedAt ? formatTime(p.parsedAt) : '-' }}</td>
              <td style="max-width: 200px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap" :title="p.errorMessage || ''">{{ p.errorMessage || '-' }}</td>
              <td>
                <div style="display: flex; gap: 6px">
                  <button class="btn btn-primary btn-small" :disabled="p.status !== 'parsed'" @click="handleViewDetail(p.id, p.fileId)">查看</button>
                  <button class="btn btn-secondary btn-small" style="color: var(--danger)" :disabled="parseDeleting === p.id" @click="handleDeleteParse(p.id)">
                    {{ parseDeleting === p.id ? '删除中...' : '删除' }}
                  </button>
                </div>
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
  </div>

  <!-- Detail Mode -->
  <div v-else>
    <div class="page-header">
      <div style="display: flex; align-items: center; gap: 12px">
        <button class="btn btn-secondary btn-small" @click="goBackToList">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
          返回列表
        </button>
        <div>
          <h1 class="page-title" style="margin-bottom: 0">解析详情</h1>
          <p class="page-subtitle" v-if="fileDetail">{{ fileDetail.fileName }}</p>
        </div>
      </div>
    </div>

    <div v-if="detailLoading" style="text-align: center; padding: 60px; color: var(--text-muted)">加载中...</div>
    <div v-else-if="detailError" style="text-align: center; padding: 60px; color: var(--danger)">{{ detailError }}</div>
    <div v-else-if="!fileDetail" style="text-align: center; padding: 60px; color: var(--text-muted)">未找到数据</div>

    <template v-else-if="fileDetail">
      <!-- Tab Bar -->
      <div style="display: flex; gap: 0; margin-bottom: 16px; border-bottom: 1px solid var(--border-color);">
        <button
          v-for="tab in ([
            { key: 'markdown' as const, label: 'Markdown' },
            { key: 'html' as const, label: 'HTML 预览' },
            { key: 'layout' as const, label: 'Layout PDF' },
            { key: 'images' as const, label: `图片 (${fileDetail.parse?.images?.length ?? 0})` }
          ] as const)"
          :key="tab.key"
          class="tab-btn"
          :class="{ active: activeDetailTab === tab.key }"
          @click="activeDetailTab = tab.key"
          style="padding: 10px 20px; border: none; background: none; cursor: pointer; font-size: 14px; color: var(--text-secondary); border-bottom: 2px solid transparent;"
        >
          {{ tab.label }}
        </button>
      </div>

      <!-- Markdown Tab -->
      <div v-if="activeDetailTab === 'markdown'" class="card">
        <div class="card-body" style="padding: 20px;">
          <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px;">
            <span style="font-weight: 600;">Markdown 内容</span>
            <div style="display: flex; gap: 8px;">
              <button class="btn btn-secondary btn-small" @click="handleExportParseMarkdown">导出 MD</button>
            </div>
          </div>
          <pre v-if="fileDetail.parse?.markdownContent" style="background: var(--bg-secondary); padding: 16px; border-radius: 6px; overflow-x: auto; white-space: pre-wrap; max-height: calc(100vh - 300px); overflow-y: auto; font-size: 13px; line-height: 1.7;">{{ fileDetail.parse.markdownContent }}</pre>
          <div v-else style="text-align: center; padding: 40px; color: var(--text-muted);">无 Markdown 内容</div>
        </div>
      </div>

      <!-- HTML Preview Tab -->
      <div v-if="activeDetailTab === 'html'" class="card">
        <div class="card-body" style="padding: 20px;">
          <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px;">
            <span style="font-weight: 600;">HTML 预览</span>
            <div style="display: flex; gap: 8px;">
              <button class="btn btn-secondary btn-small" @click="handleExportParseHtml">导出 HTML</button>
              <button class="btn btn-secondary btn-small" :disabled="!htmlPreviewUrl" @click="handlePreviewHtml">新窗口打开</button>
            </div>
          </div>
          <div v-if="!htmlPreviewUrl && fileDetail.parse?.markdownContent" style="text-align: center; padding: 40px; color: var(--text-muted);">加载预览中...</div>
          <iframe
            v-if="htmlPreviewUrl"
            :src="htmlPreviewUrl"
            style="width: 100%; min-height: calc(100vh - 300px); border: 1px solid var(--border-color); border-radius: 6px;"
            sandbox=""
          ></iframe>
          <div v-if="!fileDetail.parse?.markdownContent" style="text-align: center; padding: 40px; color: var(--text-muted);">无内容可预览</div>
        </div>
      </div>

      <!-- Layout PDF Tab -->
      <div v-if="activeDetailTab === 'layout'" class="card">
        <div class="card-body">
          <div v-if="fileDetail.parse?.layoutPdfUrl" style="height: calc(100vh - 200px); min-height: 600px">
            <iframe
              :src="fileDetail.parse.layoutPdfUrl"
              style="width: 100%; height: 100%; border: 1px solid var(--border-color); border-radius: 6px"
            ></iframe>
          </div>
          <div v-else style="text-align: center; padding: 80px; color: var(--text-muted);">
            <p style="font-size: 32px; margin-bottom: 12px;">暂无 Layout PDF</p>
            <p style="font-size: 13px;">当前使用的 MinerU 模型（vlm）不生成 layout.pdf。如需此功能，请将 MinerU 模型版本切换为 pipeline。</p>
          </div>
        </div>
      </div>

      <!-- Images Tab -->
      <div v-if="activeDetailTab === 'images'" class="card">
        <div class="card-body">
          <div v-if="fileDetail.parse?.images && fileDetail.parse.images.length > 0" style="display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px;">
            <div v-for="img in fileDetail.parse.images" :key="img.id" style="border: 1px solid var(--border-color); border-radius: 6px; overflow: hidden;">
              <a :href="img.imageUrl" target="_blank" style="display: block;">
                <img :src="img.imageUrl" :alt="img.imageName" style="width: 100%; aspect-ratio: 3/4; object-fit: cover;" />
              </a>
              <div style="padding: 8px; font-size: 11px; word-break: break-all; color: var(--text-secondary);">{{ img.imageName }}</div>
            </div>
          </div>
          <div v-else style="text-align: center; padding: 40px; color: var(--text-muted);">该解析结果没有图片</div>
        </div>
      </div>
    </template>
  </div>
</template>

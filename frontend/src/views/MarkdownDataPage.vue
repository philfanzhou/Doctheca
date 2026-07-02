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

async function handleExportParseMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Export failed'
    alert(msg)
  }
}

async function handleExportParseHtml(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Export failed'
    alert(msg)
  }
}

async function handlePreviewParse(parseId: string) {
  try {
    const { blob } = await client.exportParseHtml(parseId)
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Preview failed'
    alert(msg)
  }
}

onMounted(() => {
  loadParseList()
})
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">Markdown 数据</h1>
    <p class="page-subtitle">管理解析记录，导出 Markdown 和 HTML</p>
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
                <button class="btn btn-primary btn-small" :disabled="p.status !== 'parsed'" @click="handlePreviewParse(p.id)">预览</button>
                <button class="btn btn-secondary btn-small" @click="handleExportParseMarkdown(p.id)">导出MD</button>
                <button class="btn btn-secondary btn-small" @click="handleExportParseHtml(p.id)">导出HTML</button>
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
</template>

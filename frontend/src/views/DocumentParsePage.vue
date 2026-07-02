<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { createDocApiClient, type DocumentFile } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusClass } from '../utils/format'

const client = createDocApiClient()

const parseFileList = ref<DocumentFile[]>([])
const parseFileTotal = ref(0)
const parseFilePage = ref(1)
const parseFilePageSize = ref(20)
const parsePollTimer = ref<number | null>(null)
const parseFileParsing = ref(false)
const parseFileNameSearch = ref('')

const parseFileTotalPages = computed(() => Math.ceil(parseFileTotal.value / parseFilePageSize.value))

async function loadParseFileList() {
  try {
    const response = await client.listDocumentFiles(
      parseFilePage.value,
      parseFilePageSize.value,
      undefined,
      parseFileNameSearch.value || undefined
    )
    parseFileList.value = response.data as DocumentFile[]
    parseFileTotal.value = response.total
  } catch (e) {
    console.error('Failed to load parse file list', e)
  }
}

function handleParseFilePageChange(newPage: number) {
  parseFilePage.value = newPage
  loadParseFileList()
}

async function handleParseFile(id: string) {
  parseFileParsing.value = true
  try {
    await client.parseDocumentFile(id)
    await loadParseFileList()
    startParsePolling()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Parse request failed'
    alert(msg)
  } finally {
    parseFileParsing.value = false
  }
}

function startParsePolling() {
  if (parsePollTimer.value !== null) return
  parsePollTimer.value = window.setInterval(async () => {
    await loadParseFileList()
    const hasActive = parseFileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
    if (!hasActive) {
      stopParsePolling()
    }
  }, 5000)
}

function stopParsePolling() {
  if (parsePollTimer.value !== null) {
    clearInterval(parsePollTimer.value)
    parsePollTimer.value = null
  }
}

onMounted(() => {
  loadParseFileList()
})

onUnmounted(() => {
  stopParsePolling()
})
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">文档解析</h1>
    <p class="page-subtitle">管理所有文档的解析任务</p>
  </div>

  <div class="card">
    <div class="card-header">
      <span>文档列表</span>
      <div style="display: flex; align-items: center; gap: 8px">
        <div class="input-wrap" style="width: 200px">
          <input v-model="parseFileNameSearch" type="text" placeholder="搜索文件名..." @keyup.enter="parseFilePage = 1; loadParseFileList()" />
        </div>
        <button class="btn btn-secondary btn-small" @click="parseFilePage = 1; loadParseFileList()">搜索</button>
      </div>
    </div>
    <div class="card-body">
      <table v-if="parseFileList.length > 0" class="data-table">
        <thead>
          <tr>
            <th>文件名</th>
            <th>类型</th>
            <th>上传时间</th>
            <th>解析状态</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in parseFileList" :key="f.id">
            <td>{{ f.fileName }}</td>
            <td>{{ f.contentType }}</td>
            <td>{{ formatTime(f.createdAt) }}</td>
            <td>
              <span class="status-badge" :class="getFileStatusClass(f.parseStatus)">{{ getFileStatusLabel(f.parseStatus) }}</span>
            </td>
            <td>
              <button v-if="f.parseStatus === null || f.parseStatus === 'unparsed' || f.parseStatus === 'failed'" class="btn btn-primary btn-small" @click="handleParseFile(f.id)" :disabled="parseFileParsing">
                {{ f.parseStatus === 'failed' ? '重新解析' : '解析' }}
              </button>
              <span v-else-if="f.parseStatus === 'pending' || f.parseStatus === 'parsing'" style="font-size: 12px; color: var(--text-muted)">
                {{ getFileStatusLabel(f.parseStatus) }}
              </span>
              <button v-else-if="f.parseStatus === 'parsed'" class="btn btn-primary btn-small" @click="handleParseFile(f.id)" :disabled="parseFileParsing">重新解析</button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无文件</div>

      <div v-if="parseFileTotal > parseFilePageSize" class="pagination-bar">
        <span class="pagination-info">共 {{ parseFileTotal }} 条</span>
        <button class="page-btn" :disabled="parseFilePage <= 1" @click="handleParseFilePageChange(parseFilePage - 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
        </button>
        <button v-for="p in parseFileTotalPages" :key="p" class="page-btn" :class="{ active: parseFilePage === p }" @click="handleParseFilePageChange(p)">{{ p }}</button>
        <button class="page-btn" :disabled="parseFilePage >= parseFileTotalPages" @click="handleParseFilePageChange(parseFilePage + 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6" /></svg>
        </button>
      </div>
    </div>
  </div>
</template>

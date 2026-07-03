<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { createDocApiClient, type DocumentFile } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusClass } from '../utils/format'

const client = createDocApiClient()

const fileList = ref<DocumentFile[]>([])
const fileTotal = ref(0)
const filePage = ref(1)
const filePageSize = ref(20)
const fileNameSearch = ref('')
const fileUploadInput = ref<HTMLInputElement | null>(null)
const fileUploading = ref(false)
const fileUploadProgress = ref(0)
const parseFileParsing = ref(false)
const pollTimer = ref<number | null>(null)

const fileTotalPages = computed(() => Math.ceil(fileTotal.value / filePageSize.value))

async function loadFileList() {
  try {
    const response = await client.listDocumentFiles(
      filePage.value,
      filePageSize.value,
      undefined,
      fileNameSearch.value || undefined
    )
    fileList.value = response.data as DocumentFile[]
    fileTotal.value = response.total
  } catch (e) {
    console.error('Failed to load file list', e)
  }
}

function handlePageChange(newPage: number) {
  filePage.value = newPage
  loadFileList()
}

function triggerFileUpload() {
  fileUploadInput.value?.click()
}

async function handleFileUploadChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return

  fileUploading.value = true
  fileUploadProgress.value = 0

  try {
    await client.uploadDocumentFile(file, (progressEvent) => {
      if (progressEvent.total) {
        fileUploadProgress.value = Math.round((progressEvent.loaded / progressEvent.total) * 100)
      }
    })

    filePage.value = 1
    fileNameSearch.value = ''
    await loadFileList()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Upload failed'
    alert(msg)
  } finally {
    fileUploading.value = false
    fileUploadProgress.value = 0
  }

  input.value = ''
}

async function handleParseFile(id: string, modelVersion: string) {
  parseFileParsing.value = true
  try {
    await client.parseDocumentFile(id, modelVersion)
    await loadFileList()
    startPolling()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Parse request failed'
    alert(msg)
  } finally {
    parseFileParsing.value = false
  }
}

async function handleDeleteFile(id: string) {
  if (!confirm('确定删除此文件？')) return
  try {
    await client.deleteDocumentFile(id)
    if (fileList.value.length <= 1 && filePage.value > 1) {
      filePage.value--
    }
    await loadFileList()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Delete failed'
    alert(msg)
  }
}

function startPolling() {
  if (pollTimer.value !== null) return
  pollTimer.value = window.setInterval(async () => {
    await loadFileList()
    const hasActive = fileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
    if (!hasActive) {
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

onMounted(async () => {
  await loadFileList()
  const hasActive = fileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
  if (hasActive) startPolling()
})

onUnmounted(() => {
  stopPolling()
})
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">文档管理</h1>
    <p class="page-subtitle">管理文档的上传和解析</p>
  </div>

  <div class="card">
    <div class="card-header">
      <span>文档列表</span>
      <div class="card-header-actions">
        <div class="input-wrap" style="width: 200px">
          <input v-model="fileNameSearch" type="text" placeholder="搜索文件名..." @keyup.enter="filePage = 1; loadFileList()" />
        </div>
        <button class="btn btn-secondary btn-small" @click="filePage = 1; loadFileList()">搜索</button>
        <button class="btn btn-primary btn-small" :disabled="fileUploading" @click="triggerFileUpload">
          {{ fileUploading ? '上传中...' : '上传文件' }}
        </button>
        <input ref="fileUploadInput" type="file" accept=".pdf,.docx,.doc,.pptx,.ppt" style="display: none" @change="handleFileUploadChange" />
      </div>
    </div>
    <div class="card-body">
      <div v-if="fileUploading" style="margin-bottom: 16px">
        <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px">
          <span style="font-size: 12px; color: var(--text-secondary)">上传中...</span>
          <span style="font-size: 12px; color: var(--primary)">{{ fileUploadProgress }}%</span>
        </div>
        <div style="height: 6px; background: var(--bg-secondary); border-radius: 3px; overflow: hidden">
          <div style="height: 100%; background: var(--primary); border-radius: 3px; transition: width 0.3s" :style="{ width: fileUploadProgress + '%' }"></div>
        </div>
      </div>

      <table v-if="fileList.length > 0" class="data-table">
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
          <tr v-for="f in fileList" :key="f.id">
            <td>{{ f.fileName }}</td>
            <td>{{ f.contentType }}</td>
            <td>{{ formatTime(f.createdAt) }}</td>
            <td>
              <span class="status-badge" :class="getFileStatusClass(f.parseStatus)">{{ getFileStatusLabel(f.parseStatus) }}</span>
            </td>
            <td>
              <div class="table-actions">
                <template v-if="f.parseStatus === null || f.parseStatus === 'unparsed' || f.parseStatus === 'failed'">
                  <button class="btn btn-primary btn-small" @click="handleParseFile(f.id, 'vlm')" :disabled="parseFileParsing">VLM 解析</button>
                  <button class="btn btn-secondary btn-small" @click="handleParseFile(f.id, 'pipeline')" :disabled="parseFileParsing">Pipeline 解析</button>
                </template>
                <span v-else-if="f.parseStatus === 'pending' || f.parseStatus === 'parsing'" style="font-size: 12px; color: var(--text-muted)">
                  {{ getFileStatusLabel(f.parseStatus) }}
                </span>
                <template v-else-if="f.parseStatus === 'parsed'">
                  <button class="btn btn-primary btn-small" @click="handleParseFile(f.id, 'vlm')" :disabled="parseFileParsing">VLM 重新解析</button>
                  <button class="btn btn-secondary btn-small" @click="handleParseFile(f.id, 'pipeline')" :disabled="parseFileParsing">Pipeline 重新解析</button>
                </template>
                <button class="btn btn-secondary btn-small" style="color: var(--danger)" @click="handleDeleteFile(f.id)">删除</button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无文件，点击上方按钮上传</div>

      <div v-if="fileTotal > filePageSize" class="pagination-bar">
        <span class="pagination-info">共 {{ fileTotal }} 条</span>
        <button class="page-btn" :disabled="filePage <= 1" @click="handlePageChange(filePage - 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
        </button>
        <button v-for="p in fileTotalPages" :key="p" class="page-btn" :class="{ active: filePage === p }" @click="handlePageChange(p)">{{ p }}</button>
        <button class="page-btn" :disabled="filePage >= fileTotalPages" @click="handlePageChange(filePage + 1)">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6" /></svg>
        </button>
      </div>
    </div>
  </div>
</template>

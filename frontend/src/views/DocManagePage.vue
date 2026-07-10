<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Upload, Search } from '@element-plus/icons-vue'
import { createDocApiClient, type DocumentFile } from '../services/docApi'
import { formatTime, getFileStatusLabel, getFileStatusType } from '../utils/format'

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
const listLoading = ref(false)

async function loadFileList() {
  listLoading.value = true
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
    ElMessage.error(e instanceof Error ? e.message : '加载文件列表失败')
  } finally {
    listLoading.value = false
  }
}

function handlePageChange(newPage: number) {
  filePage.value = newPage
  loadFileList()
}

function handlePageSizeChange(newSize: number) {
  filePageSize.value = newSize
  filePage.value = 1
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
    ElMessage.success('上传成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '上传失败'
    ElMessage.error(msg)
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
    ElMessage.success('已触发解析')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '解析请求失败'
    ElMessage.error(msg)
  } finally {
    parseFileParsing.value = false
  }
}

async function handleDeleteFile(id: string) {
  try {
    await ElMessageBox.confirm('确定删除此文件？', '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }
  try {
    await client.deleteDocumentFile(id)
    if (fileList.value.length <= 1 && filePage.value > 1) {
      filePage.value--
    }
    await loadFileList()
    ElMessage.success('删除成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '删除失败'
    ElMessage.error(msg)
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

  <el-card shadow="never" class="page-card">
    <template #header>
      <div class="card-header">
        <span>文档列表</span>
        <div class="card-header-actions">
          <el-input
            v-model="fileNameSearch"
            :prefix-icon="Search"
            placeholder="搜索文件名..."
            clearable
            style="width: 220px"
            @keyup.enter="filePage = 1; loadFileList()"
          />
          <el-button size="small" @click="filePage = 1; loadFileList()">搜索</el-button>
          <el-button type="primary" size="small" :icon="Upload" :loading="fileUploading" @click="triggerFileUpload">
            上传文件
          </el-button>
          <input ref="fileUploadInput" type="file" accept=".pdf,.docx,.doc,.pptx,.ppt" hidden @change="handleFileUploadChange" />
        </div>
      </div>
    </template>

    <el-progress
      v-if="fileUploading"
      :percentage="fileUploadProgress"
      :stroke-width="6"
      status="success"
      style="margin-bottom: 12px"
    />

    <el-table
      v-loading="listLoading"
      :data="fileList"
      stripe
      border
      empty-text="暂无文件，点击上方按钮上传"
    >
      <el-table-column prop="fileName" label="文件名" min-width="220" show-overflow-tooltip />
      <el-table-column prop="contentType" label="类型" width="140" />
      <el-table-column label="上传时间" width="180">
        <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="解析状态" width="120">
        <template #default="{ row }">
          <el-tag :type="getFileStatusType(row.parseStatus)" size="small">
            {{ getFileStatusLabel(row.parseStatus) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="340">
        <template #default="{ row }">
          <div class="table-actions">
            <template v-if="row.parseStatus === null || row.parseStatus === 'unparsed' || row.parseStatus === 'failed'">
              <el-button type="primary" size="small" :disabled="parseFileParsing" @click="handleParseFile(row.id, 'vlm')">VLM 解析</el-button>
              <el-button size="small" :disabled="parseFileParsing" @click="handleParseFile(row.id, 'pipeline')">Pipeline 解析</el-button>
            </template>
            <template v-else-if="row.parseStatus === 'pending' || row.parseStatus === 'parsing'">
              <span class="text-muted-sm">{{ getFileStatusLabel(row.parseStatus) }}</span>
            </template>
            <template v-else-if="row.parseStatus === 'parsed'">
              <el-button type="primary" size="small" :disabled="parseFileParsing" @click="handleParseFile(row.id, 'vlm')">VLM 重新解析</el-button>
              <el-button size="small" :disabled="parseFileParsing" @click="handleParseFile(row.id, 'pipeline')">Pipeline 重新解析</el-button>
            </template>
            <el-button type="danger" size="small" text @click="handleDeleteFile(row.id)">删除</el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <div v-if="fileTotal > 0" class="pagination-wrap">
      <el-pagination
        v-model:current-page="filePage"
        v-model:page-size="filePageSize"
        :total="fileTotal"
        :page-sizes="[20, 50, 100]"
        layout="total, sizes, prev, pager, next, jumper"
        background
        @current-change="handlePageChange"
        @size-change="handlePageSizeChange"
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

.table-actions {
  display: flex;
  gap: 4px;
  align-items: center;
  flex-wrap: wrap;
}

.text-muted-sm {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.pagination-wrap {
  margin-top: 12px;
  display: flex;
  justify-content: flex-end;
}
</style>

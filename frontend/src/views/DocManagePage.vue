<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentFile } from '../services/docApi'
import { formatTime, getFileStatusLabel } from '../utils/format'
import { useToast } from '../composables/useToast'
import { iconHtml } from '../utils/icons'
import StatusStrip from '../components/StatusStrip.vue'
import { inject } from 'vue'

const client = createDocApiClient()
const { success: toastSuccess, error: toastError } = useToast()

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
const filterStatus = ref<string>('all')

const openDocDetail = inject<(id: string) => void>('openDocDetail')

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
    toastError(e instanceof Error ? e.message : '加载文件列表失败')
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
    toastSuccess('上传成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '上传失败'
    toastError(msg)
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
    toastSuccess(`已触发 ${modelVersion === 'vlm' ? 'VLM' : 'Pipeline'} 解析`)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '解析请求失败'
    toastError(msg)
  } finally {
    parseFileParsing.value = false
  }
}

async function handleDeleteFile(id: string) {
  try {
    await ElMessageBox.confirm('确定删除此文件？', '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
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
    toastSuccess('删除成功')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : '删除失败'
    toastError(msg)
  }
}

function startPolling() {
  if (pollTimer.value !== null) return
  pollTimer.value = window.setInterval(async () => {
    await loadFileList()
    const hasActive = fileList.value.some(
      (f) => f.parseStatus === 'pending' || f.parseStatus === 'parsing'
    )
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

const stripItems = computed(() => {
  const all = fileTotal.value
  const unparsed = fileList.value.filter((f) => f.parseStatus === null || f.parseStatus === 'unparsed').length
  const parsing = fileList.value.filter((f) => f.parseStatus === 'parsing' || f.parseStatus === 'pending').length
  const failed = fileList.value.filter((f) => f.parseStatus === 'failed').length
  return [
    { key: 'all', label: '全部文档', count: all, color: '#4F46E5' },
    { key: 'unparsed', label: '待解析', count: unparsed, color: '#9AA0B6' },
    { key: 'parsing', label: '解析中', count: parsing, color: '#0EA5E9' },
    { key: 'failed', label: '解析失败', count: failed, color: '#EF4444' },
  ]
})

function selectStrip(key: string) {
  filterStatus.value = key
  filePage.value = 1
  loadFileList()
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

function statusBadgeHtml(status: string | null): string {
  if (status === 'parsed')
    return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed')
    return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending')
    return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}

function onRowClick(f: DocumentFile) {
  openDocDetail?.(f.id)
}

function handleDrop(e: DragEvent) {
  e.preventDefault()
  const input = fileUploadInput.value
  if (!input || !e.dataTransfer?.files.length) return
  const dt = new DataTransfer()
  dt.items.add(e.dataTransfer.files[0])
  input.files = dt.files
  handleFileUploadChange({ target: input } as unknown as Event)
}

onMounted(async () => {
  await loadFileList()
  const hasActive = fileList.value.some(
    (f) => f.parseStatus === 'pending' || f.parseStatus === 'parsing'
  )
  if (hasActive) startPolling()
})

onUnmounted(() => {
  stopPolling()
})
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">文档管理</div>
        <div class="page-sub">原始教材文件的上传与解析任务调度</div>
      </div>
    </div>

    <StatusStrip :items="stripItems" :active="filterStatus" @select="selectStrip" />

    <div
      class="upload-zone"
      :class="{ drag: false }"
      @click="triggerFileUpload"
      @dragover.prevent
      @drop="handleDrop"
    >
      <span v-html="iconHtml('upload')"></span>
      <div class="upload-title">
        {{ fileUploading ? `正在上传 ${fileUploadProgress}%` : '点击或拖拽文件到此处上传' }}
      </div>
      <div class="upload-hint">支持 PDF / DOCX / PPTX，单文件不超过 200 MB</div>
      <div
        v-if="fileUploading"
        class="progress-track"
        style="max-width: 340px; margin: 12px auto 0"
      >
        <div class="progress-fill" :style="{ width: fileUploadProgress + '%' }"></div>
      </div>
      <input
        ref="fileUploadInput"
        type="file"
        accept=".pdf,.docx,.doc,.pptx,.ppt"
        hidden
        @change="handleFileUploadChange"
      />
    </div>

    <div class="card section-gap">
      <div class="card-head">
        <div>
          <div class="card-title">文档列表</div>
          <div class="card-sub">共 {{ fileTotal }} 份文档</div>
        </div>
        <div style="display: flex; gap: 8px; align-items: center; flex-wrap: wrap">
          <div style="position: relative; width: 220px">
            <span
              v-html="iconHtml('search')"
              style="position: absolute; left: 10px; top: 9px; color: var(--text-3); width: 15px; height: 15px"
            ></span>
            <input
              v-model="fileNameSearch"
              class="input"
              style="padding-left: 32px"
              placeholder="搜索文件名…"
              @keyup.enter="filePage = 1; loadFileList()"
            />
          </div>
          <button class="btn btn-ghost btn-sm" @click="filePage = 1; loadFileList()">搜索</button>
        </div>
      </div>

      <div v-loading="listLoading" style="min-height: 200px">
        <div class="table-wrap">
          <table class="data-table">
            <thead>
              <tr>
                <th>文件名</th>
                <th>类型</th>
                <th>上传时间</th>
                <th>解析状态</th>
                <th style="text-align: right">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="f in fileList"
                :key="f.id"
                class="clickable"
                @click="onRowClick(f)"
              >
                <td>
                  <div class="cell-flex">
                    <div class="file-ico" :class="fileIconCls(f.contentType)">{{ fileExtLabel(f.contentType) }}</div>
                    <div>
                      <div class="td-main" style="max-width: 360px">{{ f.fileName }}</div>
                    </div>
                  </div>
                </td>
                <td>
                  <span class="mono" style="font-size: 12px; color: var(--text-2)">{{ f.contentType }}</span>
                </td>
                <td style="color: var(--text-3); font-size: 12.5px; font-variant-numeric: tabular-nums">
                  {{ formatTime(f.createdAt) }}
                </td>
                <td>
                  <span v-html="statusBadgeHtml(f.parseStatus)"></span>
                </td>
                <td style="text-align: right; white-space: nowrap" @click.stop>
                  <template
                    v-if="f.parseStatus === null || f.parseStatus === 'unparsed' || f.parseStatus === 'failed'"
                  >
                    <button class="btn btn-sm" :disabled="parseFileParsing" @click="handleParseFile(f.id, 'vlm')">VLM 解析</button>
                    <button class="btn btn-ghost btn-sm" :disabled="parseFileParsing" @click="handleParseFile(f.id, 'pipeline')">Pipeline</button>
                  </template>
                  <template v-else-if="f.parseStatus === 'pending' || f.parseStatus === 'parsing'">
                    <span class="badge blue"><span class="dot pulse"></span>解析中</span>
                  </template>
                  <template v-else-if="f.parseStatus === 'parsed'">
                    <button class="btn btn-sm" :disabled="parseFileParsing" @click="handleParseFile(f.id, 'vlm')">重新解析</button>
                  </template>
                  <button class="btn btn-danger btn-sm" @click="handleDeleteFile(f.id)">
                    <span v-html="iconHtml('trash')"></span>删除
                  </button>
                </td>
              </tr>
              <tr v-if="!fileList.length && !listLoading">
                <td colspan="5">
                  <div class="empty">
                    <span v-html="iconHtml('file')"></span><br />
                    暂无文件，点击上方上传
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="fileTotal > 0" class="pager">
        <span class="total">共 {{ fileTotal }} 份文档</span>
        <el-pagination
          v-model:current-page="filePage"
          v-model:page-size="filePageSize"
          :total="fileTotal"
          :page-sizes="[20, 50, 100]"
          layout="sizes, prev, pager, next, jumper"
          background
          @current-change="handlePageChange"
          @size-change="handlePageSizeChange"
        />
      </div>
    </div>
  </div>
</template>

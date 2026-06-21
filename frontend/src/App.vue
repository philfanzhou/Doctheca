<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ElMessage, type UploadUserFile } from 'element-plus'
import { Upload } from '@element-plus/icons-vue'
import {
  createDocApiClient,
  getDocErrorMessage,
  type Document,
  type DocumentStatus,
  type SearchResult,
  type DocumentSegmentsData,
  type SegmentDto,
  type CorrectionDto,
  type ConsistencyScanResult
} from './services/docApi'
import { authService } from './services/authService'
import LoginPage from './components/LoginPage.vue'

const isAuthenticated = ref(false)
const appTitle = ref('DocRetrieval Admin')
const activeTab = ref('documents')
const loadingDocuments = ref(false)
const uploading = ref(false)
const deleting = ref(false)
const viewingStatus = ref(false)
const documents = ref<Document[]>([])
const total = ref(0)
const totalReady = ref(0)
const totalProcessing = ref(0)
const totalFailed = ref(0)
const page = ref(1)
const pageSize = ref(20)
const statusFilter = ref<string>('')
const subjectFilter = ref<string>('')
const gradeFilter = ref<string>('')
const keywordFilter = ref<string>('')
const yearFilter = ref<string>('')
const searchQuery = ref('')
const searchPhrase = ref(false)
const searchLoading = ref(false)
const searchResults = ref<SearchResult[]>([])
const searchTotalCount = ref(0)
const searchNextToken = ref('')
const sidebarOpen = ref(false)
const sidebarCollapsed = ref(localStorage.getItem('docSidebarCollapsed') === 'true')
const lastRefreshTime = ref('')
const showUploadDialog = ref(false)
const showStatusDialog = ref(false)
const showUpdateMetadataDialog = ref(false)
const showDeleteConfirm = ref(false)

// Segment Refinement state
const showSegmentDialog = ref(false)
const segmentLoading = ref(false)
const segmentData = ref<DocumentSegmentsData | null>(null)
const segmentDocTitle = ref('')
const selectedSegmentIds = ref<Set<string>>(new Set())
const corrections = ref<CorrectionDto[]>([])
const refining = ref(false)
const showSplitDialog = ref(false)
const splitTarget = ref<SegmentDto | null>(null)
const splitPosition = ref(10)
const showSplitMergeDialog = ref(false)
const splitMergeTarget = ref<SegmentDto | null>(null)
const splitMergePosition = ref(10)
const splitMergeWithPrev = ref(true)
const splitMergeWithNext = ref(true)

// Consistency scan state
const scanLoading = ref(false)
const scanResult = ref<ConsistencyScanResult | null>(null)
const scanTime = ref('')
const deletingOrphan = ref<string | null>(null)
const forceDeleting = ref<string | null>(null)

const selectedDocument = ref<Document | null>(null)
const selectedDocumentStatus = ref<DocumentStatus | null>(null)

// 任务进度轮询：弹窗打开且 job 处于 processing 状态时启动
const statusPollTimer = ref<number | null>(null)
const stageLabels: Record<string, string> = {
  pending: '等待处理',
  starting: '启动中',
  downloading: '下载文件',
  parsing: '解析内容',
  writing_pages: '写入页面',
  writing_segments: '写入分段',
  indexing: '建立索引',
  completed: '已完成',
}

function startStatusPolling(documentId: string) {
  if (statusPollTimer.value !== null) return
  statusPollTimer.value = window.setInterval(async () => {
    try {
      const response = await client.getDocumentStatus(documentId)
      selectedDocumentStatus.value = response.data
      const latestJob = response.data.jobs?.[0]
      if (latestJob && latestJob.status !== 'processing') {
        stopStatusPolling()
      }
    } catch (e) {
      // 网络错误保持轮询，下次重试
      console.warn('状态轮询失败', e)
    }
  }, 2000)
}

function stopStatusPolling() {
  if (statusPollTimer.value !== null) {
    window.clearInterval(statusPollTimer.value)
    statusPollTimer.value = null
  }
}
const uploadFile = ref<File | null>(null)
const uploadForm = ref({
  title: '',
  subject: '',
  grade: '',
  year: '',
  tags: ''
})

const metadataForm = ref({
  subject: '',
  grade: '',
  year: '',
  tags: ''
})

const client = createDocApiClient()

const currentUser = computed(() => authService.getUser())
const displayName = computed(() => currentUser.value?.username ?? '管理员')

const navItems = [
  {
    key: 'documents',
    label: '文档管理',
    icon: '<path d="M15.5 13.333l1.533 1.322c.645.555.967.833.967 1.178s-.322.623-.967 1.179L15.5 18.333m-3.333-5l-1.534 1.322c-.644.555-.966.833-.966 1.178s.322.623.966 1.179l1.534 1.321"/><path d="M17.167 10.836v-4.32c0-1.41 0-2.117-.224-2.68-.359-.906-1.118-1.621-2.08-1.96-.599-.21-1.349-.21-2.848-.21-2.623 0-3.935 0-4.983.369-1.684.591-3.013 1.842-3.641 3.428C3 6.449 3 7.684 3 10.154v2.122c0 2.558 0 3.838.706 4.726q.306.383.713.671c.76.536 1.79.64 3.581.66"/>'
  },
  {
    key: 'search',
    label: '检索测试',
    icon: '<circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>'
  },
  {
    key: 'consistency',
    label: '一致性检查',
    icon: '<path d="M9 12l2 2 4-4"/><path d="M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z"/>'
  }
]

const currentNavLabel = computed(() => navItems.find((n) => n.key === activeTab.value)?.label ?? '')
const totalPages = computed(() => Math.ceil(total.value / pageSize.value))
const pageNumbers = computed(() => {
  const total = totalPages.value
  if (total <= 0) return []
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1)
  const pages: number[] = []
  const current = page.value
  const start = Math.max(1, current - 2)
  const end = Math.min(total, current + 2)
  for (let i = start; i <= end; i++) pages.push(i)
  return pages
})

function toggleSidebar() {
  sidebarCollapsed.value = !sidebarCollapsed.value
  localStorage.setItem('docSidebarCollapsed', String(sidebarCollapsed.value))
}

function updateRefreshTime() {
  lastRefreshTime.value = new Date().toLocaleTimeString()
}

function getStatusTagClass(status: string) {
  switch (status) {
    case 'ready':
      return 'tag-success'
    case 'failed':
      return 'tag-danger'
    case 'processing':
      return 'tag-warning'
    default:
      return 'tag-info'
  }
}

function getStatusLabel(status: string) {
  switch (status) {
    case 'pending':
      return '待处理'
    case 'processing':
      return '处理中'
    case 'ready':
      return '已就绪'
    case 'failed':
      return '失败'
    default:
      return status
  }
}

function formatDate(dateVal: string | number | null | undefined): string {
  if (!dateVal && dateVal !== 0) return '-'
  try {
    let ts: number
    if (typeof dateVal === 'number') {
      ts = dateVal
    } else {
      const parsed = Number(dateVal)
      ts = isNaN(parsed) ? new Date(dateVal).getTime() / 1000 : parsed
    }
    if (ts < 10000000000) ts *= 1000
    const d = new Date(ts)
    return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
  } catch {
    return String(dateVal)
  }
}

async function loadDocuments() {
  loadingDocuments.value = true
  try {
    const response = await client.listDocuments(
      page.value,
      pageSize.value,
      statusFilter.value || undefined,
      subjectFilter.value || undefined,
      gradeFilter.value || undefined,
      keywordFilter.value || undefined,
      yearFilter.value || undefined
    )
    documents.value = response.data
    total.value = response.total
    // Update global stats only on first page without status filter
    if (!statusFilter.value && page.value === 1) {
      totalReady.value = documents.value.filter((d) => d.status === 'ready').length
      totalProcessing.value = documents.value.filter((d) => d.status === 'processing').length
      totalFailed.value = documents.value.filter((d) => d.status === 'failed').length
    }
    updateRefreshTime()
  } catch (error) {
    handleApiError('加载文档列表失败', error)
  } finally {
    loadingDocuments.value = false
  }
}

async function handleUpload() {
  if (!uploadFile.value || !uploadForm.value.title) {
    ElMessage.warning('请选择文件并填写标题')
    return
  }

  uploading.value = true
  try {
    const tags = uploadForm.value.tags
      ? uploadForm.value.tags.split(',').map((t) => t.trim()).filter((t) => t)
      : undefined

    await client.uploadDocument(
      uploadFile.value,
      uploadForm.value.title,
      uploadForm.value.subject,
      uploadForm.value.grade,
      uploadForm.value.year || undefined,
      tags
    )

    ElMessage.success('文档上传成功')
    showUploadDialog.value = false
    resetUploadForm()
    await loadDocuments()
  } finally {
    uploading.value = false
  }
}

async function handleViewStatus(doc: Document) {
  selectedDocument.value = doc
  viewingStatus.value = true
  try {
    const response = await client.getDocumentStatus(doc.id)
    selectedDocumentStatus.value = response.data
    showStatusDialog.value = true
    // 如果最新 job 仍在 processing，启动 2 秒轮询
    const latestJob = response.data.jobs?.[0]
    if (latestJob && latestJob.status === 'processing') {
      startStatusPolling(doc.id)
    }
  } catch (error) {
    handleApiError('获取状态失败', error)
  } finally {
    viewingStatus.value = false
  }
}

async function handleUpdateMetadata(doc: Document) {
  selectedDocument.value = doc
  metadataForm.value = {
    subject: doc.subject,
    grade: doc.grade,
    year: doc.year,
    tags: doc.tags || ''
  }
  showUpdateMetadataDialog.value = true
}

async function saveMetadata() {
  if (!selectedDocument.value) return

  try {
    const tags = metadataForm.value.tags
      ? metadataForm.value.tags.split(',').map((t) => t.trim()).filter((t) => t)
      : undefined

    await client.updateMetadata(selectedDocument.value.title, {
      subject: metadataForm.value.subject || undefined,
      grade: metadataForm.value.grade || undefined,
      year: metadataForm.value.year || undefined,
      tags
    })

    ElMessage.success('元数据更新成功')
    showUpdateMetadataDialog.value = false
    await loadDocuments()
  } catch (error) {
    handleApiError('更新元数据失败', error)
  }
}

async function confirmDelete(doc: Document) {
  selectedDocument.value = doc
  showDeleteConfirm.value = true
}

async function handleDelete() {
  if (!selectedDocument.value) return

  deleting.value = true
  try {
    await client.deleteDocument(selectedDocument.value.title)
    ElMessage.success('文档删除成功')
    showDeleteConfirm.value = false
    await loadDocuments()
  } catch (error) {
    handleApiError('删除文档失败', error)
  } finally {
    deleting.value = false
  }
}

function handlePageChange(newPage: number) {
  page.value = newPage
  loadDocuments()
}

function handleFilterChange() {
  page.value = 1
  loadDocuments()
}

function handleFileChange(file: UploadUserFile) {
  uploadFile.value = file.raw as File
  if (!uploadForm.value.title && file.name) {
    uploadForm.value.title = file.name.replace(/\.[^.]+$/, '')
  }
}

function resetUploadForm() {
  uploadForm.value = {
    title: '',
    subject: '',
    grade: '',
    year: '',
    tags: ''
  }
  uploadFile.value = null
}

function handleApiError(prefix: string, error: unknown) {
  ElMessage.error(`${prefix}: ${getDocErrorMessage(error)}`)
}

async function handleScanConsistency() {
  scanLoading.value = true
  scanResult.value = null
  try {
    const response = await client.scanConsistency()
    scanResult.value = response.data
    scanTime.value = new Date().toLocaleTimeString()
    const s = response.data.summary
    ElMessage.success(`扫描完成：${s.totalDocuments} 个文档，${s.brokenCount} 个异常，${s.orphanCount} 个孤儿文件`)
  } catch (error) {
    handleApiError('扫描失败', error)
  } finally {
    scanLoading.value = false
  }
}

async function handleDeleteOrphan(filePath: string) {
  deletingOrphan.value = filePath
  try {
    // Orphan OSS files have no DB record, so we call force-delete by constructing a temp approach
    // Actually we need a dedicated endpoint for this. For now, just show the path.
    ElMessage.info(`孤儿文件：${filePath}（请手动删除或使用管理员工具）`)
  } finally {
    deletingOrphan.value = null
  }
}

async function handleForceDelete(docId: string, title: string) {
  if (!confirm(`确定强制删除文档「${title}」？此操作不可恢复。`)) return
  forceDeleting.value = docId
  try {
    await client.forceDeleteDocument(docId)
    ElMessage.success(`文档「${title}」已强制删除`)
    // Remove from scan results
    if (scanResult.value) {
      scanResult.value.brokenDocuments = scanResult.value.brokenDocuments.filter(d => d.id !== docId)
      scanResult.value.summary.brokenCount = scanResult.value.brokenDocuments.length
    }
  } catch (error) {
    handleApiError('强制删除失败', error)
  } finally {
    forceDeleting.value = null
  }
}

async function handleSearch() {
  if (!searchQuery.value.trim()) return
  searchLoading.value = true
  try {
    const response = await client.searchTest(
      searchQuery.value.trim(),
      searchPhrase.value,
      20
    )
    searchResults.value = response.results
    searchTotalCount.value = response.totalCount
    searchNextToken.value = response.nextPageToken
  } catch (error) {
    handleApiError('检索失败', error)
  } finally {
    searchLoading.value = false
  }
}

function clearSearch() {
  searchQuery.value = ''
  searchPhrase.value = false
  searchResults.value = []
  searchTotalCount.value = 0
  searchNextToken.value = ''
}

function handleLoginSuccess() {
  isAuthenticated.value = true
  loadDocuments()
}

async function handleLogout() {
  await authService.logout()
  isAuthenticated.value = false
}

onMounted(async () => {
  // Check if already authenticated
  if (authService.isAuthenticated()) {
    isAuthenticated.value = true
    loadDocuments()
  } else if (authService.canRefresh()) {
    const newTokens = await authService.refresh()
    if (newTokens) {
      isAuthenticated.value = true
      loadDocuments()
    }
  }
})

onUnmounted(() => {
  stopStatusPolling()
})

function closeStatusDialog() {
  showStatusDialog.value = false
  stopStatusPolling()
}

// ========== Segment Refinement ==========

async function openSegmentDialog(doc: Document) {
  segmentDocTitle.value = doc.title
  showSegmentDialog.value = true
  segmentLoading.value = true
  selectedSegmentIds.value = new Set()
  corrections.value = []

  try {
    const response = await client.getDocumentSegments(doc.id)
    segmentData.value = response.data
  } catch (error) {
    handleApiError('获取分段数据失败', error)
    showSegmentDialog.value = false
  } finally {
    segmentLoading.value = false
  }
}

function toggleSegmentSelect(sentenceId: string) {
  const newSet = new Set(selectedSegmentIds.value)
  if (newSet.has(sentenceId)) {
    newSet.delete(sentenceId)
  } else {
    newSet.add(sentenceId)
  }
  selectedSegmentIds.value = newSet
}

function selectAllSegments() {
  if (!segmentData.value) return
  if (selectedSegmentIds.value.size === segmentData.value.segments.length) {
    selectedSegmentIds.value = new Set()
  } else {
    selectedSegmentIds.value = new Set(segmentData.value.segments.map(s => s.sentenceId))
  }
}

function mergeSelected() {
  if (!segmentData.value) return
  const selected = segmentData.value.segments
    .filter(s => selectedSegmentIds.value.has(s.sentenceId))
    .sort((a, b) => a.startOffset - b.startOffset)

  if (selected.length < 2) {
    ElMessage.warning('请至少选中 2 条 segment')
    return
  }

  const mergedText = selected.map(s => s.text).join(' ')
  const firstType = selected[0].segmentType

  corrections.value.push({
    originalSentenceIds: selected.map(s => s.sentenceId),
    action: 'merge',
    newText: mergedText,
    newSegmentType: firstType
  })

  const newSegments = segmentData.value.segments.filter(s => !selectedSegmentIds.value.has(s.sentenceId))
  newSegments.push({
    id: 'merged-' + Date.now(),
    sentenceId: selected[0].sentenceId,
    segmentType: firstType,
    text: mergedText,
    startOffset: selected[0].startOffset,
    endOffset: selected[selected.length - 1].endOffset,
    pageNumber: selected[0].pageNumber
  })
  newSegments.sort((a, b) => a.startOffset - b.startOffset)

  segmentData.value = { ...segmentData.value, segments: newSegments, totalCount: newSegments.length }
  selectedSegmentIds.value = new Set()
  ElMessage.success(`已合并 ${selected.length} 条 segment`)
}

function changeSegmentType(sentenceId: string, newType: string) {
  if (!segmentData.value) return
  const seg = segmentData.value.segments.find(s => s.sentenceId === sentenceId)
  if (!seg || seg.segmentType === newType) return

  corrections.value.push({
    originalSentenceIds: [sentenceId],
    action: 'retype',
    newSegmentType: newType
  })
  seg.segmentType = newType
  ElMessage.success(`已修改 ${sentenceId} 类型为 ${newType}`)
}

function startSplit(seg: SegmentDto) {
  splitTarget.value = seg
  splitPosition.value = Math.floor(seg.text.length / 2)
  showSplitDialog.value = true
}

function confirmSplit() {
  if (!splitTarget.value || !segmentData.value) return
  const seg = splitTarget.value
  const pos = splitPosition.value

  if (pos <= 0 || pos >= seg.text.length) {
    ElMessage.warning('拆分位置无效')
    return
  }

  corrections.value.push({
    originalSentenceIds: [seg.sentenceId],
    action: 'split',
    splitPosition: pos
  })

  const idx = segmentData.value.segments.findIndex(s => s.sentenceId === seg.sentenceId)
  if (idx < 0) return

  const part1: SegmentDto = {
    id: seg.id + '-part1',
    sentenceId: seg.sentenceId + '-a',
    segmentType: seg.segmentType,
    text: seg.text.slice(0, pos),
    startOffset: seg.startOffset,
    endOffset: seg.startOffset + pos,
    pageNumber: seg.pageNumber
  }
  const part2: SegmentDto = {
    id: seg.id + '-part2',
    sentenceId: seg.sentenceId + '-b',
    segmentType: seg.segmentType,
    text: seg.text.slice(pos),
    startOffset: seg.startOffset + pos,
    endOffset: seg.endOffset,
    pageNumber: seg.pageNumber
  }

  const newSegments = [...segmentData.value.segments]
  newSegments.splice(idx, 1, part1, part2)
  segmentData.value = { ...segmentData.value, segments: newSegments, totalCount: newSegments.length }
  showSplitDialog.value = false
  ElMessage.success(`已拆分 ${seg.sentenceId}`)
}

function startSplitMerge(seg: SegmentDto) {
  splitMergeTarget.value = seg
  splitMergePosition.value = Math.floor(seg.text.length / 2)
  splitMergeWithPrev.value = true
  splitMergeWithNext.value = true
  showSplitMergeDialog.value = true
}

function confirmSplitMerge() {
  if (!splitMergeTarget.value || !segmentData.value) return
  const seg = splitMergeTarget.value
  const pos = splitMergePosition.value
  const withPrev = splitMergeWithPrev.value
  const withNext = splitMergeWithNext.value

  if (pos <= 0 || pos >= seg.text.length) {
    ElMessage.warning('拆分位置无效')
    return
  }

  if (!withPrev && !withNext) {
    ElMessage.warning('请至少选择合并一个方向')
    return
  }

  const segments = segmentData.value.segments
  const idx = segments.findIndex(s => s.sentenceId === seg.sentenceId)
  if (idx < 0) return

  const prevSeg = idx > 0 ? segments[idx - 1] : null
  const nextSeg = idx < segments.length - 1 ? segments[idx + 1] : null

  if (withPrev && !prevSeg) {
    ElMessage.warning('该段落是第一段，无法与上一段合并')
    return
  }
  if (withNext && !nextSeg) {
    ElMessage.warning('该段落是最后一段，无法与下一段合并')
    return
  }

  // Record the correction
  corrections.value.push({
    originalSentenceIds: [seg.sentenceId],
    action: 'splitMerge',
    splitPosition: pos,
    mergeFirstWithPrevious: withPrev,
    mergeSecondWithNext: withNext
  })

  const part1Text = seg.text.slice(0, pos)
  const part2Text = seg.text.slice(pos)

  // Build new segments list
  const newSegments: SegmentDto[] = []
  for (let i = 0; i < segments.length; i++) {
    if (i === idx) continue // Skip the target segment

    if (withPrev && i === idx - 1) {
      // Merge previous with first part
      newSegments.push({
        ...segments[i],
        text: segments[i].text + part1Text,
        endOffset: segments[i].endOffset + pos
      })
    } else if (withNext && i === idx + 1) {
      // Merge next with second part
      newSegments.push({
        ...segments[i],
        text: part2Text + segments[i].text,
        startOffset: segments[i].startOffset - (seg.text.length - pos)
      })
    } else {
      newSegments.push(segments[i])
    }
  }

  // If not merging with prev, keep first part as independent segment
  if (!withPrev) {
    newSegments.splice(idx, 0, {
      id: seg.id + '-part1',
      sentenceId: seg.sentenceId + '-a',
      segmentType: seg.segmentType,
      text: part1Text,
      startOffset: seg.startOffset,
      endOffset: seg.startOffset + pos,
      pageNumber: seg.pageNumber
    })
  }

  // If not merging with next, keep second part as independent segment
  if (!withNext) {
    const insertIdx = newSegments.findIndex(s => s.sentenceId === (withPrev ? prevSeg!.sentenceId : seg.sentenceId + '-a'))
    newSegments.splice(insertIdx + 1, 0, {
      id: seg.id + '-part2',
      sentenceId: seg.sentenceId + '-b',
      segmentType: seg.segmentType,
      text: part2Text,
      startOffset: seg.startOffset + pos,
      endOffset: seg.endOffset,
      pageNumber: seg.pageNumber
    })
  }

  newSegments.sort((a, b) => a.startOffset - b.startOffset)
  segmentData.value = { ...segmentData.value, segments: newSegments, totalCount: newSegments.length }
  showSplitMergeDialog.value = false
  ElMessage.success(`已拆分并合并 ${seg.sentenceId}`)
}

function undoLastCorrection() {
  if (corrections.value.length === 0) return
  // Clear all corrections — individual undo is unsafe because remaining
  // corrections reference stale sentence IDs after segment reload
  corrections.value = []
  // Reload segments from server to restore clean state
  if (segmentData.value) {
    const docId = segmentData.value.documentId
    client.getDocumentSegments(docId).then(r => { segmentData.value = r.data })
  }
  ElMessage.info('已撤销所有操作')
}

function isCorrected(sentenceId: string): boolean {
  return corrections.value.some(c => c.originalSentenceIds.includes(sentenceId))
}

async function submitRefinement() {
  if (!segmentData.value || corrections.value.length === 0) return
  if (!confirm(`确定要提交 ${corrections.value.length} 条修正并重新拆分文档吗？此操作将替换所有现有分段。`)) return

  refining.value = true
  try {
    const response = await client.refineDocumentSegments(segmentData.value.documentId, corrections.value)
    ElMessage.success(response.data.message || '修正完成')
    corrections.value = []
    // Reload segments
    const refreshed = await client.getDocumentSegments(segmentData.value.documentId)
    segmentData.value = refreshed.data
  } catch (error) {
    handleApiError('修正失败', error)
  } finally {
    refining.value = false
  }
}
</script>

<template>
  <LoginPage v-if="!isAuthenticated" @login-success="handleLoginSuccess" />
  <div v-else class="admin-layout">
    <aside class="sidebar" :class="{ open: sidebarOpen, collapsed: sidebarCollapsed }">
      <div class="sidebar-header">
        <div class="sidebar-logo">DR</div>
        <span class="sidebar-title">{{ appTitle }}</span>
        <button class="sidebar-toggle" @click="toggleSidebar">
          <svg v-if="sidebarCollapsed" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="9 18 15 12 9 6" />
          </svg>
          <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="15 18 9 12 15 6" />
          </svg>
        </button>
      </div>
      <nav class="sidebar-nav">
        <div class="nav-section">导航</div>
        <div v-for="item in navItems" :key="item.key" class="nav-item" :class="{ active: activeTab === item.key }" @click="activeTab = item.key; sidebarOpen = false">
          <span class="nav-icon">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path v-html="item.icon" />
            </svg>
          </span>
          <span class="nav-label">{{ item.label }}</span>
        </div>
      </nav>
      <div class="sidebar-footer">
        <div class="sidebar-footer-user">
          <div class="sidebar-footer-avatar">{{ displayName.charAt(0).toUpperCase() }}</div>
          <div class="sidebar-footer-info">
            <div class="sidebar-footer-name">{{ displayName }}</div>
            <div class="sidebar-footer-status">
              {{ lastRefreshTime ? `上次同步 ${lastRefreshTime}` : '会话活跃' }}
            </div>
          </div>
          <button class="sidebar-logout-btn" title="登出" @click="handleLogout">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
              <polyline points="16 17 21 12 16 7" />
              <line x1="21" y1="12" x2="9" y2="12" />
            </svg>
          </button>
        </div>
      </div>
    </aside>

    <div class="sidebar-overlay" :class="{ visible: sidebarOpen }" @click="sidebarOpen = false"></div>

    <div class="main-content" :class="{ 'sidebar-collapsed': sidebarCollapsed }">
      <header class="top-header">
        <div class="header-left">
          <button class="sidebar-toggle-btn" @click="sidebarCollapsed ? toggleSidebar() : (sidebarOpen = !sidebarOpen)">
            <svg v-if="sidebarCollapsed" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="9 18 15 12 9 6" />
            </svg>
            <svg v-else width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="3" y1="12" x2="21" y2="12" />
              <line x1="3" y1="6" x2="21" y2="6" />
              <line x1="3" y1="18" x2="21" y2="18" />
            </svg>
          </button>
          <span class="header-breadcrumb">{{ currentNavLabel }}</span>
        </div>
        <div class="header-actions">
          <button class="icon-btn" @click="loadDocuments">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="23 4 23 10 17 10" />
              <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
            </svg>
            刷新
          </button>
        </div>
      </header>

      <main class="content-area">
        <div v-if="activeTab === 'documents'" class="stats-bar">
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ total }}</span>
            <span class="stats-bar-label">文档总数</span>
          </div>
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ totalReady }}</span>
            <span class="stats-bar-label">已就绪</span>
          </div>
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ totalProcessing }}</span>
            <span class="stats-bar-label">处理中</span>
          </div>
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ totalFailed }}</span>
            <span class="stats-bar-label">失败</span>
          </div>
        </div>

        <div v-if="activeTab === 'documents'" class="page-header">
          <h1 class="page-title">文档管理</h1>
          <p class="page-subtitle">上传、管理和监控文档的处理状态</p>
        </div>

        <div v-if="activeTab === 'documents'" class="card table-card">
          <div class="card-header">
            <span>文档列表</span>
            <div class="card-header-actions">
              <button class="btn btn-primary btn-small" @click="showUploadDialog = true">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <line x1="12" y1="5" x2="12" y2="19" />
                  <line x1="5" y1="12" x2="19" y2="12" />
                </svg>
                上传文档
              </button>
            </div>
          </div>
          <div class="card-body">
            <div class="filter-bar filter-bar-compact">
              <div class="select-wrap filter-item">
                <select v-model="statusFilter" @change="handleFilterChange">
                  <option value="">所有状态</option>
                  <option value="pending">待处理</option>
                  <option value="processing">处理中</option>
                  <option value="ready">已就绪</option>
                  <option value="failed">失败</option>
                </select>
              </div>
              <div class="select-wrap filter-item">
                <select v-model="subjectFilter" @change="handleFilterChange">
                  <option value="">所有科目</option>
                  <option value="英语">英语</option>
                </select>
              </div>
              <div class="select-wrap filter-item">
                <select v-model="gradeFilter" @change="handleFilterChange">
                  <option value="">所有年级</option>
                  <option value="K">幼儿园</option>
                  <option value="G1">一年级</option>
                  <option value="G2">二年级</option>
                  <option value="G3">三年级</option>
                  <option value="G4">四年级</option>
                  <option value="G5">五年级</option>
                  <option value="G6">六年级</option>
                  <option value="G7">初一</option>
                  <option value="G8">初二</option>
                  <option value="G9">初三</option>
                  <option value="G10">高一</option>
                  <option value="G11">高二</option>
                  <option value="G12">高三</option>
                </select>
              </div>
              <div class="input-wrap filter-item filter-item-wide">
                <input v-model="keywordFilter" type="text" placeholder="搜索文档标题..." @keyup.enter="handleFilterChange" />
              </div>
              <div class="input-wrap filter-item filter-item-narrow">
                <input v-model="yearFilter" type="text" placeholder="年份" @keyup.enter="handleFilterChange" />
              </div>
            </div>

            <table v-if="!loadingDocuments" class="data-table">
              <thead>
                <tr>
                  <th>标题</th>
                  <th>科目</th>
                  <th>年级</th>
                  <th>年份</th>
                  <th>状态</th>
                  <th>创建时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="doc in documents" :key="doc.id">
                  <td>{{ doc.title }}</td>
                  <td>
                    <span v-if="doc.subject">{{ doc.subject }}</span>
                    <span v-else-if="doc.status === 'pending' || doc.status === 'processing'" class="tag tag-info" style="font-size: 11px">识别中</span>
                    <span v-else style="color: var(--text-muted)">-</span>
                  </td>
                  <td>
                    <span v-if="doc.grade">{{ doc.grade }}</span>
                    <span v-else-if="doc.status === 'pending' || doc.status === 'processing'" class="tag tag-info" style="font-size: 11px">识别中</span>
                    <span v-else style="color: var(--text-muted)">-</span>
                  </td>
                  <td>
                    <span v-if="doc.year">{{ doc.year }}</span>
                    <span v-else-if="doc.status === 'pending' || doc.status === 'processing'" class="tag tag-info" style="font-size: 11px">识别中</span>
                    <span v-else style="color: var(--text-muted)">-</span>
                  </td>
                  <td>
                    <span class="tag" :class="getStatusTagClass(doc.status)">
                      {{ getStatusLabel(doc.status) }}
                    </span>
                  </td>
                  <td>{{ formatDate(doc.createdAt) }}</td>
                  <td>
                    <div class="table-actions">
                      <button class="btn btn-link btn-small" @click="handleViewStatus(doc)">
                        查看状态
                      </button>
                      <button class="btn btn-link btn-small" :disabled="doc.status !== 'ready'" @click="openSegmentDialog(doc)">
                        分段管理
                      </button>
                      <button class="btn btn-link btn-small" @click="handleUpdateMetadata(doc)">
                        更新元数据
                      </button>
                      <button class="btn btn-link btn-link-danger btn-small" @click="confirmDelete(doc)">
                        删除
                      </button>
                    </div>
                  </td>
                </tr>
                <tr v-if="documents.length === 0">
                  <td colspan="7">
                    <div class="empty-state">
                      <div class="empty-state-icon">
                        <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                          <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
                          <circle cx="9" cy="7" r="4" />
                          <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
                          <path d="M16 3.13a4 4 0 0 1 0 7.75" />
                        </svg>
                      </div>
                      <div class="empty-state-text">暂无文档，点击上传开始使用</div>
                    </div>
                  </td>
                </tr>
              </tbody>
            </table>

            <div v-else class="empty-state">
              <svg class="spinner empty-spinner" viewBox="0 0 50 50">
                <circle cx="25" cy="25" r="20" fill="none" stroke="var(--primary-color)" stroke-width="4" stroke-linecap="round" stroke-dasharray="80" stroke-dashoffset="60">
                  <animateTransform attributeName="transform" type="rotate" from="0 25 25" to="360 25 25" dur="1s" repeatCount="indefinite" />
                </circle>
              </svg>
              <div class="empty-state-text">加载中...</div>
            </div>

            <div v-if="total > 0" class="pagination-bar">
              <span class="pagination-info">共 {{ total }} 条</span>
              <button class="page-btn" :disabled="page <= 1" @click="handlePageChange(page - 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <polyline points="15 18 9 12 15 6" />
                </svg>
              </button>
              <button v-for="p in pageNumbers" :key="p" class="page-btn" :class="{ active: page === p }" @click="handlePageChange(p)">
                {{ p }}
              </button>
              <button class="page-btn" :disabled="page >= totalPages" @click="handlePageChange(page + 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <polyline points="9 18 15 12 9 6" />
                </svg>
              </button>
            </div>
          </div>
        </div>

        <div v-if="activeTab === 'search'" class="page-header">
          <h1 class="page-title">检索测试</h1>
          <p class="page-subtitle">测试文档检索功能</p>
        </div>

        <div v-if="activeTab === 'search'" class="card">
          <div class="card-header">
            <span>检索测试</span>
          </div>
          <div class="card-body">
            <div style="display: flex; gap: 12px; margin-bottom: 16px; align-items: center">
              <div class="input-wrap" style="flex: 1">
                <input v-model="searchQuery" type="text" placeholder="输入单词或短语进行检索..." @keyup.enter="handleSearch" />
              </div>
              <label style="display: flex; align-items: center; gap: 4px; font-size: 13px; white-space: nowrap">
                <input v-model="searchPhrase" type="checkbox" />
                短语查询
              </label>
              <button class="btn btn-primary btn-small" :disabled="searchLoading || !searchQuery.trim()" @click="handleSearch">
                {{ searchLoading ? '搜索中...' : '搜索' }}
              </button>
            </div>

            <div v-if="searchResults.length > 0" style="margin-bottom: 12px; font-size: 13px; color: var(--text-secondary); display: flex; align-items: center; justify-content: space-between">
              <span>共 {{ searchTotalCount }} 条结果，关键词: <strong style="color: var(--text-primary)">{{ searchQuery }}</strong></span>
              <button class="btn btn-link btn-small" @click="clearSearch">清空</button>
            </div>

            <div v-if="searchResults.length > 0" style="overflow-x: auto">
              <table style="width: 100%; border-collapse: collapse; font-size: 13px">
                <thead>
                  <tr style="background: var(--bg-secondary); text-align: left">
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">#</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">文档标题</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">页码</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">匹配类型</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">相关度</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">匹配文本</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">Segment ID</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">偏移量</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light); white-space: nowrap">创建时间</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(result, idx) in searchResults" :key="idx" style="border-bottom: 1px solid var(--border-light)">
                    <td style="padding: 8px 12px; color: var(--text-muted)">{{ idx + 1 }}</td>
                    <td style="padding: 8px 12px; font-weight: 500; max-width: 200px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap" :title="result.documentName">{{ result.documentName }}</td>
                    <td style="padding: 8px 12px"><span class="tag tag-info" style="font-size: 11px">P{{ result.pageNumber }}</span></td>
                    <td style="padding: 8px 12px">
                      <span class="tag" :class="result.matchType === 'exact_phrase' ? 'tag-success' : result.matchType === 'exact_word' ? 'tag-info' : 'tag-warning'" style="font-size: 11px">
                        {{ result.matchType === 'exact_phrase' ? '精确短语' : result.matchType === 'exact_word' ? '精确词' : result.matchType === 'stem_match' ? '词干匹配' : result.matchType }}
                      </span>
                    </td>
                    <td style="padding: 8px 12px; font-family: monospace">{{ result.score.toFixed(2) }}</td>
                    <td style="padding: 8px 12px; max-width: 400px; line-height: 1.5; color: var(--text-secondary); white-space: pre-wrap; word-break: break-word">{{ result.associatedText }}</td>
                    <td style="padding: 8px 12px; font-family: monospace; font-size: 11px; color: var(--text-muted); max-width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap" :title="result.segmentId">{{ result.segmentId }}</td>
                    <td style="padding: 8px 12px; font-family: monospace; font-size: 11px; color: var(--text-muted); white-space: nowrap">{{ result.startOffset }}–{{ result.endOffset }}</td>
                    <td style="padding: 8px 12px; font-size: 12px; color: var(--text-muted); white-space: nowrap">{{ result.createdAt ? formatDate(result.createdAt) : '-' }}</td>
                  </tr>
                </tbody>
              </table>
            </div>

            <div v-else-if="searchQuery && !searchLoading" class="empty-state">
              <div class="empty-state-text">输入查询词后点击搜索</div>
            </div>
          </div>
        </div>

        <!-- Consistency Scan Tab -->
        <div v-if="activeTab === 'consistency'" class="page-header">
          <h1 class="page-title">一致性检查</h1>
          <p class="page-subtitle">扫描 OSS 文件与数据库记录的一致性</p>
        </div>

        <div v-if="activeTab === 'consistency'" class="card">
          <div class="card-header">
            <div style="display: flex; align-items: center; gap: 12px">
              <span>OSS-DB 一致性扫描</span>
              <span v-if="scanTime" style="font-size: 12px; color: var(--text-muted)">上次扫描: {{ scanTime }}</span>
            </div>
            <button class="btn btn-primary btn-small" :disabled="scanLoading" @click="handleScanConsistency">
              {{ scanLoading ? '扫描中...' : '开始扫描' }}
            </button>
          </div>
          <div class="card-body">
            <div v-if="scanResult" class="scan-summary" style="display: flex; gap: 24px; margin-bottom: 20px">
              <div class="stat-card">
                <div class="stat-value">{{ scanResult.summary.totalDocuments }}</div>
                <div class="stat-label">文档总数</div>
              </div>
              <div class="stat-card" :class="{ 'stat-danger': scanResult.summary.brokenCount > 0 }">
                <div class="stat-value">{{ scanResult.summary.brokenCount }}</div>
                <div class="stat-label">异常文档</div>
              </div>
              <div class="stat-card" :class="{ 'stat-warning': scanResult.summary.orphanCount > 0 }">
                <div class="stat-value">{{ scanResult.summary.orphanCount }}</div>
                <div class="stat-label">孤儿文件</div>
              </div>
            </div>

            <!-- Broken Documents -->
            <div v-if="scanResult && scanResult.brokenDocuments.length > 0" style="margin-bottom: 24px">
              <h3 style="font-size: 14px; font-weight: 600; margin-bottom: 12px; color: var(--danger)">
                ⚠ 异常文档（DB 有记录但 OSS 文件缺失）
              </h3>
              <table style="width: 100%; border-collapse: collapse; font-size: 13px">
                <thead>
                  <tr style="background: var(--bg-secondary); text-align: left">
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">标题</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">状态</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">OSS 路径</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">操作</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="doc in scanResult.brokenDocuments" :key="doc.id" style="border-bottom: 1px solid var(--border-light)">
                    <td style="padding: 8px 12px; font-weight: 500">{{ doc.title }}</td>
                    <td style="padding: 8px 12px"><span class="tag tag-warning">{{ doc.status }}</span></td>
                    <td style="padding: 8px 12px; font-family: monospace; font-size: 11px; color: var(--text-muted)">{{ doc.filePath }}</td>
                    <td style="padding: 8px 12px">
                      <button class="btn btn-danger btn-small" :disabled="forceDeleting === doc.id" @click="handleForceDelete(doc.id, doc.title)">
                        {{ forceDeleting === doc.id ? '删除中...' : '强制删除' }}
                      </button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>

            <!-- Orphan OSS Files -->
            <div v-if="scanResult && scanResult.orphanOssFiles.length > 0" style="margin-bottom: 24px">
              <h3 style="font-size: 14px; font-weight: 600; margin-bottom: 12px; color: var(--warning)">
                ⚠ 孤儿文件（OSS 有文件但 DB 无记录）
              </h3>
              <table style="width: 100%; border-collapse: collapse; font-size: 13px">
                <thead>
                  <tr style="background: var(--bg-secondary); text-align: left">
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">OSS 路径</th>
                    <th style="padding: 8px 12px; border-bottom: 2px solid var(--border-light)">操作</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="filePath in scanResult.orphanOssFiles" :key="filePath" style="border-bottom: 1px solid var(--border-light)">
                    <td style="padding: 8px 12px; font-family: monospace; font-size: 12px">{{ filePath }}</td>
                    <td style="padding: 8px 12px">
                      <button class="btn btn-warning btn-small" :disabled="deletingOrphan === filePath" @click="handleDeleteOrphan(filePath)">
                        {{ deletingOrphan === filePath ? '处理中...' : '标记处理' }}
                      </button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>

            <!-- No issues -->
            <div v-if="scanResult && scanResult.brokenDocuments.length === 0 && scanResult.orphanOssFiles.length === 0" class="empty-state">
              <div class="empty-state-text" style="color: var(--success)">✓ 所有文件与记录一致，无异常</div>
            </div>

            <!-- Initial state -->
            <div v-if="!scanResult && !scanLoading" class="empty-state">
              <div class="empty-state-text">点击"开始扫描"检查 OSS 文件与数据库记录的一致性</div>
            </div>
          </div>
        </div>
      </main>
    </div>

    <div v-if="showUploadDialog" class="dialog-overlay" @click.self="showUploadDialog = false">
      <div class="dialog">
        <div class="dialog-header">
          <div class="dialog-icon primary">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
              <polyline points="17 8 12 3 7 8" />
              <line x1="12" y1="3" x2="12" y2="15" />
            </svg>
          </div>
          上传文档
        </div>
        <div class="dialog-body">
          <div class="form-group">
            <label>文档标题 (选择文件后自动填充)</label>
            <div class="input-wrap">
              <input v-model="uploadForm.title" type="text" placeholder="选择文件后自动填充，可修改" />
            </div>
          </div>
          <div class="form-group">
            <label>选择文件</label>
            <div>
              <el-upload
                drag
                action="#"
                :auto-upload="false"
                :show-file-list="false"
                :on-change="handleFileChange"
                accept=".pdf,.doc,.docx,.ppt,.pptx"
              >
                <el-icon class="el-icon--upload"><upload /></el-icon>
                <div class="el-upload__text">
                  将文件拖到此处，或<em>点击上传</em>
                </div>
                <template #tip>
                  <div class="el-upload__tip">
                    支持 pdf、doc、docx、ppt、pptx 格式文件
                  </div>
                </template>
              </el-upload>
              <div v-if="uploadFile" style="margin-top: 8px; font-size: 13px; color: var(--text-secondary)">
                已选择: {{ uploadFile.name }}
              </div>
            </div>
          </div>
          <div class="form-group">
            <label>标签 (逗号分隔)</label>
            <div class="input-wrap">
              <input v-model="uploadForm.tags" type="text" placeholder="可选，用逗号分隔" />
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showUploadDialog = false; resetUploadForm()">
            取消
          </button>
          <button class="btn btn-primary btn-small" :disabled="uploading" @click="handleUpload">
            <svg v-if="uploading" class="spinner" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            {{ uploading ? '上传中...' : '上传' }}
          </button>
        </div>
      </div>
    </div>

    <div v-if="showStatusDialog" class="dialog-overlay" @click.self="closeStatusDialog">
      <div class="dialog">
        <div class="dialog-header">
          导入任务状态 - {{ selectedDocument?.title }}
        </div>
        <div class="dialog-body">
          <div v-if="viewingStatus" class="empty-state">
            <svg class="spinner empty-spinner" viewBox="0 0 50 50">
              <circle cx="25" cy="25" r="20" fill="none" stroke="var(--primary-color)" stroke-width="4" stroke-linecap="round" stroke-dasharray="80" stroke-dashoffset="60">
                <animateTransform attributeName="transform" type="rotate" from="0 25 25" to="360 25 25" dur="1s" repeatCount="indefinite" />
              </circle>
            </svg>
            <div class="empty-state-text">加载中...</div>
          </div>
          <div v-else-if="selectedDocumentStatus">
            <div class="form-group">
              <label>文档状态</label>
              <span class="tag" :class="getStatusTagClass(selectedDocumentStatus.status)" style="margin-left: 8px">
                {{ getStatusLabel(selectedDocumentStatus.status) }}
              </span>
            </div>
            <div class="form-group">
              <label>导入任务</label>
              <div v-for="job in selectedDocumentStatus.jobs" :key="job.jobId" style="padding: 8px 0; border-bottom: 1px solid var(--border-light)">
                <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px">
                  <span class="tag" :class="getStatusTagClass(job.status)">
                    {{ getStatusLabel(job.status) }}
                  </span>
                  <span style="font-size: 12px; color: var(--text-muted)">{{ formatDate(job.startedAt) || '-' }}</span>
                </div>
                <el-progress
                  v-if="job.status === 'processing'"
                  :percentage="job.progress || 0"
                  :stroke-width="14"
                  :text-inside="true"
                />
                <el-progress
                  v-else-if="job.status === 'failed' && job.progress != null"
                  :percentage="job.progress"
                  status="exception"
                  :stroke-width="14"
                  :text-inside="true"
                />
                <div v-if="job.progressStage" style="font-size: 12px; color: var(--text-muted); margin-top: 4px">
                  {{ stageLabels[job.progressStage] || job.progressStage }}
                </div>
                <div style="font-size: 12px; color: var(--text-secondary); margin-top: 4px">
                  解析器版本: {{ job.parserVersion || '-' }} | OCR 版本: {{ job.ocrVersion || '-' }}
                </div>
                <div v-if="job.errorMessage" style="font-size: 12px; color: var(--danger-color); margin-top: 4px">
                  错误: {{ job.errorMessage }}
                </div>
              </div>
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="closeStatusDialog">
            关闭
          </button>
          <button class="btn btn-primary btn-small" @click="loadDocuments; closeStatusDialog">
            刷新并关闭
          </button>
        </div>
      </div>
    </div>

    <div v-if="showUpdateMetadataDialog" class="dialog-overlay" @click.self="showUpdateMetadataDialog = false">
      <div class="dialog">
        <div class="dialog-header">
          更新元数据 - {{ selectedDocument?.title }}
        </div>
        <div class="dialog-body">
          <div class="form-group">
            <label>科目</label>
            <div class="select-wrap">
              <select v-model="metadataForm.subject">
                <option value="英语">英语</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>年级</label>
            <div class="select-wrap">
              <select v-model="metadataForm.grade">
                <option value="K">幼儿园</option>
                <option value="G1">一年级</option>
                <option value="G2">二年级</option>
                <option value="G3">三年级</option>
                <option value="G4">四年级</option>
                <option value="G5">五年级</option>
                <option value="G6">六年级</option>
                <option value="G7">初一</option>
                <option value="G8">初二</option>
                <option value="G9">初三</option>
                <option value="G10">高一</option>
                <option value="G11">高二</option>
                <option value="G12">高三</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>年份</label>
            <div class="input-wrap">
              <input v-model="metadataForm.year" type="text" />
            </div>
          </div>
          <div class="form-group">
            <label>标签 (逗号分隔)</label>
            <div class="input-wrap">
              <input v-model="metadataForm.tags" type="text" placeholder="可选，用逗号分隔" />
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showUpdateMetadataDialog = false">
            取消
          </button>
          <button class="btn btn-primary btn-small" @click="saveMetadata">
            保存
          </button>
        </div>
      </div>
    </div>

    <div v-if="showDeleteConfirm" class="dialog-overlay" @click.self="showDeleteConfirm = false">
      <div class="dialog dialog-narrow">
        <div class="dialog-header">
          <div class="dialog-icon warning">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
              <line x1="12" y1="9" x2="12" y2="13" />
              <line x1="12" y1="17" x2="12.01" y2="17" />
            </svg>
          </div>
          确认删除
        </div>
        <div class="dialog-body">
          <p>确定要删除文档 "{{ selectedDocument?.title }}" 吗？此操作不可撤销。</p>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showDeleteConfirm = false">
            取消
          </button>
          <button class="btn btn-danger btn-small" :disabled="deleting" @click="handleDelete">
            <svg v-if="deleting" class="spinner" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            {{ deleting ? '删除中...' : '删除' }}
          </button>
        </div>
      </div>
    </div>

    <!-- Segment Management Dialog -->
    <div v-if="showSegmentDialog" class="dialog-overlay" @click.self="showSegmentDialog = false">
      <div class="dialog dialog-wide">
        <div class="dialog-header">
          分段管理 — {{ segmentDocTitle }}
        </div>
        <div class="dialog-body">
          <div v-if="segmentLoading" class="empty-state">
            <svg class="spinner empty-spinner" viewBox="0 0 50 50">
              <circle cx="25" cy="25" r="20" fill="none" stroke="var(--primary-color)" stroke-width="4" stroke-linecap="round" stroke-dasharray="80" stroke-dashoffset="60">
                <animateTransform attributeName="transform" type="rotate" from="0 25 25" to="360 25 25" dur="1s" repeatCount="indefinite" />
              </circle>
            </svg>
            <div class="empty-state-text">加载中...</div>
          </div>
          <div v-else-if="segmentData">
            <!-- LLM Profile -->
            <div v-if="segmentData.profile" class="profile-section">
              <div class="profile-title">LLM 分析结果</div>
              <div class="profile-grid">
                <div class="profile-item"><span class="profile-label">学科</span><span>{{ segmentData.profile.subject }}</span></div>
                <div class="profile-item"><span class="profile-label">文档类型</span><span>{{ segmentData.profile.docType }}</span></div>
                <div class="profile-item"><span class="profile-label">分段策略</span><span class="tag tag-info">{{ segmentData.profile.segmentStrategy }}</span></div>
                <div class="profile-item">
                  <span class="profile-label">结构</span>
                  <span v-if="segmentData.profile.structure.hasChapters" class="tag tag-success" style="margin-right: 4px">章节</span>
                  <span v-if="segmentData.profile.structure.hasQuestions" class="tag tag-warning" style="margin-right: 4px">题目</span>
                  <span v-if="segmentData.profile.structure.hasWordList" class="tag tag-info" style="margin-right: 4px">单词表</span>
                  <span v-if="segmentData.profile.structure.hasFormulas" class="tag tag-danger">公式</span>
                  <span v-if="!segmentData.profile.structure.hasChapters && !segmentData.profile.structure.hasQuestions && !segmentData.profile.structure.hasWordList && !segmentData.profile.structure.hasFormulas">—</span>
                </div>
              </div>
            </div>
            <div v-else class="profile-section">
              <div class="empty-state" style="padding: 12px">
                <div class="empty-state-text">该文档无 LLM 分析记录（使用规则切割）</div>
              </div>
            </div>

            <!-- Toolbar -->
            <div class="segment-toolbar">
              <span class="segment-count">共 {{ segmentData.totalCount }} 条分段</span>
              <div class="segment-toolbar-actions">
                <button class="btn btn-secondary btn-small" @click="selectAllSegments">
                  {{ selectedSegmentIds.size === segmentData.segments.length ? '取消全选' : '全选' }}
                </button>
                <button class="btn btn-primary btn-small" :disabled="selectedSegmentIds.size < 2" @click="mergeSelected">
                  合并选中 ({{ selectedSegmentIds.size }})
                </button>
                <button class="btn btn-secondary btn-small" :disabled="corrections.length === 0" @click="undoLastCorrection">
                  撤销 ({{ corrections.length }})
                </button>
                <button class="btn btn-success btn-small" :disabled="corrections.length === 0 || refining" @click="submitRefinement">
                  {{ refining ? '提交中...' : `提交修正 (${corrections.length})` }}
                </button>
              </div>
            </div>

            <!-- Segment List -->
            <div class="segment-list">
              <div v-for="seg in segmentData.segments" :key="seg.sentenceId" class="segment-item" :class="{ selected: selectedSegmentIds.has(seg.sentenceId), corrected: isCorrected(seg.sentenceId) }">
                <div class="segment-header">
                  <input type="checkbox" :checked="selectedSegmentIds.has(seg.sentenceId)" @change="toggleSegmentSelect(seg.sentenceId)" />
                  <span class="segment-id">{{ seg.sentenceId }}</span>
                  <select :value="seg.segmentType" @change="changeSegmentType(seg.sentenceId, ($event.target as HTMLSelectElement).value)" class="segment-type-select">
                    <option value="sentence">sentence</option>
                    <option value="concept">concept</option>
                    <option value="word_entry">word_entry</option>
                    <option value="knowledge_point">knowledge_point</option>
                    <option value="question">question</option>
                  </select>
                  <span class="segment-page">P{{ seg.pageNumber }}</span>
                  <button class="btn btn-link btn-small" @click="startSplit(seg)">拆分</button>
                  <button class="btn btn-link btn-small" @click="startSplitMerge(seg)">拆分并合并</button>
                </div>
                <div class="segment-text">{{ seg.text }}</div>
              </div>
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showSegmentDialog = false">关闭</button>
        </div>
      </div>
    </div>

    <!-- Split Dialog -->
    <div v-if="showSplitDialog" class="dialog-overlay" @click.self="showSplitDialog = false">
      <div class="dialog dialog-narrow">
        <div class="dialog-header">拆分 Segment</div>
        <div class="dialog-body">
          <div v-if="splitTarget">
            <div class="split-preview">{{ splitTarget.text }}</div>
            <div class="form-group">
              <label>拆分位置（字符偏移）</label>
              <input type="range" v-model.number="splitPosition" :min="1" :max="splitTarget.text.length - 1" style="width: 100%" />
              <div style="display: flex; justify-content: space-between; font-size: 12px; color: var(--text-muted)">
                <span>0</span>
                <span>{{ splitPosition }}</span>
                <span>{{ splitTarget.text.length - 1 }}</span>
              </div>
            </div>
            <div class="split-result">
              <div class="split-part"><span class="split-label">前半部分：</span>{{ splitTarget.text.slice(0, splitPosition) }}</div>
              <div class="split-part"><span class="split-label">后半部分：</span>{{ splitTarget.text.slice(splitPosition) }}</div>
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showSplitDialog = false">取消</button>
          <button class="btn btn-primary btn-small" @click="confirmSplit">确认拆分</button>
        </div>
      </div>
    </div>

    <!-- Split and Merge Dialog -->
    <div v-if="showSplitMergeDialog" class="dialog-overlay" @click.self="showSplitMergeDialog = false">
      <div class="dialog dialog-narrow">
        <div class="dialog-header">拆分并合并</div>
        <div class="dialog-body">
          <div v-if="splitMergeTarget">
            <div class="split-preview">{{ splitMergeTarget.text }}</div>
            <div class="form-group">
              <label>拆分位置（字符偏移）</label>
              <input type="range" v-model.number="splitMergePosition" :min="1" :max="splitMergeTarget.text.length - 1" style="width: 100%" />
              <div style="display: flex; justify-content: space-between; font-size: 12px; color: var(--text-muted)">
                <span>0</span>
                <span>{{ splitMergePosition }}</span>
                <span>{{ splitMergeTarget.text.length - 1 }}</span>
              </div>
            </div>
            <div class="split-result">
              <div class="split-part"><span class="split-label">前半部分：</span>{{ splitMergeTarget.text.slice(0, splitMergePosition) }}</div>
              <div class="split-part"><span class="split-label">后半部分：</span>{{ splitMergeTarget.text.slice(splitMergePosition) }}</div>
            </div>
            <div class="form-group" style="margin-top: 16px">
              <label style="display: flex; align-items: center; gap: 8px">
                <input type="checkbox" v-model="splitMergeWithPrev" />
                前半部分与上一段合并
              </label>
              <label style="display: flex; align-items: center; gap: 8px; margin-top: 8px">
                <input type="checkbox" v-model="splitMergeWithNext" />
                后半部分与下一段合并
              </label>
            </div>
          </div>
        </div>
        <div class="dialog-footer">
          <button class="btn btn-secondary btn-small" @click="showSplitMergeDialog = false">取消</button>
          <button class="btn btn-primary btn-small" @click="confirmSplitMerge">确认</button>
        </div>
      </div>
    </div>
  </div>
</template>

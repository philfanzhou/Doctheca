<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { marked } from 'marked'
import {
  createDocApiClient,
  type SearchResult,
  type ConsistencyScanResult,
  type DocumentFile,
  type DocumentFileDetail,
  type DocumentParse,
  type Document,
  type DocumentStatus,
  type DocumentSegmentsData,
  type SegmentDto,
  type CorrectionDto
} from './services/docApi'
import { authService } from './services/authService'
import LoginPage from './components/LoginPage.vue'

const isAuthenticated = ref(false)
const appTitle = ref('DocRetrieval Admin')
const activeTab = ref('files')

// ===== Search =====
const searchQuery = ref('')
const searchPhrase = ref(false)
const searchLoading = ref(false)
const searchResults = ref<SearchResult[]>([])
const searchTotalCount = ref(0)
const searchNextToken = ref('')

// ===== Consistency Scan =====
const scanLoading = ref(false)
const scanResult = ref<ConsistencyScanResult | null>(null)
const scanTime = ref('')
const deletingOrphan = ref<string | null>(null)
const forceDeleting = ref<string | null>(null)

// ===== File Management =====
const fileList = ref<DocumentFile[]>([])
const fileTotal = ref(0)
const filePage = ref(1)
const filePageSize = ref(20)
const selectedFileDetail = ref<DocumentFileDetail | null>(null)
const fileUploadInput = ref<HTMLInputElement | null>(null)
const fileUploading = ref(false)
const fileUploadProgress = ref(0)
const filePollTimer = ref<number | null>(null)

// ===== MinerU Parse Page =====
const parseViewMode = ref<'unparsed' | 'all'>('unparsed')
const parseFileList = ref<DocumentFile[]>([])
const parseFileTotal = ref(0)
const parseFilePage = ref(1)
const parseFilePageSize = ref(20)
const parsePollTimer = ref<number | null>(null)
const parseFileParsing = ref(false)

// ===== Markdown Data Page =====
const parseList = ref<DocumentParse[]>([])
const parseTotal = ref(0)
const parsePage = ref(1)
const parsePageSize = ref(20)
const parseSearch = ref('')
const parseDeleting = ref<string | null>(null)

// ===== Document Management (Old LLM Segmentation) =====
const documents = ref<Document[]>([])
const docTotal = ref(0)
const docPage = ref(1)
const docPageSize = ref(20)
const docStatusFilter = ref('')
const docSubjectFilter = ref('')
const docGradeFilter = ref('')
const docKeywordFilter = ref('')
const docYearFilter = ref('')
const docLoading = ref(false)
const docUploading = ref(false)
const docDeleting = ref<string | null>(null)
const selectedDocument = ref<Document | null>(null)
const selectedDocumentStatus = ref<DocumentStatus | null>(null)
const statusPollTimer = ref<number | null>(null)
const showUploadDialog = ref(false)
const showStatusDialog = ref(false)
const showUpdateMetadataDialog = ref(false)
const uploadForm = ref({ title: '', subject: '英语', grade: '', year: '', tags: '' })
const metadataForm = ref({ subject: '', grade: '', year: '', tags: '' })

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

// ===== Sidebar =====
const sidebarOpen = ref(false)
const sidebarCollapsed = ref(localStorage.getItem('docSidebarCollapsed') === 'true')
const lastRefreshTime = ref('')

const client = createDocApiClient()

const currentUser = computed(() => authService.getUser())
const displayName = computed(() => currentUser.value?.username ?? '管理员')

const navItems = [
  {
    key: 'files',
    label: '文件管理',
    icon: '<path d="M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><polyline points="13 2 13 9 20 9"/>'
  },
  {
    key: 'parses',
    label: 'MinerU 解析',
    icon: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/>'
  },
  {
    key: 'markdown',
    label: 'Markdown 数据',
    icon: '<path d="M15.5 13.333l1.533 1.322c.645.555.967.833.967 1.178s-.322.623-.967 1.179L15.5 18.333m-3.333-5l-1.534 1.322c-.644.555-.966.833-.966 1.178s.322.623.966 1.179l1.534 1.321"/><path d="M17.167 10.836v-4.32c0-1.41 0-2.117-.224-2.68-.359-.906-1.118-1.621-2.08-1.96-.599-.21-1.349-.21-2.848-.21-2.623 0-3.935 0-4.983.369-1.684.591-3.013 1.842-3.641 3.428C3 6.449 3 7.684 3 10.154v2.122c0 2.558 0 3.838.706 4.726q.306.383.713.671c.76.536 1.79.64 3.581.66"/>'
  },
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

const fileTotalPages = computed(() => Math.ceil(fileTotal.value / filePageSize.value))
const parseFileTotalPages = computed(() => Math.ceil(parseFileTotal.value / parseFilePageSize.value))
const parseTotalPages = computed(() => Math.ceil(parseTotal.value / parsePageSize.value))

const docTotalPages = computed(() => Math.ceil(docTotal.value / docPageSize.value))

const totalReady = computed(() => documents.value.filter(d => d.status === 'ready').length)
const totalProcessing = computed(() => documents.value.filter(d => d.status === 'processing').length)
const totalFailed = computed(() => documents.value.filter(d => d.status === 'failed').length)

// ===== Helper Functions =====

function toggleSidebar() {
  sidebarCollapsed.value = !sidebarCollapsed.value
  localStorage.setItem('docSidebarCollapsed', String(sidebarCollapsed.value))
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString('zh-CN')
}

function renderMarkdown(content: string): string {
  return marked.parse(content, { async: false }) as string
}

function getFileStatusLabel(status: string | null): string {
  const labels: Record<string, string> = {
    pending: '等待解析',
    parsing: '解析中',
    parsed: '已解析',
    failed: '解析失败',
  }
  if (status === null) return '未解析'
  return labels[status] || status
}

function getFileStatusClass(status: string | null): string {
  if (status === 'parsed') return 'status-success'
  if (status === 'failed') return 'status-error'
  if (status === 'parsing' || status === 'pending') return 'status-processing'
  return 'status-pending'
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

function handleApiError(prefix: string, error: unknown) {
  console.error(prefix, error)
}

// ===== File Management Functions =====

async function loadFileList() {
  try {
    const response = await client.listDocumentFiles(filePage.value, filePageSize.value)
    fileList.value = response.data as DocumentFile[]
    fileTotal.value = response.total
  } catch (e) {
    console.error('Failed to load file list', e)
  }
}

function handleFilePageChange(newPage: number) {
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

async function handleViewFile(id: string) {
  try {
    const response = await client.getDocumentFile(id)
    selectedFileDetail.value = response.data as DocumentFileDetail
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Failed to load file'
    alert(msg)
  }
}

async function handleDeleteFile(id: string) {
  if (!confirm('确定删除此文件？')) return
  try {
    await client.deleteDocumentFile(id)
    if (selectedFileDetail.value?.id === id) selectedFileDetail.value = null
    if (fileList.value.length <= 1 && filePage.value > 1) {
      filePage.value--
    }
    await loadFileList()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Delete failed'
    alert(msg)
  }
}

function startFilePolling() {
  if (filePollTimer.value !== null) return
  filePollTimer.value = window.setInterval(async () => {
    await loadFileList()
    const hasActive = fileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
    if (!hasActive) {
      stopFilePolling()
    }
  }, 5000)
}

function stopFilePolling() {
  if (filePollTimer.value !== null) {
    clearInterval(filePollTimer.value)
    filePollTimer.value = null
  }
}

// ===== MinerU Parse Page Functions =====

async function loadParseFileList() {
  try {
    const status = parseViewMode.value === 'unparsed' ? 'unparsed' : undefined
    const response = await client.listDocumentFiles(parseFilePage.value, parseFilePageSize.value, status)
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

// ===== Markdown Data Page Functions =====

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

// ===== Document Management Functions =====

async function loadDocuments() {
  docLoading.value = true
  try {
    const response = await client.listDocuments(
      docPage.value,
      docPageSize.value,
      docStatusFilter.value || undefined,
      docSubjectFilter.value || undefined,
      docGradeFilter.value || undefined,
      docKeywordFilter.value || undefined,
      docYearFilter.value || undefined
    )
    documents.value = response.data as Document[]
    docTotal.value = response.total
  } catch (e) {
    console.error('Failed to load documents', e)
  } finally {
    docLoading.value = false
  }
}

function handleDocPageChange(newPage: number) {
  docPage.value = newPage
  loadDocuments()
}

async function handleUploadDocument() {
  if (!uploadForm.value.title || !uploadForm.value.subject) {
    alert('请填写标题和学科')
    return
  }
  docUploading.value = true
  try {
    const fileInput = document.querySelector('#doc-upload-input') as HTMLInputElement
    const file = fileInput?.files?.[0]
    if (!file) {
      alert('请选择文件')
      return
    }
    const tags = uploadForm.value.tags ? uploadForm.value.tags.split(',').map(t => t.trim()).filter(Boolean) : undefined
    await client.uploadDocument(
      file,
      uploadForm.value.title,
      uploadForm.value.subject,
      uploadForm.value.grade,
      uploadForm.value.year,
      tags
    )
    showUploadDialog.value = false
    uploadForm.value = { title: '', subject: '英语', grade: '', year: '', tags: '' }
    await loadDocuments()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Upload failed'
    alert(msg)
  } finally {
    docUploading.value = false
  }
}

async function handleViewDocStatus(doc: Document) {
  selectedDocument.value = doc
  showStatusDialog.value = true
  try {
    const response = await client.getDocumentStatus(doc.id)
    selectedDocumentStatus.value = response.data
    if (response.data.jobs?.[0]?.status === 'processing') {
      startStatusPolling(doc.id)
    }
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Failed to load status'
    alert(msg)
  }
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
      console.warn('Status polling failed', e)
    }
  }, 2000)
}

function stopStatusPolling() {
  if (statusPollTimer.value !== null) {
    clearInterval(statusPollTimer.value)
    statusPollTimer.value = null
  }
}

async function handleDeleteDocument(doc: Document) {
  if (!confirm(`确定删除文档「${doc.title}」？此操作不可恢复。`)) return
  docDeleting.value = doc.id
  try {
    await client.deleteDocument(doc.title)
    await loadDocuments()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Delete failed'
    alert(msg)
  } finally {
    docDeleting.value = null
  }
}

async function handleUpdateMetadata() {
  if (!selectedDocument.value) return
  try {
    const tags = metadataForm.value.tags ? metadataForm.value.tags.split(',').map(t => t.trim()).filter(Boolean) : undefined
    await client.updateMetadata(selectedDocument.value.title, {
      subject: metadataForm.value.subject || undefined,
      grade: metadataForm.value.grade || undefined,
      year: metadataForm.value.year || undefined,
      tags
    })
    showUpdateMetadataDialog.value = false
    await loadDocuments()
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Update failed'
    alert(msg)
  }
}

function openMetadataDialog(doc: Document) {
  selectedDocument.value = doc
  metadataForm.value = {
    subject: doc.subject || '',
    grade: doc.grade || '',
    year: doc.year || '',
    tags: doc.tags || ''
  }
  showUpdateMetadataDialog.value = true
}

// Segment functions
async function handleViewSegments(doc: Document) {
  showSegmentDialog.value = true
  segmentLoading.value = true
  segmentDocTitle.value = doc.title
  selectedSegmentIds.value = new Set()
  corrections.value = []
  try {
    const response = await client.getDocumentSegments(doc.id)
    segmentData.value = response.data
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Failed to load segments'
    alert(msg)
  } finally {
    segmentLoading.value = false
  }
}

function toggleSegmentSelection(segId: string) {
  const s = new Set(selectedSegmentIds.value)
  if (s.has(segId)) s.delete(segId)
  else s.add(segId)
  selectedSegmentIds.value = s
}

function handleSplitSegment(seg: SegmentDto) {
  splitTarget.value = seg
  splitPosition.value = Math.floor(seg.text.length / 2)
  showSplitDialog.value = true
}

function handleSplitMergeSegment(seg: SegmentDto) {
  splitMergeTarget.value = seg
  splitMergePosition.value = Math.floor(seg.text.length / 2)
  splitMergeWithPrev.value = true
  splitMergeWithNext.value = true
  showSplitMergeDialog.value = true
}

function confirmSplit() {
  if (!splitTarget.value) return
  corrections.value.push({
    originalSentenceIds: [splitTarget.value.sentenceId],
    action: 'split',
    splitPosition: splitPosition.value,
    newSegmentType: splitTarget.value.segmentType
  })
  showSplitDialog.value = false
  splitTarget.value = null
}

function confirmSplitMerge() {
  if (!splitMergeTarget.value) return
  corrections.value.push({
    originalSentenceIds: [splitMergeTarget.value.sentenceId],
    action: 'splitMerge',
    splitPosition: splitMergePosition.value,
    newSegmentType: splitMergeTarget.value.segmentType,
    mergeFirstWithPrevious: splitMergeWithPrev.value,
    mergeSecondWithNext: splitMergeWithNext.value
  })
  showSplitMergeDialog.value = false
  splitMergeTarget.value = null
}

function handleMergeSelected() {
  if (selectedSegmentIds.value.size < 2) {
    alert('请至少选择 2 个分段进行合并')
    return
  }
  corrections.value.push({
    originalSentenceIds: Array.from(selectedSegmentIds.value),
    action: 'merge'
  })
  selectedSegmentIds.value = new Set()
}

async function handleRefineSegments() {
  if (corrections.value.length === 0) {
    alert('请先添加修改操作')
    return
  }
  if (!segmentData.value) return
  refining.value = true
  try {
    await client.refineDocumentSegments(segmentData.value.documentId, corrections.value)
    corrections.value = []
    // Reload segments
    const response = await client.getDocumentSegments(segmentData.value.documentId)
    segmentData.value = response.data
    alert('分段精炼完成')
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'Refine failed'
    alert(msg)
  } finally {
    refining.value = false
  }
}

function getStatusTagClass(status: string) {
  switch (status) {
    case 'ready': return 'status-success'
    case 'failed': return 'status-error'
    case 'processing': return 'status-processing'
    default: return 'status-pending'
  }
}

function getStatusLabel(status: string) {
  const labels: Record<string, string> = {
    ready: '就绪',
    processing: '处理中',
    failed: '失败',
    pending: '等待中'
  }
  return labels[status] || status
}

const stageLabels: Record<string, string> = {
  pending: '等待处理',
  starting: '启动中',
  downloading: '下载文件',
  analyzing: '文档分析',
  parsing: '解析内容',
  writing_pages: '写入页面',
  writing_segments: '写入分段',
  indexing: '建立索引',
  completed: '已完成',
}

// ===== Search Functions =====

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

// ===== Consistency Scan Functions =====

async function handleScanConsistency() {
  scanLoading.value = true
  scanResult.value = null
  try {
    const response = await client.scanConsistency()
    scanResult.value = response.data
    scanTime.value = new Date().toLocaleTimeString()
    const s = response.data.summary
    console.log(`Scan complete: ${s.totalDocuments} docs, ${s.brokenCount} broken, ${s.orphanCount} orphans`)
  } catch (error) {
    handleApiError('扫描失败', error)
  } finally {
    scanLoading.value = false
  }
}

async function handleDeleteOrphan(filePath: string) {
  deletingOrphan.value = filePath
  try {
    console.log(`Orphan file: ${filePath}`)
  } finally {
    deletingOrphan.value = null
  }
}

async function handleForceDelete(docId: string, title: string) {
  if (!confirm(`确定强制删除文档「${title}」？此操作不可恢复。`)) return
  forceDeleting.value = docId
  try {
    await client.forceDeleteDocument(docId)
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

// ===== Auth =====

function handleLoginSuccess() {
  isAuthenticated.value = true
  loadFileList()
}

async function handleLogout() {
  await authService.logout()
  isAuthenticated.value = false
}

// ===== Watchers =====

watch(activeTab, (tab) => {
  if (tab === 'files') {
    loadFileList()
    const hasActive = fileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
    if (hasActive) startFilePolling()
  } else {
    stopFilePolling()
  }
  if (tab === 'parses') {
    loadParseFileList()
    const hasActive = parseFileList.value.some(f => f.parseStatus === 'pending' || f.parseStatus === 'parsing')
    if (hasActive) startParsePolling()
  } else {
    stopParsePolling()
  }
  if (tab === 'markdown') {
    loadParseList()
  }
  if (tab === 'documents') {
    loadDocuments()
  } else {
    stopStatusPolling()
  }
})

// ===== Lifecycle =====

onMounted(async () => {
  if (authService.isAuthenticated()) {
    isAuthenticated.value = true
    loadFileList()
  } else if (authService.canRefresh()) {
    const newTokens = await authService.refresh()
    if (newTokens) {
      isAuthenticated.value = true
      loadFileList()
    }
  }
})

onUnmounted(() => {
  stopFilePolling()
  stopParsePolling()
  stopStatusPolling()
})
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
          <button class="icon-btn" @click="loadFileList">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="23 4 23 10 17 10" />
              <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
            </svg>
            刷新
          </button>
        </div>
      </header>

      <main class="content-area">
        <!-- ===== Files Tab ===== -->
        <div v-if="activeTab === 'files'" class="page-header">
          <h1 class="page-title">文件管理</h1>
          <p class="page-subtitle">上传文件到 S3，管理文件和解析状态</p>
        </div>

        <div v-if="activeTab === 'files'" class="card">
          <div class="card-header">
            <span>文件列表</span>
            <div style="display: flex; align-items: center; gap: 12px">
              <button class="btn btn-primary btn-small" :disabled="fileUploading" @click="triggerFileUpload">
                {{ fileUploading ? '上传中...' : '上传文件' }}
              </button>
              <input ref="fileUploadInput" type="file" accept=".pdf,.docx,.doc,.pptx,.ppt" style="display: none" @change="handleFileUploadChange" />
            </div>
          </div>
          <div class="card-body">
            <!-- Upload Progress -->
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
                  <td>
                    <span style="cursor: pointer; color: var(--primary)" @click="handleViewFile(f.id)">{{ f.fileName }}</span>
                  </td>
                  <td>{{ f.contentType }}</td>
                  <td>{{ formatTime(f.createdAt) }}</td>
                  <td>
                    <span class="status-badge" :class="getFileStatusClass(f.parseStatus)">{{ getFileStatusLabel(f.parseStatus) }}</span>
                  </td>
                  <td>
                    <button class="btn btn-secondary btn-small" style="color: var(--danger)" @click="handleDeleteFile(f.id)">删除</button>
                  </td>
                </tr>
              </tbody>
            </table>
            <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无文件，点击上方按钮上传</div>

            <!-- Pagination -->
            <div v-if="fileTotal > filePageSize" class="pagination-bar">
              <span class="pagination-info">共 {{ fileTotal }} 条</span>
              <button class="page-btn" :disabled="filePage <= 1" @click="handleFilePageChange(filePage - 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
              </button>
              <button v-for="p in fileTotalPages" :key="p" class="page-btn" :class="{ active: filePage === p }" @click="handleFilePageChange(p)">{{ p }}</button>
              <button class="page-btn" :disabled="filePage >= fileTotalPages" @click="handleFilePageChange(filePage + 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6" /></svg>
              </button>
            </div>
          </div>
        </div>

        <!-- File Detail Dialog -->
        <div v-if="selectedFileDetail && activeTab === 'files'" class="card" style="margin-top: 16px">
          <div class="card-header">
            <span>{{ selectedFileDetail.fileName }} — Markdown 预览</span>
            <button class="btn btn-secondary btn-small" @click="selectedFileDetail = null">关闭</button>
          </div>
          <div class="card-body">
            <div v-if="selectedFileDetail.parse?.errorMessage" style="margin-bottom: 12px; padding: 12px; background: #fef2f2; border: 1px solid #fecaca; border-radius: 8px; color: var(--danger); font-size: 13px">
              {{ selectedFileDetail.parse.errorMessage }}
            </div>
            <div v-if="selectedFileDetail.parse?.markdownContent" class="markdown-preview" v-html="renderMarkdown(selectedFileDetail.parse.markdownContent)"></div>
            <div v-else-if="selectedFileDetail.parse?.images && selectedFileDetail.parse.images.length > 0">
              <div style="display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 12px">
                <div v-for="img in selectedFileDetail.parse.images" :key="img.id" style="border: 1px solid var(--border-light); border-radius: 8px; overflow: hidden">
                  <img :src="img.imageUrl" :alt="img.imageName" style="width: 100%; display: block" />
                  <div style="padding: 6px 8px; font-size: 11px; color: var(--text-muted)">{{ img.imageName }}</div>
                </div>
              </div>
            </div>
            <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无 Markdown 内容</div>
          </div>
        </div>

        <!-- ===== Parses Tab ===== -->
        <div v-if="activeTab === 'parses'" class="page-header">
          <h1 class="page-title">MinerU 解析</h1>
          <p class="page-subtitle">管理文件的 MinerU 解析任务</p>
        </div>

        <div v-if="activeTab === 'parses'" class="card">
          <div class="card-header">
            <span>解析管理</span>
            <div style="display: flex; align-items: center; gap: 8px">
              <div style="display: flex; border: 1px solid var(--border-light); border-radius: 6px; overflow: hidden">
                <button class="btn btn-small" :class="parseViewMode === 'unparsed' ? 'btn-primary' : 'btn-secondary'" @click="parseViewMode = 'unparsed'; parseFilePage = 1; loadParseFileList()" style="border: none; border-radius: 0">未解析</button>
                <button class="btn btn-small" :class="parseViewMode === 'all' ? 'btn-primary' : 'btn-secondary'" @click="parseViewMode = 'all'; parseFilePage = 1; loadParseFileList()" style="border: none; border-radius: 0">全部</button>
              </div>
            </div>
          </div>
          <div class="card-body">
            <table v-if="parseFileList.length > 0" class="data-table">
              <thead>
                <tr>
                  <th>文件名</th>
                  <th>解析状态</th>
                  <th>上传时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="f in parseFileList" :key="f.id">
                  <td>{{ f.fileName }}</td>
                  <td>
                    <span class="status-badge" :class="getFileStatusClass(f.parseStatus)">{{ getFileStatusLabel(f.parseStatus) }}</span>
                  </td>
                  <td>{{ formatTime(f.createdAt) }}</td>
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
            <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">
              {{ parseViewMode === 'unparsed' ? '没有未解析的文件' : '暂无文件' }}
            </div>

            <!-- Pagination -->
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

        <!-- ===== Markdown Data Tab ===== -->
        <div v-if="activeTab === 'markdown'" class="page-header">
          <h1 class="page-title">Markdown 数据</h1>
          <p class="page-subtitle">管理解析记录，导出 Markdown 和 HTML</p>
        </div>

        <div v-if="activeTab === 'markdown'" class="card">
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

            <!-- Pagination -->
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

        <!-- ===== Documents Tab (Old LLM Segmentation) ===== -->
        <div v-if="activeTab === 'documents'" class="page-header">
          <h1 class="page-title">文档管理</h1>
          <p class="page-subtitle">大模型拆段文档管理：上传、查看、元数据管理、分段精炼</p>
        </div>

        <div v-if="activeTab === 'documents'" class="stats-bar">
          <div class="stat-item">
            <span class="stat-value">{{ docTotal }}</span>
            <span class="stat-label">全部文档</span>
          </div>
          <div class="stat-item stat-success">
            <span class="stat-value">{{ totalReady }}</span>
            <span class="stat-label">就绪</span>
          </div>
          <div class="stat-item stat-warning">
            <span class="stat-value">{{ totalProcessing }}</span>
            <span class="stat-label">处理中</span>
          </div>
          <div class="stat-item stat-danger">
            <span class="stat-value">{{ totalFailed }}</span>
            <span class="stat-label">失败</span>
          </div>
        </div>

        <div v-if="activeTab === 'documents'" class="card">
          <div class="card-header">
            <span>文档列表</span>
            <div style="display: flex; align-items: center; gap: 8px">
              <select v-model="docStatusFilter" class="input-wrap" style="width: auto; padding: 4px 8px" @change="docPage = 1; loadDocuments()">
                <option value="">全部状态</option>
                <option value="ready">就绪</option>
                <option value="processing">处理中</option>
                <option value="failed">失败</option>
              </select>
              <select v-model="docSubjectFilter" class="input-wrap" style="width: auto; padding: 4px 8px" @change="docPage = 1; loadDocuments()">
                <option value="">全部学科</option>
                <option value="英语">英语</option>
                <option value="数学">数学</option>
                <option value="语文">语文</option>
              </select>
              <div class="input-wrap" style="width: 150px">
                <input v-model="docKeywordFilter" type="text" placeholder="搜索关键词..." @keyup.enter="docPage = 1; loadDocuments()" />
              </div>
              <button class="btn btn-primary btn-small" @click="showUploadDialog = true">上传文档</button>
            </div>
          </div>
          <div class="card-body">
            <table v-if="documents.length > 0" class="data-table">
              <thead>
                <tr>
                  <th>标题</th>
                  <th>学科</th>
                  <th>年级</th>
                  <th>状态</th>
                  <th>创建时间</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="doc in documents" :key="doc.id">
                  <td>{{ doc.title }}</td>
                  <td>{{ doc.subject || '-' }}</td>
                  <td>{{ doc.grade || '-' }}</td>
                  <td>
                    <span class="status-badge" :class="getStatusTagClass(doc.status)">{{ getStatusLabel(doc.status) }}</span>
                  </td>
                  <td>{{ formatTime(doc.createdAt) }}</td>
                  <td>
                    <div style="display: flex; gap: 6px">
                      <button class="btn btn-secondary btn-small" @click="handleViewDocStatus(doc)">状态</button>
                      <button class="btn btn-secondary btn-small" @click="openMetadataDialog(doc)">元数据</button>
                      <button v-if="doc.status === 'ready'" class="btn btn-secondary btn-small" @click="handleViewSegments(doc)">分段</button>
                      <button class="btn btn-secondary btn-small" style="color: var(--danger)" :disabled="docDeleting === doc.id" @click="handleDeleteDocument(doc)">
                        {{ docDeleting === doc.id ? '删除中...' : '删除' }}
                      </button>
                    </div>
                  </td>
                </tr>
              </tbody>
            </table>
            <div v-else style="text-align: center; padding: 40px; color: var(--text-muted)">暂无文档</div>

            <!-- Pagination -->
            <div v-if="docTotal > docPageSize" class="pagination-bar">
              <span class="pagination-info">共 {{ docTotal }} 条</span>
              <button class="page-btn" :disabled="docPage <= 1" @click="handleDocPageChange(docPage - 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
              </button>
              <button v-for="p in docTotalPages" :key="p" class="page-btn" :class="{ active: docPage === p }" @click="handleDocPageChange(p)">{{ p }}</button>
              <button class="page-btn" :disabled="docPage >= docTotalPages" @click="handleDocPageChange(docPage + 1)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6" /></svg>
              </button>
            </div>
          </div>
        </div>

        <!-- Upload Document Dialog -->
        <div v-if="showUploadDialog" class="modal-overlay" @click.self="showUploadDialog = false">
          <div class="modal-content" style="max-width: 500px">
            <div class="modal-header">
              <h3>上传文档</h3>
              <button class="modal-close" @click="showUploadDialog = false">&times;</button>
            </div>
            <div class="modal-body">
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">标题 *</label>
                <input v-model="uploadForm.title" type="text" class="input-wrap" placeholder="文档标题" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">学科 *</label>
                <select v-model="uploadForm.subject" class="input-wrap">
                  <option value="英语">英语</option>
                  <option value="数学">数学</option>
                  <option value="语文">语文</option>
                </select>
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">年级</label>
                <input v-model="uploadForm.grade" type="text" class="input-wrap" placeholder="如：三年级" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">年份</label>
                <input v-model="uploadForm.year" type="text" class="input-wrap" placeholder="如：2024" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">标签（逗号分隔）</label>
                <input v-model="uploadForm.tags" type="text" class="input-wrap" placeholder="如：阅读,语法" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">文件 *</label>
                <input id="doc-upload-input" type="file" accept=".pdf,.docx,.doc,.pptx,.ppt" class="input-wrap" />
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showUploadDialog = false">取消</button>
              <button class="btn btn-primary" :disabled="docUploading" @click="handleUploadDocument">
                {{ docUploading ? '上传中...' : '上传' }}
              </button>
            </div>
          </div>
        </div>

        <!-- Status Dialog -->
        <div v-if="showStatusDialog" class="modal-overlay" @click.self="showStatusDialog = false; stopStatusPolling()">
          <div class="modal-content" style="max-width: 600px">
            <div class="modal-header">
              <h3>文档状态 — {{ selectedDocument?.title }}</h3>
              <button class="modal-close" @click="showStatusDialog = false; stopStatusPolling()">&times;</button>
            </div>
            <div class="modal-body">
              <div v-if="selectedDocumentStatus">
                <div style="margin-bottom: 12px">
                  <span class="status-badge" :class="getStatusTagClass(selectedDocumentStatus.status)">{{ getStatusLabel(selectedDocumentStatus.status) }}</span>
                </div>
                <div v-if="selectedDocumentStatus.jobs && selectedDocumentStatus.jobs.length > 0">
                  <div v-for="job in selectedDocumentStatus.jobs" :key="job.jobId" style="border: 1px solid var(--border-light); border-radius: 8px; padding: 12px; margin-bottom: 8px">
                    <div style="display: flex; justify-content: space-between; margin-bottom: 8px">
                      <span style="font-weight: 500">Job: {{ job.jobId.substring(0, 8) }}...</span>
                      <span class="status-badge" :class="getStatusTagClass(job.status)">{{ job.status }}</span>
                    </div>
                    <div v-if="job.progress > 0" style="margin-bottom: 8px">
                      <div style="height: 6px; background: var(--bg-secondary); border-radius: 3px; overflow: hidden">
                        <div style="height: 100%; background: var(--primary); border-radius: 3px; transition: width 0.3s" :style="{ width: job.progress + '%' }"></div>
                      </div>
                      <div style="font-size: 12px; color: var(--text-secondary); margin-top: 4px">
                        {{ job.progressStage ? (stageLabels[job.progressStage] || job.progressStage) : '' }} ({{ job.progress }}%)
                      </div>
                    </div>
                    <div v-if="job.errorMessage" style="font-size: 12px; color: var(--danger)">{{ job.errorMessage }}</div>
                  </div>
                </div>
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showStatusDialog = false; stopStatusPolling()">关闭</button>
            </div>
          </div>
        </div>

        <!-- Update Metadata Dialog -->
        <div v-if="showUpdateMetadataDialog" class="modal-overlay" @click.self="showUpdateMetadataDialog = false">
          <div class="modal-content" style="max-width: 500px">
            <div class="modal-header">
              <h3>更新元数据 — {{ selectedDocument?.title }}</h3>
              <button class="modal-close" @click="showUpdateMetadataDialog = false">&times;</button>
            </div>
            <div class="modal-body">
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">学科</label>
                <input v-model="metadataForm.subject" type="text" class="input-wrap" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">年级</label>
                <input v-model="metadataForm.grade" type="text" class="input-wrap" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">年份</label>
                <input v-model="metadataForm.year" type="text" class="input-wrap" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">标签（逗号分隔）</label>
                <input v-model="metadataForm.tags" type="text" class="input-wrap" />
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showUpdateMetadataDialog = false">取消</button>
              <button class="btn btn-primary" @click="handleUpdateMetadata">保存</button>
            </div>
          </div>
        </div>

        <!-- Segment Refinement Dialog -->
        <div v-if="showSegmentDialog" class="modal-overlay" @click.self="showSegmentDialog = false">
          <div class="modal-content" style="max-width: 900px; max-height: 80vh; display: flex; flex-direction: column">
            <div class="modal-header">
              <h3>分段精炼 — {{ segmentDocTitle }}</h3>
              <button class="modal-close" @click="showSegmentDialog = false">&times;</button>
            </div>
            <div class="modal-body" style="flex: 1; overflow-y: auto">
              <div v-if="segmentLoading" style="text-align: center; padding: 40px">加载中...</div>
              <div v-else-if="segmentData">
                <div v-if="segmentData.profile" style="margin-bottom: 16px; padding: 12px; background: var(--bg-secondary); border-radius: 8px; font-size: 13px">
                  <div><strong>学科:</strong> {{ segmentData.profile.subject }} | <strong>类型:</strong> {{ segmentData.profile.docType }} | <strong>策略:</strong> {{ segmentData.profile.segmentStrategy }}</div>
                  <div style="margin-top: 4px; color: var(--text-secondary)">共 {{ segmentData.totalCount }} 个分段</div>
                </div>

                <div v-if="corrections.length > 0" style="margin-bottom: 16px; padding: 12px; background: #fffbeb; border: 1px solid #fde68a; border-radius: 8px">
                  <div style="font-weight: 500; margin-bottom: 8px">待执行修改 ({{ corrections.length }})</div>
                  <div v-for="(c, i) in corrections" :key="i" style="font-size: 12px; margin-bottom: 4px">
                    {{ c.action }}: {{ c.originalSentenceIds.join(', ') }}
                    <button style="margin-left: 8px; color: var(--danger); background: none; border: none; cursor: pointer; font-size: 12px" @click="corrections.splice(i, 1)">撤销</button>
                  </div>
                  <button class="btn btn-primary btn-small" style="margin-top: 8px" :disabled="refining" @click="handleRefineSegments">
                    {{ refining ? '执行中...' : '执行修改' }}
                  </button>
                </div>

                <div style="margin-bottom: 12px; display: flex; gap: 8px">
                  <button class="btn btn-secondary btn-small" :disabled="selectedSegmentIds.size < 2" @click="handleMergeSelected">合并选中 ({{ selectedSegmentIds.size }})</button>
                </div>

                <div v-for="seg in segmentData.segments" :key="seg.id" style="border: 1px solid var(--border-light); border-radius: 6px; padding: 10px; margin-bottom: 8px; cursor: pointer" :class="{ 'selected': selectedSegmentIds.has(seg.sentenceId) }" @click="toggleSegmentSelection(seg.sentenceId)">
                  <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px">
                    <span style="font-size: 11px; color: var(--text-muted)">P{{ seg.pageNumber }} · {{ seg.segmentType }}</span>
                    <div style="display: flex; gap: 4px">
                      <button class="btn btn-secondary btn-small" style="font-size: 11px; padding: 2px 6px" @click.stop="handleSplitSegment(seg)">拆分</button>
                      <button class="btn btn-secondary btn-small" style="font-size: 11px; padding: 2px 6px" @click.stop="handleSplitMergeSegment(seg)">拆分合并</button>
                    </div>
                  </div>
                  <div style="font-size: 13px; line-height: 1.5; white-space: pre-wrap">{{ seg.text }}</div>
                </div>
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showSegmentDialog = false">关闭</button>
            </div>
          </div>
        </div>

        <!-- Split Dialog -->
        <div v-if="showSplitDialog" class="modal-overlay" @click.self="showSplitDialog = false">
          <div class="modal-content" style="max-width: 500px">
            <div class="modal-header">
              <h3>拆分段落</h3>
              <button class="modal-close" @click="showSplitDialog = false">&times;</button>
            </div>
            <div class="modal-body">
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">拆分位置（字符偏移）</label>
                <input v-model.number="splitPosition" type="number" class="input-wrap" min="1" :max="splitTarget?.text.length" />
              </div>
              <div v-if="splitTarget" style="padding: 12px; background: var(--bg-secondary); border-radius: 8px; font-size: 13px; max-height: 200px; overflow-y: auto">
                <span style="color: var(--primary)">{{ splitTarget.text.substring(0, splitPosition) }}</span><span style="color: var(--danger)">|</span><span>{{ splitTarget.text.substring(splitPosition) }}</span>
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showSplitDialog = false">取消</button>
              <button class="btn btn-primary" @click="confirmSplit">确认拆分</button>
            </div>
          </div>
        </div>

        <!-- Split Merge Dialog -->
        <div v-if="showSplitMergeDialog" class="modal-overlay" @click.self="showSplitMergeDialog = false">
          <div class="modal-content" style="max-width: 500px">
            <div class="modal-header">
              <h3>拆分并合并</h3>
              <button class="modal-close" @click="showSplitMergeDialog = false">&times;</button>
            </div>
            <div class="modal-body">
              <div style="margin-bottom: 12px">
                <label style="display: block; margin-bottom: 4px; font-size: 13px; font-weight: 500">拆分位置（字符偏移）</label>
                <input v-model.number="splitMergePosition" type="number" class="input-wrap" min="1" :max="splitMergeTarget?.text.length" />
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: flex; align-items: center; gap: 8px; font-size: 13px">
                  <input v-model="splitMergeWithPrev" type="checkbox" /> 前半部分与上一段合并
                </label>
              </div>
              <div style="margin-bottom: 12px">
                <label style="display: flex; align-items: center; gap: 8px; font-size: 13px">
                  <input v-model="splitMergeWithNext" type="checkbox" /> 后半部分与下一段合并
                </label>
              </div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" @click="showSplitMergeDialog = false">取消</button>
              <button class="btn btn-primary" @click="confirmSplitMerge">确认</button>
            </div>
          </div>
        </div>

        <!-- ===== Search Tab ===== -->
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

        <!-- ===== Consistency Scan Tab ===== -->
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
  </div>
</template>

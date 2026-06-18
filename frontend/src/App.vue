<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage, type UploadUserFile } from 'element-plus'
import { Upload } from '@element-plus/icons-vue'
import {
  createDocApiClient,
  getDocErrorMessage,
  type Document,
  type DocumentStatus,
  type SearchResult
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

const selectedDocument = ref<Document | null>(null)
const selectedDocumentStatus = ref<DocumentStatus | null>(null)
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
    updateRefreshTime()
  } catch (error) {
    handleApiError('加载文档列表失败', error)
  } finally {
    loadingDocuments.value = false
  }
}

async function handleUpload() {
  if (!uploadFile.value || !uploadForm.value.title || !uploadForm.value.subject || !uploadForm.value.grade) {
    ElMessage.warning('请填写所有必填字段并选择文件')
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
  } catch (error) {
    handleApiError('上传文档失败', error)
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
        <div class="stats-bar">
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ total }}</span>
            <span class="stats-bar-label">文档总数</span>
          </div>
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ documents.filter((d) => d.status === 'ready').length }}</span>
            <span class="stats-bar-label">已就绪</span>
          </div>
          <div class="stats-bar-item">
            <span class="stats-bar-value">{{ documents.filter((d) => d.status === 'processing').length }}</span>
            <span class="stats-bar-label">处理中</span>
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
            <div class="filter-bar">
              <div class="select-wrap" style="width: 160px">
                <select v-model="statusFilter" @change="handleFilterChange">
                  <option value="">所有状态</option>
                  <option value="pending">待处理</option>
                  <option value="processing">处理中</option>
                  <option value="ready">已就绪</option>
                  <option value="failed">失败</option>
                </select>
              </div>
              <div class="select-wrap" style="width: 140px">
                <select v-model="subjectFilter" @change="handleFilterChange">
                  <option value="">所有科目</option>
                  <option value="英语">英语</option>
                </select>
              </div>
              <div class="select-wrap" style="width: 140px">
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
              <div class="input-wrap" style="width: 180px">
                <input v-model="keywordFilter" type="text" placeholder="搜索文档标题..." @keyup.enter="handleFilterChange" />
              </div>
              <div class="input-wrap" style="width: 100px">
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
                  <td>{{ doc.subject }}</td>
                  <td>{{ doc.grade }}</td>
                  <td>{{ doc.year }}</td>
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

            <div v-if="searchResults.length > 0" style="margin-bottom: 12px; font-size: 13px; color: var(--text-secondary)">
              共 {{ searchTotalCount }} 条结果
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
                  </tr>
                </tbody>
              </table>
            </div>

            <div v-else-if="searchQuery && !searchLoading" class="empty-state">
              <div class="empty-state-text">输入查询词后点击搜索</div>
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
            <label>文档标题</label>
            <div class="input-wrap">
              <input v-model="uploadForm.title" type="text" placeholder="请输入文档标题" />
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
            <label>科目</label>
            <div class="select-wrap">
              <select v-model="uploadForm.subject">
                <option value="">请选择</option>
                <option value="英语">英语</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <label>年级</label>
            <div class="select-wrap">
              <select v-model="uploadForm.grade">
                <option value="">请选择</option>
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
            <label>年份 (可选)</label>
            <div class="input-wrap">
              <input v-model="uploadForm.year" type="text" placeholder="例如: 2024" />
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

    <div v-if="showStatusDialog" class="dialog-overlay" @click.self="showStatusDialog = false">
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
                <div style="font-size: 12px; color: var(--text-secondary)">
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
          <button class="btn btn-secondary btn-small" @click="showStatusDialog = false">
            关闭
          </button>
          <button class="btn btn-primary btn-small" @click="loadDocuments; showStatusDialog = false">
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
  </div>
</template>

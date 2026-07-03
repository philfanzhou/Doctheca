<script setup lang="ts">
import { ref, computed, onMounted, provide } from 'vue'
import { authService } from './services/authService'
import LoginPage from './components/LoginPage.vue'
import DocManagePage from './views/DocManagePage.vue'
import MarkdownDataPage from './views/MarkdownDataPage.vue'
import SearchPage from './views/SearchPage.vue'
import LayoutPdfPage from './views/LayoutPdfPage.vue'

const isAuthenticated = ref(false)
const appTitle = ref('DocLibrary Admin')
const activeTab = ref('documents')
const selectedFileIdForLayout = ref<string | null>(null)

function viewLayoutPdf(fileId: string) {
  if (fileId) {
    selectedFileIdForLayout.value = fileId
    activeTab.value = 'layout'
  } else {
    selectedFileIdForLayout.value = null
    activeTab.value = 'documents'
  }
}

provide('selectedFileIdForLayout', selectedFileIdForLayout)
provide('viewLayoutPdf', viewLayoutPdf)

const sidebarOpen = ref(false)
const sidebarCollapsed = ref(localStorage.getItem('docSidebarCollapsed') === 'true')
const lastRefreshTime = ref('')

const currentUser = computed(() => authService.getUser())
const displayName = computed(() => currentUser.value?.username ?? '管理员')

const navItems = [
  {
    key: 'documents',
    label: '文档管理',
    icon: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/>'
  },
  {
    key: 'markdown',
    label: 'Markdown 数据',
    icon: '<path d="M15.5 13.333l1.533 1.322c.645.555.967.833.967 1.178s-.322.623-.967 1.179L15.5 18.333m-3.333-5l-1.534 1.322c-.644.555-.966.833-.966 1.178s.322.623.966 1.179l1.534 1.321"/><path d="M17.167 10.836v-4.32c0-1.41 0-2.117-.224-2.68-.359-.906-1.118-1.621-2.08-1.96-.599-.21-1.349-.21-2.848-.21-2.623 0-3.935 0-4.983.369-1.684.591-3.013 1.842-3.641 3.428C3 6.449 3 7.684 3 10.154v2.122c0 2.558 0 3.838.706 4.726q.306.383.713.671c.76.536 1.79.64 3.581.66"/>'
  },
  {
    key: 'search',
    label: '检索测试',
    icon: '<circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>'
  }
]

const currentNavLabel = computed(() => navItems.find((n) => n.key === activeTab.value)?.label ?? '')

const componentMap: Record<string, any> = {
  documents: DocManagePage,
  markdown: MarkdownDataPage,
  search: SearchPage,
  layout: LayoutPdfPage,
}

const currentComponent = computed(() => componentMap[activeTab.value])

function toggleSidebar() {
  sidebarCollapsed.value = !sidebarCollapsed.value
  localStorage.setItem('docSidebarCollapsed', String(sidebarCollapsed.value))
}

function handleLoginSuccess() {
  isAuthenticated.value = true
}

async function handleLogout() {
  await authService.logout()
  isAuthenticated.value = false
}

onMounted(async () => {
  if (authService.isAuthenticated()) {
    isAuthenticated.value = true
  } else if (authService.canRefresh()) {
    const newTokens = await authService.refresh()
    if (newTokens) {
      isAuthenticated.value = true
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
      </header>

      <main class="content-area">
        <component :is="currentComponent" />
      </main>
    </div>
  </div>
</template>

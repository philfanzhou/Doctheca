<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, provide, watch } from 'vue'
import Sidebar from './components/Sidebar.vue'
import Topbar from './components/Topbar.vue'
import AppToast from './components/AppToast.vue'
import LoginPage from './views/LoginPage.vue'
import OverviewPage from './views/OverviewPage.vue'
import DocManagePage from './views/DocManagePage/DocManagePage.vue'
import DocDetailPage from './views/DocDetailPage/DocDetailPage.vue'
import ParseResultsPage from './views/ParseResultsPage/ParseResultsPage.vue'
import SearchPage from './views/SearchPage/SearchPage.vue'
import { useToast } from './composables/useToast'
import { useAdminSession } from './composables/useAdminSession'
import type { IconName } from './utils/icons'

interface NavItem {
  key: string
  label: string
  icon: IconName
}

const navItems: NavItem[] = [
  { key: 'overview', label: '概览', icon: 'grid' },
  { key: 'docs', label: '文档管理', icon: 'file' },
  { key: 'results', label: '解析结果', icon: 'doc' },
  { key: 'search', label: '检索测试', icon: 'search' },
]

const state = ref<{ page: string; docId: string | null }>({
  page: 'overview',
  docId: null,
})
const sidebarOpen = ref(false)
const isMobile = ref(false)
const viewRef = ref<HTMLElement | null>(null)
const sidebarComp = ref<InstanceType<typeof Sidebar> | null>(null)

const { error: toastError } = useToast()
const {
  status: sessionStatus,
  session,
  forbiddenSignal,
  initialize: initializeSession,
  logout,
} = useAdminSession()
// expose globally for legacy inline calls (optional)
if (typeof window !== 'undefined') {
  ;(window as unknown as { __toast: { success: (m: string) => void; error: (m: string) => void } }).__toast = {
    success: (m: string) => useToast().success(m),
    error: toastError,
  }
}

const currentNavKey = computed(() => {
  // For sidebar active highlighting, treat 'detail' as part of 'docs'
  if (state.value.page === 'detail') return 'docs'
  return state.value.page
})

const currentCrumb = computed(() => {
  if (state.value.page === 'detail') return '文档管理 / 文档详情'
  return navItems.find((n) => n.key === state.value.page)?.label ?? ''
})

function checkMobile() {
  isMobile.value = window.innerWidth < 900
  if (!isMobile.value) sidebarOpen.value = false
}

function navigate(page: string, docId: string | null = null) {
  if (state.value.page === page && state.value.docId === docId) return
  // 200ms blur transition: add leaving class, swap content after 150ms
  const v = viewRef.value
  if (v) v.classList.add('leaving')
  setTimeout(() => {
    state.value = { page, docId }
    if (v) v.classList.remove('leaving')
    nextTick(() => {
      viewRef.value?.scrollTo({ top: 0 })
      window.scrollTo({ top: 0 })
    })
    syncHash()
    nextTick(() => sidebarComp.value?.moveIndicator())
  }, 150)
}

function syncHash() {
  const { page, docId } = state.value
  const hash = docId ? `#${page}/${docId}` : `#${page}`
  if (location.hash !== hash) history.replaceState(null, '', hash)
}

function parseHash() {
  const m = location.hash.match(/^#(overview|docs|results|search|detail)(?:\/([A-Za-z0-9_-]+))?/)
  if (m) {
    state.value = { page: m[1], docId: m[2] || null }
  } else {
    state.value = { page: 'overview', docId: null }
  }
}

const componentMap: Record<string, typeof OverviewPage> = {
  overview: OverviewPage,
  docs: DocManagePage,
  detail: DocDetailPage,
  results: ParseResultsPage,
  search: SearchPage,
}

const currentComponent = computed(() => componentMap[state.value.page] || OverviewPage)

function handleNavigate(key: string) {
  navigate(key, null)
}

function openDocDetail(id: string) {
  navigate('detail', id)
}

function backToDocs() {
  navigate('docs', null)
}

function toggleSidebar() {
  sidebarOpen.value = !sidebarOpen.value
}

async function handleLogout(): Promise<void> {
  await logout().catch(() => undefined)
}

function onHashChange() {
  parseHash()
  nextTick(() => sidebarComp.value?.moveIndicator())
}

onMounted(() => {
  void initializeSession()
  parseHash()
  checkMobile()
  window.addEventListener('resize', checkMobile)
  window.addEventListener('hashchange', onHashChange)
  nextTick(() => sidebarComp.value?.moveIndicator())
})

watch(forbiddenSignal, (current, previous) => {
  if (current > previous) {
    toastError('当前账户没有管理员权限。')
  }
})

onUnmounted(() => {
  window.removeEventListener('resize', checkMobile)
  window.removeEventListener('hashchange', onHashChange)
})

// Provide openDocDetail/backToDocs/navigate to descendant views
provide('navigate', navigate)
provide('openDocDetail', openDocDetail)
provide('backToDocs', backToDocs)
</script>

<template>
  <div
    v-if="sessionStatus === 'checking'"
    class="session-loading doclibrary-admin"
    role="status"
  >
    正在检查登录状态…
  </div>
  <LoginPage v-else-if="sessionStatus === 'anonymous'" />
  <div v-else class="admin-shell doclibrary-admin">
    <Sidebar
      ref="sidebarComp"
      :active="currentNavKey"
      :nav-items="navItems"
      :open="sidebarOpen"
      :username="session?.username ?? ''"
      @navigate="handleNavigate"
      @close="sidebarOpen = false"
    />
    <div class="main">
      <Topbar
        :crumb="currentCrumb"
        :show-hamburger="isMobile"
        :sidebar-open="sidebarOpen"
        :username="session?.username ?? ''"
        @toggle-sidebar="toggleSidebar"
        @logout="handleLogout"
      />
      <main class="view" ref="viewRef">
        <component :is="currentComponent" :doc-id="state.docId" />
      </main>
    </div>
  </div>
  <AppToast />
</template>

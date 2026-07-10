<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { Document, Files, Search, Menu as MenuIcon, Expand, Fold } from '@element-plus/icons-vue'
import DocManagePage from './views/DocManagePage.vue'
import ParseResultsPage from './views/ParseResultsPage.vue'
import SearchPage from './views/SearchPage.vue'

const appTitle = ref('DocLibrary Admin')
const activeTab = ref('documents')

const sidebarOpen = ref(false)
const sidebarCollapsed = ref(localStorage.getItem('docSidebarCollapsed') === 'true')
const isMobile = ref(false)

function checkMobile() {
  isMobile.value = window.innerWidth < 768
  if (isMobile.value) {
    sidebarCollapsed.value = false
  }
}

onMounted(() => {
  checkMobile()
  window.addEventListener('resize', checkMobile)
})

onUnmounted(() => {
  window.removeEventListener('resize', checkMobile)
})

const navItems = [
  { key: 'documents', label: '文档管理', icon: Document },
  { key: 'results', label: '解析结果', icon: Files },
  { key: 'search', label: '检索测试', icon: Search }
]

const currentNavLabel = computed(() => navItems.find((n) => n.key === activeTab.value)?.label ?? '')

const componentMap: Record<string, any> = {
  documents: DocManagePage,
  results: ParseResultsPage,
  search: SearchPage
}

const currentComponent = computed(() => componentMap[activeTab.value])

function toggleSidebar() {
  if (isMobile.value) {
    sidebarOpen.value = !sidebarOpen.value
  } else {
    sidebarCollapsed.value = !sidebarCollapsed.value
    localStorage.setItem('docSidebarCollapsed', String(sidebarCollapsed.value))
  }
}

function handleMenuSelect(index: string) {
  activeTab.value = index
  if (isMobile.value) {
    sidebarOpen.value = false
  }
}
</script>

<template>
  <el-container class="admin-layout">
    <!-- PC/平板 sidebar -->
    <el-aside v-if="!isMobile" :width="sidebarCollapsed ? '64px' : '220px'" class="sidebar">
      <div class="sidebar-header">
        <div class="sidebar-logo">DL</div>
        <span v-if="!sidebarCollapsed" class="sidebar-title">{{ appTitle }}</span>
      </div>
      <el-menu
        :default-active="activeTab"
        :collapse="sidebarCollapsed"
        :collapse-transition="false"
        background-color="#ffffff"
        text-color="#6b7280"
        active-text-color="#2563eb"
        @select="handleMenuSelect"
      >
        <el-menu-item v-for="item in navItems" :key="item.key" :index="item.key">
          <el-icon><component :is="item.icon" /></el-icon>
          <template #title>{{ item.label }}</template>
        </el-menu-item>
      </el-menu>
    </el-aside>

    <!-- 手机 drawer -->
    <el-drawer v-model="sidebarOpen" direction="ltr" :size="220" :with-header="false" class="mobile-drawer">
      <div class="sidebar-header">
        <div class="sidebar-logo">DL</div>
        <span class="sidebar-title">{{ appTitle }}</span>
      </div>
      <el-menu
        :default-active="activeTab"
        background-color="#ffffff"
        text-color="#6b7280"
        active-text-color="#2563eb"
        @select="handleMenuSelect"
      >
        <el-menu-item v-for="item in navItems" :key="item.key" :index="item.key">
          <el-icon><component :is="item.icon" /></el-icon>
          <template #title>{{ item.label }}</template>
        </el-menu-item>
      </el-menu>
    </el-drawer>

    <el-container>
      <el-header height="52px" class="top-header">
        <div class="header-left">
          <el-button text @click="toggleSidebar">
            <el-icon :size="18">
              <Fold v-if="!isMobile && !sidebarCollapsed" />
              <Expand v-else-if="!isMobile && sidebarCollapsed" />
              <MenuIcon v-else />
            </el-icon>
          </el-button>
          <el-breadcrumb separator="/">
            <el-breadcrumb-item>{{ currentNavLabel }}</el-breadcrumb-item>
          </el-breadcrumb>
        </div>
      </el-header>
      <el-main class="content-area">
        <component :is="currentComponent" />
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.admin-layout {
  height: 100vh;
}

.sidebar {
  background: #ffffff;
  border-right: 1px solid var(--el-border-color-light);
  transition: width 0.2s ease;
  overflow: hidden;
}

.sidebar-header {
  height: 52px;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 16px;
  border-bottom: 1px solid var(--el-border-color-light);
}

.sidebar-logo {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: #2563eb;
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  font-weight: 700;
  flex-shrink: 0;
}

.sidebar-title {
  font-size: 14px;
  font-weight: 600;
  color: #111827;
  white-space: nowrap;
}

.sidebar :deep(.el-menu) {
  border-right: none;
}

.mobile-drawer :deep(.el-drawer__body) {
  padding: 0;
}

.top-header {
  display: flex;
  align-items: center;
  background: #ffffff;
  border-bottom: 1px solid var(--el-border-color-light);
  padding: 0 12px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.content-area {
  background: #f5f7fa;
  padding: 16px;
  overflow-y: auto;
}
</style>

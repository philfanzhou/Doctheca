<script setup lang="ts">
import { ref } from 'vue'
import { iconHtml } from '../../utils/icons'
import SearchBar from './SearchBar.vue'
import SearchFiltersDrawer from './SearchFiltersDrawer.vue'
import SearchResultCard from './SearchResultCard.vue'
import { useSearch } from './useSearch'

const devMode = ref(false)

const {
  searchQuery,
  searchPhrase,
  searchLoading,
  searchResults,
  searchTotalCount,
  hasSearched,
  showAdvancedFilter,
  filters,
  hasMinerUFilter,
  handleSearch,
  clearSearch,
  clearFilters,
  applyFiltersAndSearch,
} = useSearch()
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">检索测试</div>
        <div class="page-sub">验证文档解析结果的检索能力</div>
      </div>
      <div class="page-actions">
        <button
          class="btn btn-ghost btn-sm"
          :class="{ active: devMode }"
          @click="devMode = !devMode"
        >
          <span v-html="iconHtml('zap')"></span>{{ devMode ? '关闭 Dev' : 'Dev 模式' }}
        </button>
      </div>
    </div>

    <div class="card">
      <div class="card-head">
        <div>
          <div class="card-title">检索</div>
          <div class="card-sub">输入关键词，查看 BM25 命中与 minerU 块级元数据</div>
        </div>
        <span v-if="hasMinerUFilter" class="badge amber"><span class="dot"></span>minerU 筛选已启用</span>
      </div>

      <SearchBar
        v-model:query="searchQuery"
        v-model:phrase="searchPhrase"
        :loading="searchLoading"
        :has-results="searchResults.length > 0"
        :has-miner-u-filter="hasMinerUFilter"
        @search="handleSearch"
        @clear="clearSearch"
        @open-filters="showAdvancedFilter = true"
      />

      <div v-if="searchResults.length > 0" class="result-meta">
        共 <strong>{{ searchTotalCount }}</strong> 条结果，关键词:
        <strong>{{ searchQuery }}</strong>
      </div>
    </div>

    <div v-loading="searchLoading" style="min-height: 120px; margin-top: 16px">
      <div v-if="searchResults.length" class="result-list">
        <SearchResultCard
          v-for="(row, idx) in searchResults"
          :key="row.segmentId"
          :row="row"
          :index="idx"
          :query="searchQuery"
          :dev-mode="devMode"
        />
      </div>

      <div v-else-if="!searchLoading && hasSearched" class="empty">
        <span v-html="iconHtml('search')"></span><br />
        无匹配结果
      </div>

      <div v-else-if="!searchLoading && !hasSearched" class="empty">
        <span v-html="iconHtml('search')"></span><br />
        输入查询词后点击搜索
      </div>
    </div>

    <SearchFiltersDrawer
      v-model:filters="filters"
      :open="showAdvancedFilter"
      @clear="clearFilters"
      @apply="applyFiltersAndSearch"
      @close="showAdvancedFilter = false"
    />
  </div>
</template>

<style scoped>
.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.page-actions :deep(svg),
.btn :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}

.result-meta {
  margin: 12px 0;
  font-size: 13px;
  color: var(--text-2);
}

.result-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.btn.active {
  background: var(--primary-soft);
  color: var(--primary-hover);
  border-color: var(--primary-line);
}
</style>

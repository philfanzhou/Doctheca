<script setup lang="ts">
import { ref } from 'vue'
import { createDocApiClient, type SearchResult } from '../services/docApi'
import { formatDate } from '../utils/format'

const client = createDocApiClient()

const searchQuery = ref('')
const searchPhrase = ref(false)
const searchLoading = ref(false)
const searchResults = ref<SearchResult[]>([])
const searchTotalCount = ref(0)
const searchNextToken = ref('')

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
    console.error('检索失败', error)
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
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">检索测试</h1>
    <p class="page-subtitle">测试文档检索功能</p>
  </div>

  <div class="card">
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
</template>

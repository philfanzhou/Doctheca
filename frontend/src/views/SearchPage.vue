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
      <div class="toolbar">
        <div class="input-wrap input-flex">
          <input v-model="searchQuery" type="text" placeholder="输入单词或短语进行检索..." @keyup.enter="handleSearch" />
        </div>
        <label class="inline-check">
          <input v-model="searchPhrase" type="checkbox" />
          短语查询
        </label>
        <button class="btn btn-primary btn-small" :disabled="searchLoading || !searchQuery.trim()" @click="handleSearch">
          {{ searchLoading ? '搜索中...' : '搜索' }}
        </button>
      </div>

      <div v-if="searchResults.length > 0" class="result-meta">
        <span>共 {{ searchTotalCount }} 条结果，关键词: <strong>{{ searchQuery }}</strong></span>
        <button class="btn btn-link btn-small" @click="clearSearch">清空</button>
      </div>

      <div v-if="searchResults.length > 0" class="table-scroll">
        <table class="data-table">
          <thead>
            <tr>
              <th>#</th>
              <th>文档标题</th>
              <th>页码</th>
              <th>匹配类型</th>
              <th>相关度</th>
              <th>匹配文本</th>
              <th>Segment ID</th>
              <th>偏移量</th>
              <th>创建时间</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(result, idx) in searchResults" :key="idx">
              <td class="text-muted-sm">{{ idx + 1 }}</td>
              <td class="text-ellipsis" :style="{ maxWidth: '200px' }" :title="result.documentName">{{ result.documentName }}</td>
              <td><span class="tag tag-info">P{{ result.pageNumber }}</span></td>
              <td>
                <span class="tag" :class="result.matchType === 'exact_phrase' ? 'tag-success' : result.matchType === 'exact_word' ? 'tag-info' : 'tag-warning'">
                  {{ result.matchType === 'exact_phrase' ? '精确短语' : result.matchType === 'exact_word' ? '精确词' : result.matchType === 'stem_match' ? '词干匹配' : result.matchType }}
                </span>
              </td>
              <td class="text-mono">{{ result.score.toFixed(2) }}</td>
              <td class="match-text" :style="{ maxWidth: '400px' }">{{ result.associatedText }}</td>
              <td class="text-mono text-muted-sm text-ellipsis" :style="{ maxWidth: '150px' }" :title="result.segmentId">{{ result.segmentId }}</td>
              <td class="text-mono text-muted-sm">{{ result.startOffset }}–{{ result.endOffset }}</td>
              <td class="text-muted-sm">{{ result.createdAt ? formatDate(result.createdAt) : '-' }}</td>
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

<style scoped>
.match-text {
  line-height: 1.5;
  color: var(--text-secondary);
  white-space: pre-wrap;
  word-break: break-word;
}
</style>

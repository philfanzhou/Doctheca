<script setup lang="ts">
import { ref, computed } from 'vue'
import { createDocApiClient, type SearchResult } from '../services/docApi'
import { formatDate } from '../utils/format'

const client = createDocApiClient()

// V1 search state
const searchQuery = ref('')
const searchPhrase = ref(false)
const searchLoading = ref(false)
const searchResults = ref<SearchResult[]>([])
const searchTotalCount = ref(0)
const searchNextToken = ref('')

// [Gen-2] minerU advanced filter state
const showAdvancedFilter = ref(false)
const filterBlockType = ref('')
const filterBlockSubType = ref('')
const filterPageNumber = ref<number | undefined>(undefined)
const filterTextLevel = ref<number | undefined>(undefined)
const filterTextFormat = ref('')
const filterParseId = ref('')
const filterDocumentFileId = ref('')
const filterHasImage = ref(false)

// Row expansion state (blockData detail)
const expandedRows = ref<Set<number>>(new Set())

// minerU block type candidates (pipeline + VLM union; not enforced — minerU version may iterate)
const blockTypeOptions = [
  'text', 'title', 'image', 'table', 'chart', 'list', 'index', 'interline_equation',
  'code', 'algorithm', 'equation', 'phonetic', 'ref_text',
  'header', 'footer', 'page_number', 'aside_text', 'page_footnote'
]
const subTypePlaceholder = '如 table_caption / code / algorithm / image_body'
const textFormatOptions = ['', 'latex', 'markdown', 'none']

const hasMinerUFilter = computed(() => {
  return !!(filterBlockType.value || filterBlockSubType.value
    || filterPageNumber.value !== undefined
    || filterTextLevel.value !== undefined
    || filterTextFormat.value
    || filterParseId.value || filterDocumentFileId.value
    || filterHasImage.value)
})

async function handleSearch() {
  if (!searchQuery.value.trim()) return
  searchLoading.value = true
  try {
    const response = await client.searchTest(
      searchQuery.value.trim(),
      searchPhrase.value,
      20,
      undefined,
      filterBlockType.value || undefined,
      filterBlockSubType.value || undefined,
      filterPageNumber.value,
      filterTextLevel.value,
      filterTextFormat.value || undefined,
      filterParseId.value || undefined,
      filterDocumentFileId.value || undefined,
      filterHasImage.value || undefined
    )
    searchResults.value = response.results
    searchTotalCount.value = response.totalCount
    searchNextToken.value = response.nextPageToken
    expandedRows.value.clear()
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
  expandedRows.value.clear()
}

function clearFilters() {
  filterBlockType.value = ''
  filterBlockSubType.value = ''
  filterPageNumber.value = undefined
  filterTextLevel.value = undefined
  filterTextFormat.value = ''
  filterParseId.value = ''
  filterDocumentFileId.value = ''
  filterHasImage.value = false
}

function toggleRow(idx: number) {
  if (expandedRows.value.has(idx)) {
    expandedRows.value.delete(idx)
  } else {
    expandedRows.value.add(idx)
  }
}

function formatBlockData(raw?: string): string {
  if (!raw) return ''
  try {
    const parsed = JSON.parse(raw)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return raw
  }
}

function extractBlockType(raw?: string): string {
  if (!raw) return ''
  try {
    const parsed = JSON.parse(raw)
    return typeof parsed === 'object' && parsed !== null && 'type' in parsed
      ? String((parsed as { type: unknown }).type)
      : ''
  } catch {
    return ''
  }
}

function formatBbox(bbox?: number[]): string {
  if (!bbox || bbox.length < 4) return '-'
  return `[${bbox[0]}, ${bbox[1]}, ${bbox[2]}, ${bbox[3]}]`
}

function captionPreview(caption?: string): string {
  if (!caption) return '-'
  return caption.length > 30 ? caption.slice(0, 30) + '…' : caption
}

function matchTypeLabel(matchType: string): string {
  if (matchType === 'exact_phrase') return '精确短语'
  if (matchType === 'exact_word') return '精确词'
  if (matchType === 'stem_match') return '词干匹配'
  return matchType
}

function matchTypeClass(matchType: string): string {
  if (matchType === 'exact_phrase') return 'tag-success'
  if (matchType === 'exact_word') return 'tag-info'
  return 'tag-warning'
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
      <!-- V1 keyword + phrase query area (preserved) -->
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
        <button class="btn btn-link btn-small" @click="showAdvancedFilter = !showAdvancedFilter">
          {{ showAdvancedFilter ? '收起筛选' : '高级筛选' }}
          <span v-if="hasMinerUFilter" class="filter-active-dot" title="已启用 minerU 筛选"></span>
        </button>
      </div>

      <!-- [Gen-2] minerU advanced filter drawer -->
      <div v-if="showAdvancedFilter" class="advanced-filter">
        <div class="filter-grid">
          <div class="input-wrap">
            <label class="filter-label">块类型 (blockType)</label>
            <input v-model="filterBlockType" type="text" list="blockTypeList" placeholder="text / image / table..." />
            <datalist id="blockTypeList">
              <option v-for="t in blockTypeOptions" :key="t" :value="t" />
            </datalist>
          </div>
          <div class="input-wrap">
            <label class="filter-label">二级类型 (subType)</label>
            <input v-model="filterBlockSubType" type="text" :placeholder="subTypePlaceholder" />
          </div>
          <div class="input-wrap">
            <label class="filter-label">页码 (pageNumber)</label>
            <input v-model.number="filterPageNumber" type="number" min="0" placeholder="0-based page_idx" />
          </div>
          <div class="input-wrap">
            <label class="filter-label">标题层级 (textLevel)</label>
            <input v-model.number="filterTextLevel" type="number" min="-1" placeholder="0=正文 / 1=h1 / 2=h2" />
          </div>
          <div class="input-wrap">
            <label class="filter-label">文本格式 (textFormat)</label>
            <select v-model="filterTextFormat">
              <option v-for="f in textFormatOptions" :key="f" :value="f">{{ f || '(不限)' }}</option>
            </select>
          </div>
          <div class="input-wrap">
            <label class="filter-label">解析 ID (parseId)</label>
            <input v-model="filterParseId" type="text" placeholder="Guid" />
          </div>
          <div class="input-wrap">
            <label class="filter-label">文档 ID (documentFileId)</label>
            <input v-model="filterDocumentFileId" type="text" placeholder="Guid" />
          </div>
          <div class="input-wrap checkbox-wrap">
            <label class="inline-check">
              <input v-model="filterHasImage" type="checkbox" />
              仅含图片块 (hasImage)
            </label>
          </div>
        </div>
        <div class="filter-actions">
          <button class="btn btn-primary btn-small" :disabled="searchLoading || !searchQuery.trim()" @click="handleSearch">
            应用筛选
          </button>
          <button class="btn btn-secondary btn-small" @click="clearFilters">
            清空筛选
          </button>
        </div>
      </div>

      <div v-if="searchResults.length > 0" class="result-meta">
        <span>共 {{ searchTotalCount }} 条结果，关键词: <strong>{{ searchQuery }}</strong>
          <span v-if="hasMinerUFilter" class="filter-badge">已应用 minerU 筛选</span>
        </span>
        <button class="btn btn-link btn-small" @click="clearSearch">清空</button>
      </div>

      <div v-if="searchResults.length > 0" class="table-scroll">
        <table class="data-table">
          <thead>
            <tr>
              <th>#</th>
              <th>文档标题</th>
              <th>块类型</th>
              <th>subType</th>
              <th>页码</th>
              <th>匹配类型</th>
              <th>BM25 相关度</th>
              <th>矿工 U 置信度</th>
              <th>textFormat</th>
              <th>Caption</th>
              <th>匹配文本</th>
              <th>Segment ID</th>
              <th>创建时间</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="(result, idx) in searchResults" :key="idx">
              <tr>
                <td class="text-muted-sm">{{ idx + 1 }}</td>
                <td class="text-ellipsis" :style="{ maxWidth: '200px' }" :title="result.documentName">{{ result.documentName }}</td>
                <td><span class="tag tag-info">{{ extractBlockType(result.blockData) || '-' }}</span></td>
                <td class="text-muted-sm">{{ result.subType || '-' }}</td>
                <td><span class="tag tag-info">P{{ result.pageNumber + 1 }}</span></td>
                <td>
                  <span class="tag" :class="matchTypeClass(result.matchType)">{{ matchTypeLabel(result.matchType) }}</span>
                </td>
                <td class="text-mono">{{ result.score.toFixed(2) }}</td>
                <td class="text-mono">{{ result.mineruScore !== undefined && result.mineruScore !== null ? result.mineruScore.toFixed(2) : '-' }}</td>
                <td class="text-muted-sm">{{ result.textFormat || '-' }}</td>
                <td class="text-muted-sm text-ellipsis" :style="{ maxWidth: '120px' }" :title="result.caption">{{ captionPreview(result.caption) }}</td>
                <td class="match-text" :style="{ maxWidth: '400px' }">{{ result.associatedText }}</td>
                <td class="text-mono text-muted-sm text-ellipsis" :style="{ maxWidth: '150px' }" :title="result.segmentId">{{ result.segmentId }}</td>
                <td class="text-muted-sm">{{ result.createdAt ? formatDate(result.createdAt) : '-' }}</td>
                <td>
                  <button class="btn btn-link btn-small" @click="toggleRow(idx)">
                    {{ expandedRows.has(idx) ? '收起' : '展开详情' }}
                  </button>
                </td>
              </tr>
              <tr v-if="expandedRows.has(idx)" class="detail-row">
                <td :colspan="14">
                  <div class="detail-card">
                    <div class="detail-section">
                      <div class="detail-label">BM25 相关度 (V1 Score)</div>
                      <div class="detail-value text-mono">{{ result.score.toFixed(4) }}</div>
                      <div class="detail-hint">OpenSearch _score，越大越相关</div>
                    </div>
                    <div class="detail-section">
                      <div class="detail-label">矿工 U 置信度 (mineruScore)</div>
                      <div class="detail-value text-mono">{{ result.mineruScore !== undefined && result.mineruScore !== null ? result.mineruScore.toFixed(4) : '-' }}</div>
                      <div class="detail-hint">VLM 后端解析置信度，0-1 区间</div>
                    </div>
                    <div class="detail-section">
                      <div class="detail-label">bbox [x0, y0, x1, y1]</div>
                      <div class="detail-value text-mono">{{ formatBbox(result.bbox) }}</div>
                      <div class="detail-hint">归一化到 0-1000 pipeline 惯例</div>
                    </div>
                    <div class="detail-section">
                      <div class="detail-label">textLevel</div>
                      <div class="detail-value text-mono">{{ result.textLevel !== undefined && result.textLevel !== null ? result.textLevel : '-' }}</div>
                      <div class="detail-hint">0=正文 / 1=h1 / 2=h2...</div>
                    </div>
                    <div class="detail-block-data">
                      <div class="detail-label">blockData 原始 JSON</div>
                      <pre class="block-data-pre">{{ formatBlockData(result.blockData) || '(无)' }}</pre>
                    </div>
                  </div>
                </td>
              </tr>
            </template>
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

.advanced-filter {
  margin-top: 12px;
  padding: 16px;
  background: var(--bg-secondary);
  border-radius: var(--radius-md);
  border: 1px solid var(--border-light);
}

.filter-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 12px;
}

.filter-label {
  display: block;
  font-size: 12px;
  color: var(--text-secondary);
  margin-bottom: 4px;
  font-weight: 500;
}

.filter-actions {
  margin-top: 12px;
  display: flex;
  gap: 8px;
}

.filter-active-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--warning-color);
  margin-left: 4px;
  vertical-align: middle;
}

.filter-badge {
  display: inline-block;
  padding: 2px 8px;
  background: var(--warning-color);
  color: #fff;
  border-radius: var(--radius-sm);
  font-size: 11px;
  margin-left: 8px;
}

.detail-row {
  background: var(--bg-secondary);
}

.detail-row > td {
  padding: 12px 16px;
}

.detail-card {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 16px;
  align-items: start;
}

.detail-section {
  padding: 8px 12px;
  background: var(--card-bg);
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-light);
}

.detail-label {
  font-size: 12px;
  color: var(--text-secondary);
  font-weight: 500;
  margin-bottom: 4px;
}

.detail-value {
  font-size: 14px;
  color: var(--text-primary);
}

.detail-hint {
  font-size: 11px;
  color: var(--text-muted);
  margin-top: 2px;
}

.detail-block-data {
  grid-column: 1 / -1;
}

.block-data-pre {
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--card-bg);
  padding: 12px;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-light);
  font-size: 12px;
  font-family: 'SF Mono', Menlo, Monaco, Consolas, monospace;
  max-height: 400px;
  overflow-y: auto;
  margin: 0;
}

.checkbox-wrap {
  display: flex;
  align-items: flex-end;
}
</style>

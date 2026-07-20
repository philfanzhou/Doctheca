<script setup lang="ts">
import { ref, computed } from 'vue'
import { createDocApiClient, type SearchResult } from '../services/docApi'
import { formatDate } from '../utils/format'
import { useToast, escapeHtml } from '../composables/useToast'
import { iconHtml } from '../utils/icons'
import AppDrawer from '../components/AppDrawer.vue'

const client = createDocApiClient()
const { error: toastError } = useToast()

// V1 search state
const searchQuery = ref('')
const searchPhrase = ref(false)
const searchLoading = ref(false)
const searchResults = ref<SearchResult[]>([])
const searchTotalCount = ref(0)
const searchNextToken = ref('')
const hasSearched = ref(false)
const devMode = ref(false)
const expandedRowKeys = ref<Set<string>>(new Set())

// [Gen-2] minerU advanced filter state — RED LINE: all 8 filters must be preserved
const showAdvancedFilter = ref(false)
const filterBlockType = ref('')
const filterBlockSubType = ref('')
const filterPageNumber = ref<number | undefined>(undefined)
const filterTextLevel = ref<number | undefined>(undefined)
const filterTextFormat = ref('')
const filterParseId = ref('')
const filterDocumentFileId = ref('')
const filterHasImage = ref(false)

// minerU block type candidates (pipeline + VLM union; not enforced — minerU version may iterate)
// RED LINE: continue to provide as ElSelect.filterable (replaces legacy <datalist>)
const blockTypeOptions = [
  'text', 'title', 'image', 'table', 'chart', 'list', 'index', 'interline_equation',
  'code', 'algorithm', 'equation', 'phonetic', 'ref_text',
  'header', 'footer', 'page_number', 'aside_text', 'page_footnote'
]
const subTypePlaceholder = '如 table_caption / code / algorithm / image_body'
const textFormatOptions = [
  { value: '', label: '(不限)' },
  { value: 'latex', label: 'latex' },
  { value: 'markdown', label: 'markdown' },
  { value: 'none', label: 'none' }
]

const hasMinerUFilter = computed(() => {
  return !!(filterBlockType.value || filterBlockSubType.value
    || filterPageNumber.value !== undefined
    || filterTextLevel.value !== undefined
    || filterTextFormat.value
    || filterParseId.value || filterDocumentFileId.value
    || filterHasImage.value)
})

// RED LINE: handleSearch() must pass all 8 minerU filters to client.searchTest(...) in the same order
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
    hasSearched.value = true
    expandedRowKeys.value = new Set()
  } catch (error) {
    toastError(error instanceof Error ? error.message : '检索失败')
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
  hasSearched.value = false
  expandedRowKeys.value = new Set()
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

function applyFiltersAndSearch() {
  showAdvancedFilter.value = false
  handleSearch()
}

function toggleRowExpand(segmentId: string) {
  const s = new Set(expandedRowKeys.value)
  if (s.has(segmentId)) s.delete(segmentId)
  else s.add(segmentId)
  expandedRowKeys.value = s
}

function isRowExpanded(segmentId: string): boolean {
  return expandedRowKeys.value.has(segmentId)
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

function matchTypeBadgeHtml(matchType: string): string {
  if (matchType === 'exact_phrase') return `<span class="badge green"><span class="dot"></span>${matchTypeLabel(matchType)}</span>`
  if (matchType === 'exact_word') return `<span class="badge blue"><span class="dot"></span>${matchTypeLabel(matchType)}</span>`
  return `<span class="badge amber"><span class="dot"></span>${matchTypeLabel(matchType)}</span>`
}

function mineruScoreText(score?: number): string {
  return score !== undefined && score !== null ? score.toFixed(2) : '-'
}

function scorePercent(score: number): number {
  // Normalize BM25 score (typical range 0-30) to 0-100% bar
  return Math.min(100, Math.max(0, (score / 30) * 100))
}

function highlightText(text: string, query: string): string {
  if (!query) return escapeHtml(text)
  const esc = escapeHtml(text)
  const terms = query.trim().split(/\s+/).filter((t) => t.length > 0)
  if (!terms.length) return esc
  const re = new RegExp(`(${terms.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi')
  return esc.replace(re, '<mark>$1</mark>')
}
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

      <div class="search-bar">
        <div style="position: relative; flex: 1; min-width: 280px">
          <span
            v-html="iconHtml('search')"
            style="position: absolute; left: 12px; top: 11px; color: var(--text-3); width: 16px; height: 16px"
          ></span>
          <input
            v-model="searchQuery"
            class="input"
            style="padding-left: 38px; padding-right: 38px"
            placeholder="输入单词或短语进行检索…"
            @keyup.enter="handleSearch"
          />
          <button
            v-if="searchQuery"
            class="clear-btn"
            @click="searchQuery = ''"
          >
            <span v-html="iconHtml('x')"></span>
          </button>
        </div>
        <label class="phrase-toggle">
          <input type="checkbox" v-model="searchPhrase" />
          <span>短语查询</span>
        </label>
        <button
          class="btn"
          :disabled="!searchQuery.trim() || searchLoading"
          @click="handleSearch"
        >
          <span v-html="iconHtml('search')"></span>{{ searchLoading ? '检索中…' : '搜索' }}
        </button>
        <button class="btn btn-ghost" @click="showAdvancedFilter = true">
          <span v-html="iconHtml('shield')"></span>高级筛选
          <i v-if="hasMinerUFilter" class="filter-active-dot"></i>
        </button>
        <button v-if="searchResults.length > 0" class="btn btn-ghost btn-sm" @click="clearSearch">清空</button>
      </div>

      <div v-if="searchResults.length > 0" class="result-meta">
        共 <strong>{{ searchTotalCount }}</strong> 条结果，关键词:
        <strong>{{ searchQuery }}</strong>
      </div>
    </div>

    <div v-loading="searchLoading" style="min-height: 120px; margin-top: 16px">
      <div v-if="searchResults.length" class="result-list">
        <div
          v-for="(row, idx) in searchResults"
          :key="row.segmentId"
          class="card hoverable result-card"
        >
          <div class="result-head">
            <span class="result-page">P{{ row.pageNumber + 1 }}</span>
            <span v-if="extractBlockType(row.blockData)" class="badge indigo">{{ extractBlockType(row.blockData) }}</span>
            <span v-if="row.subType" class="td-sub">{{ row.subType }}</span>
            <span v-html="matchTypeBadgeHtml(row.matchType)"></span>
            <span v-if="row.textFormat" class="badge gray">{{ row.textFormat }}</span>
            <span class="spacer"></span>
            <span class="td-sub mono">#{{ idx + 1 }}</span>
          </div>

          <div class="result-doc">{{ row.documentName }}</div>
          <div class="result-snippet" v-html="highlightText(row.associatedText, searchQuery)"></div>

          <div v-if="row.caption" class="td-sub" style="margin-top: 8px">
            caption: {{ captionPreview(row.caption) }}
          </div>

          <div class="result-foot">
            <div class="score-bar">
              <span>BM25</span>
              <div class="score-track">
                <div class="score-fill" :style="{ width: scorePercent(row.score) + '%' }"></div>
              </div>
              <span class="mono">{{ row.score.toFixed(2) }}</span>
            </div>
            <div class="score-bar" v-if="row.mineruScore !== undefined && row.mineruScore !== null">
              <span>minerU</span>
              <div class="score-track">
                <div class="score-fill" :style="{ width: (row.mineruScore * 100) + '%' }"></div>
              </div>
              <span class="mono">{{ mineruScoreText(row.mineruScore) }}</span>
            </div>
            <span class="td-sub">textLevel: {{ row.textLevel !== undefined && row.textLevel !== null ? row.textLevel : '-' }}</span>
            <span class="td-sub">bbox: <span class="mono">{{ formatBbox(row.bbox) }}</span></span>
            <span class="td-sub">{{ row.createdAt ? formatDate(row.createdAt) : '-' }}</span>
            <span class="spacer"></span>
            <button
              class="btn btn-ghost btn-sm"
              @click="toggleRowExpand(row.segmentId)"
            >
              <span v-html="iconHtml('eye')"></span>{{ isRowExpanded(row.segmentId) ? '收起' : '查看 blockData' }}
            </button>
          </div>

          <div v-if="isRowExpanded(row.segmentId)" class="expanded-detail">
            <div class="detail-grid">
              <div class="detail-cell">
                <div class="detail-label">BM25 相关度 (V1 Score)</div>
                <div class="detail-value mono">{{ row.score.toFixed(4) }}</div>
                <div class="detail-hint">OpenSearch _score，越大越相关</div>
              </div>
              <div class="detail-cell">
                <div class="detail-label">矿工 U 置信度 (mineruScore)</div>
                <div class="detail-value mono">{{ row.mineruScore !== undefined && row.mineruScore !== null ? row.mineruScore.toFixed(4) : '-' }}</div>
                <div class="detail-hint">VLM 后端解析置信度，0-1 区间</div>
              </div>
              <div class="detail-cell">
                <div class="detail-label">bbox [x0, y0, x1, y1]</div>
                <div class="detail-value mono">{{ formatBbox(row.bbox) }}</div>
                <div class="detail-hint">归一化到 0-1000 pipeline 惯例</div>
              </div>
              <div class="detail-cell">
                <div class="detail-label">textLevel</div>
                <div class="detail-value mono">{{ row.textLevel !== undefined && row.textLevel !== null ? row.textLevel : '-' }}</div>
                <div class="detail-hint">0=正文 / 1=h1 / 2=h2…</div>
              </div>
              <div class="detail-cell">
                <div class="detail-label">Segment ID</div>
                <div class="detail-value mono" style="word-break: break-all">{{ row.segmentId }}</div>
              </div>
            </div>
            <div class="dev-block" v-if="devMode && row.blockData">
              <div class="block-label">blockData 原始 JSON</div>
              <pre>{{ formatBlockData(row.blockData) || '(无)' }}</pre>
            </div>
          </div>
        </div>
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

    <!-- Advanced filter drawer (using AppDrawer for design-language consistency) -->
    <AppDrawer
      :open="showAdvancedFilter"
      title="高级筛选"
      subtitle="minerU 第 2 代块级过滤"
      @close="showAdvancedFilter = false"
    >
      <div class="filter-form">
        <div class="filter-field">
          <label class="filter-label">块类型 (blockType)</label>
          <el-select
            v-model="filterBlockType"
            filterable
            allow-create
            clearable
            default-first-option
            placeholder="text / image / table..."
            style="width: 100%"
          >
            <el-option v-for="t in blockTypeOptions" :key="t" :label="t" :value="t" />
          </el-select>
        </div>
        <div class="filter-field">
          <label class="filter-label">二级类型 (subType)</label>
          <el-input v-model="filterBlockSubType" :placeholder="subTypePlaceholder" clearable />
        </div>
        <div class="filter-field">
          <label class="filter-label">页码 (pageNumber)</label>
          <el-input-number
            v-model="filterPageNumber"
            :min="0"
            controls-position="right"
            placeholder="0-based page_idx"
            style="width: 100%"
          />
        </div>
        <div class="filter-field">
          <label class="filter-label">标题层级 (textLevel)</label>
          <el-input-number
            v-model="filterTextLevel"
            :min="-1"
            controls-position="right"
            placeholder="0=正文 / 1=h1 / 2=h2"
            style="width: 100%"
          />
        </div>
        <div class="filter-field">
          <label class="filter-label">文本格式 (textFormat)</label>
          <el-select v-model="filterTextFormat" clearable style="width: 100%">
            <el-option v-for="f in textFormatOptions" :key="f.value" :label="f.label" :value="f.value" />
          </el-select>
        </div>
        <div class="filter-field">
          <label class="filter-label">解析 ID (parseId)</label>
          <el-input v-model="filterParseId" placeholder="Guid" clearable />
        </div>
        <div class="filter-field">
          <label class="filter-label">文档 ID (documentFileId)</label>
          <el-input v-model="filterDocumentFileId" placeholder="Guid" clearable />
        </div>
        <div class="filter-field">
          <label class="filter-label">仅含图片块 (hasImage)</label>
          <el-switch v-model="filterHasImage" />
        </div>
      </div>
      <template #foot>
        <button class="btn btn-ghost" @click="clearFilters">清空筛选</button>
        <button
          class="btn"
          :disabled="!searchQuery.trim()"
          @click="applyFiltersAndSearch"
        >
          应用筛选
        </button>
      </template>
    </AppDrawer>
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

.search-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-top: 12px;
}

.clear-btn {
  position: absolute;
  right: 6px;
  top: 6px;
  width: 22px;
  height: 22px;
  border: none;
  background: var(--surface-2);
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  color: var(--text-3);
  padding: 0;
}

.clear-btn :deep(svg) {
  width: 12px;
  height: 12px;
}

.clear-btn:hover {
  background: var(--border-2);
  color: var(--text);
}

.phrase-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--text-2);
  cursor: pointer;
  user-select: none;
}

.phrase-toggle input {
  width: 14px;
  height: 14px;
  accent-color: var(--primary);
}

.filter-active-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--warning);
  margin-left: 4px;
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

.spacer {
  flex: 1;
}

.expanded-detail {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px dashed var(--border-2);
}

.detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 12px;
  align-items: start;
}

.detail-cell {
  padding: 10px 12px;
  background: var(--surface-2);
  border-radius: 8px;
  border: 1px solid var(--border-2);
}

.detail-label {
  font-size: 12px;
  color: var(--text-3);
  font-weight: 500;
  margin-bottom: 4px;
}

.detail-value {
  font-size: 14px;
  color: var(--text);
  word-break: break-all;
}

.detail-hint {
  font-size: 11px;
  color: var(--text-3);
  margin-top: 2px;
}

.block-label {
  font-size: 11px;
  color: #818cf8;
  margin-bottom: 6px;
  letter-spacing: 0.3px;
  font-weight: 600;
  text-transform: uppercase;
}

.dev-block pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  color: #c6cbe8;
  font-family: var(--mono);
  font-size: 11.5px;
  line-height: 1.6;
}

.filter-form {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.filter-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.filter-label {
  font-size: 12px;
  color: var(--text-2);
  font-weight: 500;
}

.btn.active {
  background: var(--primary-soft);
  color: var(--primary-hover);
  border-color: var(--primary-line);
}
</style>

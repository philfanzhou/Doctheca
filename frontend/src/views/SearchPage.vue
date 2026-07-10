<script setup lang="ts">
import { ref, computed } from 'vue'
import { ElMessage } from 'element-plus'
import { Search, Filter } from '@element-plus/icons-vue'
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
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '检索失败')
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

function matchTypeTagType(matchType: string): 'success' | 'info' | 'warning' {
  if (matchType === 'exact_phrase') return 'success'
  if (matchType === 'exact_word') return 'info'
  return 'warning'
}

function mineruScoreText(score?: number): string {
  return score !== undefined && score !== null ? score.toFixed(2) : '-'
}
</script>

<template>
  <div class="page-header">
    <h1 class="page-title">检索测试</h1>
    <p class="page-subtitle">测试文档检索功能</p>
  </div>

  <el-card shadow="never" class="page-card">
    <template #header>
      <div class="card-header">
        <span>检索测试</span>
        <el-tag v-if="hasMinerUFilter" type="warning" size="small" effect="light">已应用 minerU 筛选</el-tag>
      </div>
    </template>

    <!-- V1 keyword + phrase query area (preserved) -->
    <div class="toolbar">
      <el-input
        v-model="searchQuery"
        :prefix-icon="Search"
        placeholder="输入单词或短语进行检索..."
        clearable
        class="query-input"
        @keyup.enter="handleSearch"
      />
      <el-checkbox v-model="searchPhrase">短语查询</el-checkbox>
      <el-button type="primary" :loading="searchLoading" :disabled="!searchQuery.trim()" @click="handleSearch">
        搜索
      </el-button>
      <el-button :icon="Filter" @click="showAdvancedFilter = true">
        高级筛选
        <span v-if="hasMinerUFilter" class="filter-active-dot" title="已启用 minerU 筛选"></span>
      </el-button>
      <el-button v-if="searchResults.length > 0" text @click="clearSearch">清空</el-button>
    </div>

    <div v-if="searchResults.length > 0" class="result-meta">
      <span>共 {{ searchTotalCount }} 条结果，关键词: <strong>{{ searchQuery }}</strong></span>
    </div>

    <!-- RED LINE: result table preserves minerU columns + row-expand blockData detail -->
    <el-table
      v-loading="searchLoading"
      :data="searchResults"
      stripe
      border
      :empty-text="searchQuery && !searchLoading ? '无匹配结果' : '输入查询词后点击搜索'"
    >
      <el-table-column type="expand">
        <!-- RED LINE: row expand blockData detail (formatBlockData + formatBbox + mineruScore) -->
        <template #default="{ row }">
          <div class="detail-card">
            <div class="detail-section">
              <div class="detail-label">BM25 相关度 (V1 Score)</div>
              <div class="detail-value mono">{{ row.score.toFixed(4) }}</div>
              <div class="detail-hint">OpenSearch _score，越大越相关</div>
            </div>
            <div class="detail-section">
              <div class="detail-label">矿工 U 置信度 (mineruScore)</div>
              <div class="detail-value mono">{{ row.mineruScore !== undefined && row.mineruScore !== null ? row.mineruScore.toFixed(4) : '-' }}</div>
              <div class="detail-hint">VLM 后端解析置信度，0-1 区间</div>
            </div>
            <div class="detail-section">
              <div class="detail-label">bbox [x0, y0, x1, y1]</div>
              <div class="detail-value mono">{{ formatBbox(row.bbox) }}</div>
              <div class="detail-hint">归一化到 0-1000 pipeline 惯例</div>
            </div>
            <div class="detail-section">
              <div class="detail-label">textLevel</div>
              <div class="detail-value mono">{{ row.textLevel !== undefined && row.textLevel !== null ? row.textLevel : '-' }}</div>
              <div class="detail-hint">0=正文 / 1=h1 / 2=h2...</div>
            </div>
            <div class="detail-block-data">
              <div class="detail-label">blockData 原始 JSON</div>
              <pre class="block-data-pre">{{ formatBlockData(row.blockData) || '(无)' }}</pre>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="#" type="index" width="50" />
      <el-table-column label="文档标题" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">{{ row.documentName }}</template>
      </el-table-column>
      <el-table-column label="块类型" width="110">
        <template #default="{ row }">
          <el-tag type="info" size="small">{{ extractBlockType(row.blockData) || '-' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="subType" width="110">
        <template #default="{ row }">
          <span class="text-muted-sm">{{ row.subType || '-' }}</span>
        </template>
      </el-table-column>
      <el-table-column label="页码" width="80">
        <template #default="{ row }">
          <el-tag type="info" size="small">P{{ row.pageNumber + 1 }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="匹配类型" width="100">
        <template #default="{ row }">
          <el-tag :type="matchTypeTagType(row.matchType)" size="small">{{ matchTypeLabel(row.matchType) }}</el-tag>
        </template>
      </el-table-column>
      <!-- RED LINE: BM25 Score and mineruScore must be explicit separate columns (not conflated) -->
      <el-table-column label="BM25 相关度" width="120">
        <template #default="{ row }">
          <span class="mono">{{ row.score.toFixed(2) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="矿工 U 置信度" width="120">
        <template #default="{ row }">
          <span class="mono">{{ mineruScoreText(row.mineruScore) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="textFormat" width="110">
        <template #default="{ row }">
          <span class="text-muted-sm">{{ row.textFormat || '-' }}</span>
        </template>
      </el-table-column>
      <el-table-column label="Caption" width="140" show-overflow-tooltip>
        <template #default="{ row }">{{ captionPreview(row.caption) }}</template>
      </el-table-column>
      <el-table-column label="匹配文本" min-width="280">
        <template #default="{ row }">
          <div class="match-text">{{ row.associatedText }}</div>
        </template>
      </el-table-column>
      <el-table-column label="Segment ID" width="160" show-overflow-tooltip>
        <template #default="{ row }">
          <span class="mono text-muted-sm">{{ row.segmentId }}</span>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="160">
        <template #default="{ row }">
          <span class="text-muted-sm">{{ row.createdAt ? formatDate(row.createdAt) : '-' }}</span>
        </template>
      </el-table-column>
    </el-table>

    <el-empty
      v-if="searchResults.length === 0 && searchQuery && !searchLoading"
      description="无匹配结果"
    />
  </el-card>

  <!-- [Gen-2] minerU advanced filter drawer (ADR §6: el-drawer) -->
  <el-drawer
    v-model="showAdvancedFilter"
    title="高级筛选 (minerU 第 2 代)"
    direction="rtl"
    size="420px"
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
    <template #footer>
      <div class="filter-actions">
        <el-button @click="clearFilters">清空筛选</el-button>
        <el-button
          type="primary"
          :loading="searchLoading"
          :disabled="!searchQuery.trim()"
          @click="showAdvancedFilter = false; handleSearch()"
        >
          应用筛选
        </el-button>
      </div>
    </template>
  </el-drawer>
</template>

<style scoped>
.page-card {
  border-radius: 8px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-weight: 600;
  font-size: 13px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}

.query-input {
  flex: 1;
  min-width: 280px;
}

.filter-active-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--el-color-warning);
  margin-left: 4px;
  vertical-align: middle;
}

.result-meta {
  margin-bottom: 8px;
  font-size: 13px;
  color: var(--el-text-color-regular);
}

.match-text {
  line-height: 1.5;
  color: var(--el-text-color-regular);
  white-space: pre-wrap;
  word-break: break-word;
}

.text-muted-sm {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.mono {
  font-family: 'SF Mono', Menlo, Monaco, Consolas, monospace;
}

/* RED LINE: row expand blockData detail styling preserved */
.detail-card {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 12px;
  align-items: start;
  padding: 8px 4px;
}

.detail-section {
  padding: 8px 12px;
  background: var(--el-fill-color-light);
  border-radius: 4px;
  border: 1px solid var(--el-border-color-lighter);
}

.detail-label {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  font-weight: 500;
  margin-bottom: 4px;
}

.detail-value {
  font-size: 14px;
  color: var(--el-text-color-primary);
}

.detail-hint {
  font-size: 11px;
  color: var(--el-text-color-placeholder);
  margin-top: 2px;
}

.detail-block-data {
  grid-column: 1 / -1;
}

.block-data-pre {
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--el-fill-color-blank);
  padding: 12px;
  border-radius: 4px;
  border: 1px solid var(--el-border-color-lighter);
  font-size: 12px;
  font-family: 'SF Mono', Menlo, Monaco, Consolas, monospace;
  max-height: 400px;
  overflow-y: auto;
  margin: 0;
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
  color: var(--el-text-color-secondary);
  font-weight: 500;
}

.filter-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>

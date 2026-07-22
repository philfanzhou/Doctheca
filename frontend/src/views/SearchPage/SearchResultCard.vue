<script setup lang="ts">
import { ref, computed } from 'vue'
import { formatDate } from '../../utils/format'
import { iconHtml } from '../../utils/icons'
import type { SearchResult } from '../../services/docApi'
import {
  extractBlockType,
  formatBbox,
  captionPreview,
  matchTypeBadgeHtml,
  mineruScoreText,
  scorePercent,
  highlightText,
  formatBlockData,
} from './searchFormatters'

const props = defineProps<{
  row: SearchResult
  index: number
  query: string
  devMode: boolean
}>()

const expanded = ref(false)

const detailCells = computed(() => [
  { label: 'BM25 相关度 (V1 Score)', value: props.row.score.toFixed(4), hint: 'OpenSearch _score，越大越相关' },
  {
    label: '矿工 U 置信度 (mineruScore)',
    value: props.row.mineruScore !== undefined && props.row.mineruScore !== null ? props.row.mineruScore.toFixed(4) : '-',
    hint: 'VLM 后端解析置信度，0-1 区间',
  },
  { label: 'bbox [x0, y0, x1, y1]', value: formatBbox(props.row.bbox), hint: '归一化到 0-1000 pipeline 惯例' },
  {
    label: 'textLevel',
    value: props.row.textLevel !== undefined && props.row.textLevel !== null ? props.row.textLevel : '-',
    hint: '0=正文 / 1=h1 / 2=h2…',
  },
  { label: 'Segment ID', value: props.row.segmentId, hint: '' },
])
</script>

<template>
  <div class="card hoverable result-card">
    <div class="result-head">
      <span class="result-page">P{{ props.row.pageNumber + 1 }}</span>
      <span v-if="extractBlockType(props.row.blockData)" class="badge indigo">{{ extractBlockType(props.row.blockData) }}</span>
      <span v-if="props.row.subType" class="td-sub">{{ props.row.subType }}</span>
      <span v-html="matchTypeBadgeHtml(props.row.matchType)"></span>
      <span v-if="props.row.textFormat" class="badge gray">{{ props.row.textFormat }}</span>
      <span class="spacer"></span>
      <span class="td-sub mono">#{{ props.index + 1 }}</span>
    </div>

    <div class="result-doc">{{ props.row.documentName }}</div>
    <div class="result-snippet" v-html="highlightText(props.row.associatedText, props.query)"></div>

    <div v-if="props.row.caption" class="td-sub" style="margin-top: 8px">
      caption: {{ captionPreview(props.row.caption) }}
    </div>

    <div class="result-foot">
      <div class="score-bar">
        <span>BM25</span>
        <div class="score-track">
          <div class="score-fill" :style="{ width: scorePercent(props.row.score) + '%' }"></div>
        </div>
        <span class="mono">{{ props.row.score.toFixed(2) }}</span>
      </div>
      <div class="score-bar" v-if="props.row.mineruScore !== undefined && props.row.mineruScore !== null">
        <span>minerU</span>
        <div class="score-track">
          <div class="score-fill" :style="{ width: (props.row.mineruScore * 100) + '%' }"></div>
        </div>
        <span class="mono">{{ mineruScoreText(props.row.mineruScore) }}</span>
      </div>
      <span class="td-sub">textLevel: {{ props.row.textLevel !== undefined && props.row.textLevel !== null ? props.row.textLevel : '-' }}</span>
      <span class="td-sub">bbox: <span class="mono">{{ formatBbox(props.row.bbox) }}</span></span>
      <span class="td-sub">{{ props.row.createdAt ? formatDate(props.row.createdAt) : '-' }}</span>
      <span class="spacer"></span>
      <button class="btn btn-ghost btn-sm" @click="expanded = !expanded">
        <span v-html="iconHtml('eye')"></span>{{ expanded ? '收起' : '查看 blockData' }}
      </button>
    </div>

    <div v-if="expanded" class="expanded-detail">
      <div class="detail-grid">
        <div v-for="cell in detailCells" :key="cell.label" class="detail-cell">
          <div class="detail-label">{{ cell.label }}</div>
          <div class="detail-value mono" style="word-break: break-all">{{ cell.value }}</div>
          <div v-if="cell.hint" class="detail-hint">{{ cell.hint }}</div>
        </div>
      </div>
      <div class="dev-block" v-if="props.devMode && props.row.blockData">
        <div class="block-label">blockData 原始 JSON</div>
        <pre>{{ formatBlockData(props.row.blockData) || '(无)' }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
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
</style>

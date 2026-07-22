<script setup lang="ts">
import type { DocumentFileDetail } from '../../services/docApi'

const props = defineProps<{
  detail: DocumentFileDetail
}>()

const stats = [
  { key: 'parsed', label: '已解析' },
  { key: 'parsing', label: '解析中' },
  { key: 'failed', label: '失败' },
  { key: 'vlm', label: 'VLM' },
  { key: 'pipeline', label: 'Pipeline' },
]

function count(key: string): number {
  const parses = props.detail.parses
  switch (key) {
    case 'parsed':
      return parses.filter((p) => p.status === 'parsed').length
    case 'parsing':
      return parses.filter((p) => p.status === 'parsing' || p.status === 'pending').length
    case 'failed':
      return parses.filter((p) => p.status === 'failed').length
    case 'vlm':
      return parses.filter((p) => p.modelVersion === 'vlm').length
    case 'pipeline':
      return parses.filter((p) => p.modelVersion === 'pipeline').length
    default:
      return 0
  }
}
</script>

<template>
  <div class="card">
    <div class="card-head">
      <div>
        <div class="card-title">解析记录</div>
        <div class="card-sub">共 {{ props.detail.parses.length }} 条解析</div>
      </div>
    </div>
    <div class="stats-body">
      <div v-for="item in stats" :key="item.key" class="stat-mini">
        <div class="stat-num">{{ count(item.key) }}</div>
        <div class="stat-foot">{{ item.label }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.stats-body {
  display: flex;
  gap: 18px;
  padding: 8px 0;
  flex-wrap: wrap;
}

.stat-mini {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}

.stat-num {
  font-size: 22px;
  font-weight: 680;
  letter-spacing: -0.5px;
  font-variant-numeric: tabular-nums;
  line-height: 1.1;
}

.stat-foot {
  font-size: 12px;
  color: var(--text-3);
}
</style>

<script setup lang="ts">
import { ref, computed, onMounted, inject } from 'vue'
import { createDocApiClient, type DocumentFile, type DocumentParse } from '../services/docApi'
import { formatTime, getFileStatusLabel } from '../utils/format'
import { useToast } from '../composables/useToast'
import { useCountUp } from '../composables/useCountUp'
import ChartLine from '../components/ChartLine.vue'
import ChartDonut from '../components/ChartDonut.vue'
import { iconHtml } from '../utils/icons'
import { fileIconCls, fileExtLabel } from '../utils/file'

const client = createDocApiClient()
const { error: toastError } = useToast()

const rootRef = ref<HTMLElement | null>(null)
const files = ref<DocumentFile[]>([])
const parses = ref<DocumentParse[]>([])
const loading = ref(false)
useCountUp(rootRef)

const stats = computed(() => {
  const total = files.value.length
  const parsed = files.value.filter((f) => f.parseStatus === 'parsed').length
  const parsing = files.value.filter((f) => f.parseStatus === 'parsing' || f.parseStatus === 'pending').length
  const unparsed = files.value.filter((f) => f.parseStatus === null || f.parseStatus === 'unparsed').length
  const failed = files.value.filter((f) => f.parseStatus === 'failed').length
  return { total, parsed, parsing, unparsed, failed }
})

const donutSegments = computed(() => [
  { value: stats.value.parsed, color: '#10B981', label: '已解析' },
  { value: stats.value.unparsed, color: '#C7CBD9', label: '未解析' },
  { value: stats.value.parsing, color: '#0EA5E9', label: '解析中' },
  { value: stats.value.failed, color: '#EF4444', label: '失败' },
])

const legend = computed(() => [
  { c: '#10B981', l: `已解析 ${stats.value.parsed}` },
  { c: '#C7CBD9', l: `未解析 ${stats.value.unparsed}` },
  { c: '#0EA5E9', l: `解析中 ${stats.value.parsing}` },
  { c: '#EF4444', l: `失败 ${stats.value.failed}` },
])

// Build 30-day bucket from parse records (parsedAt).
const trend = computed(() => {
  const days = 30
  const arr = new Array(days).fill(0)
  const now = new Date()
  const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  for (const p of parses.value) {
    if (!p.parsedAt) continue
    const t = new Date(p.parsedAt).getTime()
    const diff = Math.floor((todayStart - t) / 86400000)
    if (diff >= 0 && diff < days) {
      arr[days - 1 - diff]++
    }
  }
  return arr
})

// Axis labels for the trend chart, matching the real 30-day window (last tick is today).
// Tick indices mirror ChartLine's even label distribution: round(i * (n - 1) / (count - 1)).
const trendLabels = computed(() => {
  const days = trend.value.length
  if (days < 2) return []
  const count = Math.min(7, days)
  const now = new Date()
  const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  return Array.from({ length: count }, (_, i) => {
    const idx = Math.round((i * (days - 1)) / (count - 1))
    if (idx === days - 1) return '今天'
    const d = new Date(todayStart - (days - 1 - idx) * 86400000)
    return `${d.getMonth() + 1}/${d.getDate()}`
  })
})

const recentFiles = computed(() => files.value.slice(0, 5))

const fileStatusBadge = (status: string | null): string => {
  if (status === 'parsed') return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed') return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending')
    return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}

async function load() {
  loading.value = true
  try {
    const [filesResp, parsesResp] = await Promise.all([
      client.listDocumentFiles(1, 100),
      client.listDocumentParses(1, 100),
    ])
    files.value = filesResp.data as DocumentFile[]
    parses.value = parsesResp.data as DocumentParse[]
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载概览数据失败')
  } finally {
    loading.value = false
  }
}

const openDocDetail = inject<(id: string) => void>('openDocDetail')
const navigate = inject<(page: string, docId?: string | null) => void>('navigate')

onMounted(load)
</script>

<template>
  <div ref="rootRef">
    <div class="page-head">
      <div>
        <div class="page-title">文档库概览</div>
        <div class="page-sub">文档解析和检索索引状态</div>
      </div>
      <div class="page-actions">
        <button class="btn" @click="navigate?.('docs')">
          <span v-html="iconHtml('upload')"></span>上传文档
        </button>
      </div>
    </div>

    <div class="stat-grid">
      <div class="card hoverable stat-card">
        <div class="stat-label"><span v-html="iconHtml('file')"></span>文档总数</div>
        <div class="stat-num" :data-count="stats.total">0</div>
        <div class="stat-foot">最多展示前 100 条</div>
      </div>
      <div class="card hoverable stat-card">
        <div class="stat-label"><span v-html="iconHtml('clock')"></span>解析中</div>
        <div class="stat-num" :data-count="stats.parsing">0</div>
        <div class="stat-foot">包含等待解析的任务</div>
      </div>
      <div class="card hoverable stat-card">
        <div class="stat-label"><span v-html="iconHtml('check')"></span>已解析</div>
        <div class="stat-num" :data-count="stats.parsed">0</div>
        <div class="stat-foot">可检索</div>
      </div>
      <div class="card hoverable stat-card">
        <div class="stat-label"><span v-html="iconHtml('warnTri')"></span>解析失败</div>
        <div class="stat-num" :data-count="stats.failed">0</div>
        <div class="stat-foot"><span class="trend-down">需关注</span></div>
      </div>
    </div>

    <div class="grid-2r">
      <div class="card">
        <div class="card-head">
          <div>
            <div class="card-title">解析状态分布</div>
            <div class="card-sub">基于最近 100 条文档记录</div>
          </div>
        </div>
        <div style="display: flex; align-items: center; gap: 22px; justify-content: center; padding: 6px 0">
          <ChartDonut :segments="donutSegments" :center-value="stats.total" center-label="文档总数" />
          <div class="feed" style="flex: 1">
            <div
              v-for="(item, i) in legend"
              :key="i"
              style="display: flex; align-items: center; gap: 9px; padding: 7px 0; font-size: 13px; color: var(--text-2)"
            >
              <i :style="{ width: '9px', height: '9px', borderRadius: '3px', background: item.c, display: 'inline-block' }"></i>
              {{ item.l }}
            </div>
          </div>
        </div>
      </div>
      <div class="card">
        <div class="card-head">
          <div>
            <div class="card-title">解析完成数量</div>
            <div class="card-sub">近 30 天完成的解析任务数</div>
          </div>
        </div>
        <ChartLine :data="trend" :labels="trendLabels" />
      </div>
    </div>

    <div class="card section-gap">
      <div class="card-head">
        <div>
          <div class="card-title">最近上传</div>
          <div class="card-sub">最近上传的文档</div>
        </div>
        <button class="btn btn-ghost btn-sm" @click="navigate?.('docs')">全部文档</button>
      </div>
      <div class="feed" v-if="recentFiles.length">
        <div
          v-for="f in recentFiles"
          :key="f.id"
          class="feed-item"
          style="cursor: pointer"
          @click="openDocDetail?.(f.id)"
        >
          <div class="file-ico" :class="fileIconCls(f.contentType)">{{ fileExtLabel(f.contentType) }}</div>
          <div class="feed-body">
            <div class="feed-title">{{ f.fileName }}</div>
            <div class="feed-meta">
              <span>{{ formatTime(f.createdAt) }}</span>
            </div>
          </div>
          <span v-html="fileStatusBadge(f.parseStatus)"></span>
        </div>
      </div>
      <div v-else class="empty">
        <span v-html="iconHtml('file')"></span><br />
        暂无文档
      </div>
    </div>
  </div>
</template>

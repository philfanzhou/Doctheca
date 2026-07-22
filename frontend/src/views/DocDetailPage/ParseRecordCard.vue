<script setup lang="ts">
import { formatTime, getFileStatusLabel } from '../../utils/format'
import { iconHtml } from '../../utils/icons'
import { createDocApiClient } from '../../services/docApi'
import { useToast } from '../../composables/useToast'
import {
  openMarkdown,
  openHtmlPreview,
  openRawJson,
  openImages,
  downloadBlob,
} from '../../utils/preview'
import type { DocumentFileDetail } from '../../services/docApi'

const props = defineProps<{
  detail: DocumentFileDetail
  parse: DocumentFileDetail['parses'][number]
  deleting: string | null
}>()

const emit = defineEmits<{
  (e: 'delete', parseId: string): void
}>()

const client = createDocApiClient()
const { success: toastSuccess, error: toastError } = useToast()

function statusBadgeHtml(status: string): string {
  if (status === 'parsed') return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed') return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending') return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}

function handleOpenMarkdown() {
  if (!props.parse.markdownContent) {
    toastError('该解析结果没有 Markdown 内容')
    return
  }
  openMarkdown(props.detail.fileName, props.parse.markdownContent)
}

async function handleOpenHtmlPreview() {
  try {
    await openHtmlPreview(client.exportParseHtml(props.parse.id).then((r) => r.blob))
  } catch (e) {
    toastError(e instanceof Error ? e.message : '生成 HTML 失败')
  }
}

function handleOpenRawJson(field: 'contentList' | 'contentListV2' | 'modelJson' | 'layoutJson', label: string) {
  const rawContent = props.parse[field]
  if (!rawContent || rawContent === '[]' || rawContent === 'null') {
    toastError(`该解析结果没有 ${label} 数据`)
    return
  }
  openRawJson(props.detail.fileName, label, rawContent)
}

function handleOpenImages() {
  if (!props.parse.images?.length) {
    toastError('该解析结果没有图片')
    return
  }
  openImages(props.detail.fileName, props.parse.images)
}

async function handleExportMarkdown() {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(props.parse.id)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}

async function handleExportHtml() {
  try {
    const { blob, fileName } = await client.exportParseHtml(props.parse.id)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}
</script>

<template>
  <div class="parse-item">
    <div class="parse-head">
      <div class="cell-flex" style="gap: 12px">
        <span class="badge" :class="props.parse.modelVersion === 'pipeline' ? 'green' : 'amber'">
          {{ props.parse.modelVersion === 'pipeline' ? 'Pipeline' : 'VLM' }}
        </span>
        <span v-html="statusBadgeHtml(props.parse.status)"></span>
        <span v-if="props.parse.parsedAt" class="td-sub">{{ formatTime(props.parse.parsedAt) }}</span>
      </div>
      <button
        class="btn btn-danger btn-sm"
        :disabled="props.deleting === props.parse.id"
        @click="emit('delete', props.parse.id)"
      >
        <span v-html="iconHtml('trash')"></span>删除
      </button>
    </div>

    <div v-if="props.parse.errorMessage" class="error-box">
      <span v-html="iconHtml('warnTri')"></span>
      <span>{{ props.parse.errorMessage }}</span>
    </div>

    <div v-if="props.parse.status === 'parsed'" class="parse-actions">
      <button class="btn btn-sm" @click="handleOpenMarkdown">
        <span v-html="iconHtml('doc')"></span>Markdown
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenHtmlPreview">
        <span v-html="iconHtml('eye')"></span>HTML 预览
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenRawJson('contentList', 'content_list.json')">
        <span v-html="iconHtml('file')"></span>JSON
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenRawJson('contentListV2', 'content_list_v2.json')">
        <span v-html="iconHtml('file')"></span>V2
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenRawJson('modelJson', 'model.json')">
        <span v-html="iconHtml('file')"></span>Model
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenRawJson('layoutJson', 'layout.json')">
        <span v-html="iconHtml('file')"></span>Layout
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleOpenImages">
        <span v-html="iconHtml('image')"></span>图片 ({{ props.parse.images?.length || 0 }})
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleExportMarkdown">
        <span v-html="iconHtml('upload')"></span>导出 MD
      </button>
      <button class="btn btn-ghost btn-sm" @click="handleExportHtml">
        <span v-html="iconHtml('upload')"></span>导出 HTML
      </button>
    </div>

    <div v-else-if="props.parse.status === 'parsing' || props.parse.status === 'pending'" class="parsing-box">
      <div class="progress-track" style="max-width: 100%">
        <div class="progress-fill parsing" style="width: 100%"></div>
      </div>
      <div class="td-sub" style="margin-top: 6px">MinerU 正在解析，5 秒后自动刷新…</div>
    </div>
  </div>
</template>

<style scoped>
.parse-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.parse-actions :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}

.error-box :deep(svg) {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
}

.progress-track {
  height: 6px;
  background: var(--surface-2);
  border-radius: 6px;
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  background: var(--primary);
  border-radius: 6px;
  transition: width 0.3s var(--ease);
}

.progress-fill.parsing {
  background: linear-gradient(90deg, var(--primary) 0%, #6366f1 50%, var(--primary) 100%);
  background-size: 200% 100%;
  animation: progPulse 1.4s var(--ease) infinite;
}

@keyframes progPulse {
  0% { background-position: 0% 0%; }
  100% { background-position: -200% 0%; }
}
</style>

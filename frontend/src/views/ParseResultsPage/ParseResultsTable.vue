<script setup lang="ts">
import { formatTime, getFileStatusLabel } from '../../utils/format'
import { iconHtml } from '../../utils/icons'
import { createDocApiClient } from '../../services/docApi'
import { useToast } from '../../composables/useToast'
import type { DocumentParse } from '../../services/docApi'
import {
  openMarkdown,
  openHtmlPreview,
  openRawJson,
  openImages,
  downloadBlob,
} from '../../utils/preview'

const props = defineProps<{
  rows: DocumentParse[]
  deleting: string | null
  loading: boolean
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

function modelBadgeHtml(modelVersion: string): string {
  if (modelVersion === 'pipeline') return `<span class="badge green">Pipeline</span>`
  return `<span class="badge amber">VLM</span>`
}

async function openMarkdownForRow(row: DocumentParse) {
  try {
    const response = await client.getDocumentFile(row.fileId)
    const detail = response.data
    const parse = detail.parses?.find((p) => p.id === row.id)
    if (!parse?.markdownContent) {
      toastError('该解析结果没有 Markdown 内容')
      return
    }
    openMarkdown(detail.fileName, parse.markdownContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载 Markdown 失败')
  }
}

async function openHtmlPreviewForRow(parseId: string) {
  try {
    await openHtmlPreview(client.exportParseHtml(parseId).then((r) => r.blob))
  } catch (e) {
    toastError(e instanceof Error ? e.message : '生成 HTML 失败')
  }
}

async function openJsonForRow(row: DocumentParse, field: 'contentList' | 'contentListV2' | 'modelJson' | 'layoutJson', label: string) {
  try {
    const response = await client.getDocumentFile(row.fileId)
    const detail = response.data
    const parse = detail.parses?.find((p) => p.id === row.id)
    const rawContent = parse?.[field]
    if (!rawContent || rawContent === '[]' || rawContent === 'null') {
      toastError(`该解析结果没有 ${label} 数据`)
      return
    }
    openRawJson(detail.fileName, label, rawContent)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载数据失败')
  }
}

async function openImagesForRow(row: DocumentParse) {
  try {
    const response = await client.getDocumentFile(row.fileId)
    const detail = response.data
    const parse = detail.parses?.find((p) => p.id === row.id)
    if (!parse?.images?.length) {
      toastError('该解析结果没有图片')
      return
    }
    openImages(detail.fileName, parse.images)
  } catch (e) {
    toastError(e instanceof Error ? e.message : '加载图片失败')
  }
}

async function exportMarkdown(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseMarkdown(parseId)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}

async function exportHtml(parseId: string) {
  try {
    const { blob, fileName } = await client.exportParseHtml(parseId)
    downloadBlob(blob, fileName)
    toastSuccess('导出成功')
  } catch (e) {
    toastError(e instanceof Error ? e.message : '导出失败')
  }
}
</script>

<template>
  <div v-loading="props.loading" style="min-height: 200px">
    <div class="table-wrap">
      <table class="data-table">
        <thead>
          <tr>
            <th>文件名</th>
            <th>模型</th>
            <th>状态</th>
            <th>解析时间</th>
            <th>错误信息</th>
            <th style="text-align: right">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in props.rows" :key="row.id">
            <td>
              <div class="cell-flex">
                <div class="file-ico docx">DOC</div>
                <div>
                  <div class="td-main" style="max-width: 360px">{{ row.fileName }}</div>
                  <div class="td-sub mono">{{ row.id.slice(0, 8) }}…</div>
                </div>
              </div>
            </td>
            <td><span v-html="modelBadgeHtml(row.modelVersion)"></span></td>
            <td><span v-html="statusBadgeHtml(row.status)"></span></td>
            <td class="mono" style="color: var(--text-3); font-size: 12.5px">
              {{ row.parsedAt ? formatTime(row.parsedAt) : '-' }}
            </td>
            <td style="max-width: 260px">
              <span v-if="row.errorMessage" class="error-text">{{ row.errorMessage }}</span>
              <span v-else style="color: var(--text-3)">-</span>
            </td>
            <td style="text-align: right; white-space: nowrap">
              <template v-if="row.status === 'parsed'">
                <button class="btn btn-sm" @click="openMarkdownForRow(row)">MD</button>
                <button class="btn btn-ghost btn-sm" @click="openHtmlPreviewForRow(row.id)">HTML</button>
                <button class="btn btn-ghost btn-sm" @click="openJsonForRow(row, 'contentList', 'content_list.json')">JSON</button>
                <button class="btn btn-ghost btn-sm" @click="openJsonForRow(row, 'contentListV2', 'content_list_v2.json')">V2</button>
                <button class="btn btn-ghost btn-sm" @click="openJsonForRow(row, 'modelJson', 'model.json')">Model</button>
                <button class="btn btn-ghost btn-sm" @click="openJsonForRow(row, 'layoutJson', 'layout.json')">Layout</button>
                <button class="btn btn-ghost btn-sm" @click="openImagesForRow(row)">图片</button>
                <button class="btn btn-ghost btn-sm" @click="exportMarkdown(row.id)">导出MD</button>
                <button class="btn btn-ghost btn-sm" @click="exportHtml(row.id)">导出HTML</button>
              </template>
              <button
                class="btn btn-danger btn-sm"
                :disabled="props.deleting === row.id"
                @click="emit('delete', row.id)"
              >
                <span v-html="iconHtml('trash')"></span>删除
              </button>
            </td>
          </tr>
          <tr v-if="!props.rows.length && !props.loading">
            <td colspan="6">
              <div class="empty">
                <span v-html="iconHtml('doc')"></span><br />
                暂无解析记录
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.error-text {
  color: #b91c1c;
  font-size: 12.5px;
  display: inline-block;
  max-width: 260px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: middle;
}
</style>

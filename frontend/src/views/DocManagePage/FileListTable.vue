<script setup lang="ts">
import { formatTime, getFileStatusLabel } from '../../utils/format'
import { iconHtml } from '../../utils/icons'
import { fileIconCls, fileExtLabel } from '../../utils/file'
import type { DocumentFile } from '../../services/docApi'

const props = defineProps<{
  files: DocumentFile[]
  parsing: boolean
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'row-click', file: DocumentFile): void
  (e: 'parse', id: string, modelVersion: string): void
  (e: 'delete', id: string): void
}>()

function statusBadgeHtml(status: string | null): string {
  if (status === 'parsed')
    return `<span class="badge green"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'failed')
    return `<span class="badge red"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
  if (status === 'parsing' || status === 'pending')
    return `<span class="badge blue"><span class="dot pulse"></span>${getFileStatusLabel(status)}</span>`
  return `<span class="badge gray"><span class="dot"></span>${getFileStatusLabel(status)}</span>`
}
</script>

<template>
  <div v-loading="props.loading" style="min-height: 200px">
    <div class="table-wrap">
      <table class="data-table">
        <thead>
          <tr>
            <th>文件名</th>
            <th>类型</th>
            <th>上传时间</th>
            <th>解析状态</th>
            <th style="text-align: right">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="f in props.files"
            :key="f.id"
            class="clickable"
            @click="emit('row-click', f)"
          >
            <td>
              <div class="cell-flex">
                <div class="file-ico" :class="fileIconCls(f.contentType)">{{ fileExtLabel(f.contentType) }}</div>
                <div>
                  <div class="td-main" style="max-width: 360px">{{ f.fileName }}</div>
                </div>
              </div>
            </td>
            <td>
              <span class="mono" style="font-size: 12px; color: var(--text-2)">{{ f.contentType }}</span>
            </td>
            <td style="color: var(--text-3); font-size: 12.5px; font-variant-numeric: tabular-nums">
              {{ formatTime(f.createdAt) }}
            </td>
            <td>
              <span v-html="statusBadgeHtml(f.parseStatus)"></span>
            </td>
            <td style="text-align: right; white-space: nowrap" @click.stop
            >
              <template
                v-if="f.parseStatus === null || f.parseStatus === 'unparsed' || f.parseStatus === 'failed'"
              >
                <button class="btn btn-sm" :disabled="props.parsing" @click="emit('parse', f.id, 'vlm')">VLM 解析</button>
                <button class="btn btn-ghost btn-sm" :disabled="props.parsing" @click="emit('parse', f.id, 'pipeline')">Pipeline</button>
              </template>
              <template v-else-if="f.parseStatus === 'pending' || f.parseStatus === 'parsing'">
                <span class="badge blue"><span class="dot pulse"></span>解析中</span>
              </template>
              <template v-else-if="f.parseStatus === 'parsed'">
                <button class="btn btn-sm" :disabled="props.parsing" @click="emit('parse', f.id, 'vlm')">重新解析</button>
              </template>
              <button class="btn btn-danger btn-sm" @click="emit('delete', f.id)">
                <span v-html="iconHtml('trash')"></span>删除
              </button>
            </td>
          </tr>
          <tr v-if="!props.files.length && !props.loading">
            <td colspan="5">
              <div class="empty">
                <span v-html="iconHtml('file')"></span><br />
                暂无文件，点击上方上传
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

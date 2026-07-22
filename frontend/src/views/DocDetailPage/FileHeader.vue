<script setup lang="ts">
import { formatTime } from '../../utils/format'
import { iconHtml } from '../../utils/icons'
import type { DocumentFileDetail } from '../../services/docApi'
import { fileIconCls, fileExtLabel } from '../../utils/file'

const props = defineProps<{
  detail: DocumentFileDetail
  parsing: boolean
}>()

const emit = defineEmits<{
  (e: 'parse', modelVersion: string): void
  (e: 'delete'): void
}>()
</script>

<template>
  <div class="page-head">
    <div style="display: flex; align-items: center; gap: 14px">
      <div class="file-ico" :class="fileIconCls(props.detail.contentType)">{{ fileExtLabel(props.detail.contentType) }}</div>
      <div>
        <div class="page-title">{{ props.detail.fileName }}</div>
        <div class="page-sub">
          {{ props.detail.contentType }} · 上传于 {{ formatTime(props.detail.createdAt) }}
        </div>
      </div>
    </div>
    <div class="page-actions">
      <button class="btn btn-sm" :disabled="props.parsing" @click="emit('parse', 'vlm')">
        <span v-html="iconHtml('zap')"></span>VLM 解析
      </button>
      <button class="btn btn-ghost btn-sm" :disabled="props.parsing" @click="emit('parse', 'pipeline')">
        <span v-html="iconHtml('refresh')"></span>Pipeline
      </button>
      <button class="btn btn-danger btn-sm" @click="emit('delete')">
        <span v-html="iconHtml('trash')"></span>删除文件
      </button>
    </div>
  </div>
</template>

<style scoped>
.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

.page-actions :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}
</style>

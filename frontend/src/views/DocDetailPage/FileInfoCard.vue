<script setup lang="ts">
import { formatTime } from '../../utils/format'
import type { DocumentFileDetail } from '../../services/docApi'

const props = defineProps<{
  detail: DocumentFileDetail
}>()
</script>

<template>
  <div class="card">
    <div class="card-head">
      <div>
        <div class="card-title">文件信息</div>
        <div class="card-sub">原始文档元数据</div>
      </div>
    </div>
    <div class="kv-body">
      <div class="kv-row">
        <span class="kv-key">文件名</span>
        <span class="kv-val">{{ props.detail.fileName }}</span>
      </div>
      <div class="kv-row">
        <span class="kv-key">类型</span>
        <span class="kv-val mono">{{ props.detail.contentType }}</span>
      </div>
      <div class="kv-row">
        <span class="kv-key">上传时间</span>
        <span class="kv-val mono">{{ formatTime(props.detail.createdAt) }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.kv-body {
  padding: 4px 0;
}

.kv-row {
  display: flex;
  padding: 7px 0;
  font-size: 13px;
  border-bottom: 1px solid var(--border-2);
}

.kv-row:last-child {
  border-bottom: none;
}

.kv-key {
  width: 90px;
  color: var(--text-3);
  flex-shrink: 0;
}

.kv-val {
  color: var(--text);
  word-break: break-all;
}
</style>

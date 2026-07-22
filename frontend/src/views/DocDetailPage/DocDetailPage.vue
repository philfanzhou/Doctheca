<script setup lang="ts">
import { inject } from 'vue'
import { iconHtml } from '../../utils/icons'
import FileHeader from './FileHeader.vue'
import FileInfoCard from './FileInfoCard.vue'
import ParseStatsCard from './ParseStatsCard.vue'
import ParseRecordCard from './ParseRecordCard.vue'
import { useDocumentDetail } from './useDocumentDetail'

interface Props {
  docId?: string | null
}
const props = defineProps<Props>()

const backToDocs = inject<() => void>('backToDocs')

const {
  detail,
  loading,
  parsing,
  deleting,
  handleParse,
  handleDeleteFile,
  handleDeleteParse,
} = useDocumentDetail(props.docId)
</script>

<template>
  <div v-loading="loading">
    <button class="back-btn" @click="backToDocs?.()">
      <span v-html="iconHtml('back')"></span>
      返回文档列表
    </button>

    <FileHeader
      v-if="detail"
      :detail="detail"
      :parsing="parsing"
      @parse="handleParse"
      @delete="handleDeleteFile"
    />

    <div v-if="detail" class="grid-2r" style="margin-bottom: 16px">
      <FileInfoCard :detail="detail" />
      <ParseStatsCard :detail="detail" />
    </div>

    <div v-if="detail" class="card">
      <div class="card-head">
        <div>
          <div class="card-title">解析记录详情</div>
          <div class="card-sub">每条记录的产物预览与导出</div>
        </div>
      </div>

      <div v-if="!detail.parses.length" class="empty">
        <span v-html="iconHtml('doc')"></span><br />
        暂无解析记录，请点击上方按钮触发解析
      </div>

      <div v-else class="parse-list">
        <ParseRecordCard
          v-for="parse in detail.parses"
          :key="parse.id"
          :detail="detail"
          :parse="parse"
          :deleting="deleting"
          @delete="handleDeleteParse"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.back-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--text-2);
  margin-bottom: 14px;
  transition: color 0.15s;
  background: none;
  border: none;
  cursor: pointer;
  padding: 0;
}

.back-btn:hover {
  color: var(--primary);
}

.back-btn :deep(svg) {
  width: 16px;
  height: 16px;
}

.parse-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
</style>

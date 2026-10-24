<script setup lang="ts">
import { inject, ref } from 'vue'
import { iconHtml } from '../../utils/icons'
import BookMetadataForm from '../../components/BookMetadataForm.vue'
import { attachFirstSource } from '../../services/documentApi'
import { getApiMessage, getStructureFieldErrors } from '../../services/structureApi'
import { useToast } from '../../composables/useToast'
import FileHeader from './FileHeader.vue'
import FileInfoCard from './FileInfoCard.vue'
import ParseStatsCard from './ParseStatsCard.vue'
import ParseRecordCard from './ParseRecordCard.vue'
import { useDocumentDetail } from './useDocumentDetail'

interface Props {
  docId?: string | null
}
const props = defineProps<Props>()

const editing = ref(false)
const attaching = ref(false)
const attachmentError = ref('')
const navigate = inject<(page: string, id: string) => void>('navigate')
const { success } = useToast()
async function attach(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file || !props.docId) return
  attaching.value = true; attachmentError.value = ''
  try { await attachFirstSource(props.docId, file); await loadDetail(); success('首个源文件已附加，请显式启动解析') }
  catch (e) { attachmentError.value = getStructureFieldErrors(e).map(f => `${f.field}: ${f.reason}`).join('; ') || getApiMessage(e, '附件失败') }
  finally { attaching.value = false; input.value = '' }
}
const backToDocs = inject<() => void>('backToDocs')

const {
  detail,
  loadDetail,
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

    <div v-if="detail" class="card section-gap">
      <button class="btn" @click="navigate?.('document-structure', detail.id)">编辑书籍章节</button>
      <button class="btn btn-ghost" @click="editing = true">编辑书籍信息</button>
      <template v-if="!detail.hasSource">
        <p>仅结构书籍：可先编排章节，稍后附加首个 PDF 或 Office 文件。附件不会自动解析。</p>
        <label>附加首个源文件 <input aria-label="附加首个源文件" type="file" :disabled="attaching" accept=".pdf,.doc,.docx,.ppt,.pptx" @change="attach" /></label>
        <p role="alert">{{ attachmentError }}</p>
      </template>
      <button class="btn" @click="navigate?.('document-audio', detail.id)">管理书籍音频</button>
    </div>
    <BookMetadataForm v-if="detail && editing" :detail="detail" @cancel="editing = false" @saved="() => { editing = false; loadDetail() }" />
    <div v-if="detail" class="grid-2r" style="margin-bottom: 16px">
      <FileInfoCard :detail="detail" @definition-changed="detail.structureDefinitionId = $event" />
      <ParseStatsCard :detail="detail" />
    </div>

    <div v-if="detail" class="card">
      <div class="card-head">
        <div>
          <div class="card-title">解析记录详情</div>
          <div class="card-sub">预览和导出每次解析的结果</div>
        </div>
      </div>

      <div v-if="!detail.parses.length" class="empty">
        <span v-html="iconHtml('doc')"></span><br />
        {{ detail.hasSource ? '暂无解析记录，请点击上方按钮触发解析' : '暂无解析记录；无源文件时不创建解析。' }}
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

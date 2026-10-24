<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { formatTime } from '../../utils/format'
import { useToast } from '../../composables/useToast'
import type { DocumentFileDetail } from '../../services/docApi'
import {
  getApiMessage,
  listStructureDefinitions,
  setDocumentStructureDefinition,
  type StructureDefinition,
} from '../../services/structureApi'

const props = defineProps<{
  detail: DocumentFileDetail
}>()

const emit = defineEmits<{
  (e: 'definition-changed', structureDefinitionId: string | null): void
}>()

const { success: toastSuccess, error: toastError } = useToast()
const definitions = ref<StructureDefinition[]>([])
const selectedDefinition = ref<string>(props.detail.structureDefinitionId ?? '')
const savingDefinition = ref(false)

watch(
  () => props.detail.structureDefinitionId,
  (value) => {
    selectedDefinition.value = value ?? ''
  }
)

async function changeDefinition(value: string | undefined) {
  const next = value ?? ''
  const previous = props.detail.structureDefinitionId ?? ''
  if (next === previous) return
  savingDefinition.value = true
  try {
    const response = await setDocumentStructureDefinition(props.detail.id, next)
    emit('definition-changed', response.data.structureDefinitionId)
    toastSuccess(next ? '已设置结构定义' : '已清除结构定义')
  } catch (e) {
    selectedDefinition.value = previous
    const status = (e as { response?: { status?: number } })?.response?.status
    toastError(status === 409 ? '该文档已有标注的结构节点，请先清空标注再更换结构定义' : getApiMessage(e, '设置结构定义失败'))
  } finally {
    savingDefinition.value = false
  }
}

onMounted(async () => {
  try {
    definitions.value = (await listStructureDefinitions()).data
  } catch {
    definitions.value = []
  }
})
</script>

<template>
  <div class="card">
    <div class="card-head">
      <div>
        <div class="card-title">书籍信息</div>
        <div class="card-sub">书名、元数据与结构定义</div>
      </div>
    </div>
    <div class="kv-body">
      <div class="kv-row">
        <span class="kv-key">文件名</span>
        <span class="kv-val">{{ props.detail.fileName }}</span>
      </div>
      <div class="kv-row">
        <span class="kv-key">类型</span>
        <span class="kv-val mono">{{ props.detail.contentType || '无源文件' }}</span>
      </div>
      <div class="kv-row">
        <span class="kv-key">创建时间</span>
        <span class="kv-val mono">{{ formatTime(props.detail.createdAt) }}</span>
      </div>
      <div class="kv-row"><span class="kv-key">学科 / 年级 / 年份</span><span class="kv-val">{{ props.detail.subject || '—' }} / {{ props.detail.grade || '—' }} / {{ props.detail.year || '—' }}</span></div>
      <div class="kv-row kv-row-control">
        <span class="kv-key">结构定义</span>
        <el-select
          v-model="selectedDefinition"
          placeholder="未设置"
          clearable
          size="small"
          aria-label="结构定义"
          :loading="savingDefinition"
          style="width: 220px"
          @change="changeDefinition"
        >
          <el-option v-for="definition in definitions" :key="definition.id" :label="definition.name" :value="definition.id" />
        </el-select>
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

.kv-row-control {
  align-items: center;
}

.kv-val {
  color: var(--text);
  word-break: break-all;
}
</style>

<script setup lang="ts">
import AppDrawer from '../../components/AppDrawer.vue'
import type { SearchFilters } from './useSearch'

const props = defineProps<{
  open: boolean
  filters: SearchFilters
}>()

const emit = defineEmits<{
  (e: 'update:filters', value: SearchFilters): void
  (e: 'clear'): void
  (e: 'apply'): void
  (e: 'close'): void
}>()

const blockTypeOptions = [
  'text', 'title', 'image', 'table', 'chart', 'list', 'index', 'interline_equation',
  'code', 'algorithm', 'equation', 'phonetic', 'ref_text',
  'header', 'footer', 'page_number', 'aside_text', 'page_footnote'
]

const subTypePlaceholder = '如 table_caption / code / algorithm / image_body'

const textFormatOptions = [
  { value: '', label: '(不限)' },
  { value: 'latex', label: 'latex' },
  { value: 'markdown', label: 'markdown' },
  { value: 'none', label: 'none' }
]

function updateField<K extends keyof SearchFilters>(key: K, value: SearchFilters[K]) {
  emit('update:filters', { ...props.filters, [key]: value })
}
</script>

<template>
  <AppDrawer
    :open="props.open"
    title="高级筛选"
    subtitle="minerU 第 2 代块级过滤"
    @close="emit('close')"
  >
    <div class="filter-form">
      <div class="filter-field">
        <label class="filter-label">块类型 (blockType)</label>
        <el-select
          :model-value="props.filters.blockType"
          filterable
          allow-create
          clearable
          default-first-option
          placeholder="text / image / table..."
          style="width: 100%"
          @update:model-value="updateField('blockType', $event as string)"
        >
          <el-option v-for="t in blockTypeOptions" :key="t" :label="t" :value="t" />
        </el-select>
      </div>
      <div class="filter-field">
        <label class="filter-label">二级类型 (subType)</label>
        <el-input
          :model-value="props.filters.blockSubType"
          :placeholder="subTypePlaceholder"
          clearable
          @update:model-value="updateField('blockSubType', $event)"
        />
      </div>
      <div class="filter-field">
        <label class="filter-label">页码 (pageNumber)</label>
        <el-input-number
          :model-value="props.filters.pageNumber"
          :min="0"
          controls-position="right"
          placeholder="0-based page_idx"
          style="width: 100%"
          @update:model-value="updateField('pageNumber', $event as number | undefined)"
        />
      </div>
      <div class="filter-field">
        <label class="filter-label">标题层级 (textLevel)</label>
        <el-input-number
          :model-value="props.filters.textLevel"
          :min="-1"
          controls-position="right"
          placeholder="0=正文 / 1=h1 / 2=h2"
          style="width: 100%"
          @update:model-value="updateField('textLevel', $event as number | undefined)"
        />
      </div>
      <div class="filter-field">
        <label class="filter-label">文本格式 (textFormat)</label>
        <el-select
          :model-value="props.filters.textFormat"
          clearable
          style="width: 100%"
          @update:model-value="updateField('textFormat', $event as string)"
        >
          <el-option v-for="f in textFormatOptions" :key="f.value" :label="f.label" :value="f.value" />
        </el-select>
      </div>
      <div class="filter-field">
        <label class="filter-label">解析 ID (parseId)</label>
        <el-input
          :model-value="props.filters.parseId"
          placeholder="Guid"
          clearable
          @update:model-value="updateField('parseId', $event)"
        />
      </div>
      <div class="filter-field">
        <label class="filter-label">文档 ID (documentFileId)</label>
        <el-input
          :model-value="props.filters.documentFileId"
          placeholder="Guid"
          clearable
          @update:model-value="updateField('documentFileId', $event)"
        />
      </div>
      <div class="filter-field">
        <label class="filter-label">仅含图片块 (hasImage)</label>
        <el-switch
          :model-value="props.filters.hasImage"
          @update:model-value="updateField('hasImage', $event as boolean)"
        />
      </div>
    </div>
    <template #foot>
      <button class="btn btn-ghost" @click="emit('clear')">清空筛选</button>
      <button class="btn" @click="emit('apply')">应用筛选</button>
    </template>
  </AppDrawer>
</template>

<style scoped>
.filter-form {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.filter-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.filter-label {
  font-size: 12px;
  color: var(--text-2);
  font-weight: 500;
}
</style>

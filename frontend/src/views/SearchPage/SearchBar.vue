<script setup lang="ts">
import { iconHtml } from '../../utils/icons'

const props = defineProps<{
  query: string
  phrase: boolean
  loading: boolean
  hasResults: boolean
  hasMinerUFilter: boolean
}>()

const emit = defineEmits<{
  (e: 'update:query', value: string): void
  (e: 'update:phrase', value: boolean): void
  (e: 'search'): void
  (e: 'clear'): void
  (e: 'openFilters'): void
}>()
</script>

<template>
  <div class="search-bar">
    <div style="position: relative; flex: 1; min-width: 280px">
      <span
        v-html="iconHtml('search')"
        style="position: absolute; left: 12px; top: 11px; color: var(--text-3); width: 16px; height: 16px"
      ></span>
      <input
        :value="props.query"
        class="input"
        style="padding-left: 38px; padding-right: 38px"
        placeholder="输入单词或短语进行检索…"
        @input="emit('update:query', ($event.target as HTMLInputElement).value)"
        @keyup.enter="emit('search')"
      />
      <button
        v-if="props.query"
        class="clear-btn"
        @click="emit('update:query', '')"
      >
        <span v-html="iconHtml('x')"></span>
      </button>
    </div>
    <label class="phrase-toggle">
      <input
        type="checkbox"
        :checked="props.phrase"
        @change="emit('update:phrase', ($event.target as HTMLInputElement).checked)"
      />
      <span>短语查询</span>
    </label>
    <button
      class="btn"
      :disabled="!props.query.trim() || props.loading"
      @click="emit('search')"
    >
      <span v-html="iconHtml('search')"></span>{{ props.loading ? '检索中…' : '搜索' }}
    </button>
    <button class="btn btn-ghost" @click="emit('openFilters')">
      <span v-html="iconHtml('shield')"></span>高级筛选
      <i v-if="props.hasMinerUFilter" class="filter-active-dot"></i>
    </button>
    <button v-if="props.hasResults" class="btn btn-ghost btn-sm" @click="emit('clear')">清空</button>
  </div>
</template>

<style scoped>
.search-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-top: 12px;
}

.clear-btn {
  position: absolute;
  right: 6px;
  top: 6px;
  width: 22px;
  height: 22px;
  border: none;
  background: var(--surface-2);
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  color: var(--text-3);
  padding: 0;
}

.clear-btn :deep(svg) {
  width: 12px;
  height: 12px;
}

.clear-btn:hover {
  background: var(--border-2);
  color: var(--text);
}

.phrase-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--text-2);
  cursor: pointer;
  user-select: none;
}

.phrase-toggle input {
  width: 14px;
  height: 14px;
  accent-color: var(--primary);
}

.filter-active-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--warning);
  margin-left: 4px;
  vertical-align: middle;
}
</style>

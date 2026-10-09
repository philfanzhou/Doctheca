<script setup lang="ts">
import { iconHtml } from '../../utils/icons'
import StatusStrip from '../../components/StatusStrip.vue'
import ParseResultsTable from './ParseResultsTable.vue'
import { useParseResults } from './useParseResults'

const {
  parseList,
  parseTotal,
  parsePage,
  parsePageSize,
  parseSearch,
  parseDeleting,
  listLoading,
  filterStatus,
  stripItems,
  loadParseList,
  handleParsePageChange,
  handleParsePageSizeChange,
  handleDeleteParse,
  selectStrip,
} = useParseResults()
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">解析结果</div>
        <div class="page-sub">查看所有文档的解析记录和结果</div>
      </div>
      <div class="page-actions">
        <button class="btn btn-ghost btn-sm" @click="loadParseList">
          <span v-html="iconHtml('refresh')"></span>刷新
        </button>
      </div>
    </div>

    <StatusStrip :items="stripItems" :active="filterStatus" @select="selectStrip" />

    <div class="card section-gap">
      <div class="card-head">
        <div>
          <div class="card-title">解析记录</div>
          <div class="card-sub">共 {{ parseTotal }} 条记录</div>
        </div>
        <div style="position: relative; width: 240px">
          <span
            v-html="iconHtml('search')"
            style="position: absolute; left: 10px; top: 9px; color: var(--text-3); width: 15px; height: 15px"
          ></span>
          <input
            v-model="parseSearch"
            class="input"
            style="padding-left: 32px"
            placeholder="搜索文档名…"
            @keyup.enter="parsePage = 1; loadParseList()"
          />
        </div>
      </div>

      <ParseResultsTable
        :rows="parseList"
        :deleting="parseDeleting"
        :loading="listLoading"
        @delete="handleDeleteParse"
      />

      <div v-if="parseTotal > 0" class="pager">
        <span class="total">共 {{ parseTotal }} 条记录</span>
        <el-pagination
          v-model:current-page="parsePage"
          v-model:page-size="parsePageSize"
          :total="parseTotal"
          :page-sizes="[20, 50, 100]"
          layout="sizes, prev, pager, next, jumper"
          background
          @current-change="handleParsePageChange"
          @size-change="handleParsePageSizeChange"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.page-actions :deep(svg),
.btn :deep(svg) {
  width: 14px;
  height: 14px;
  vertical-align: middle;
}
</style>

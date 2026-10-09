<script setup lang="ts">
import { iconHtml } from '../../utils/icons'
import StatusStrip from '../../components/StatusStrip.vue'
import FileUploadZone from './FileUploadZone.vue'
import FileListTable from './FileListTable.vue'
import { useDocManage } from './useDocManage'

const {
  fileList,
  fileTotal,
  filePage,
  filePageSize,
  fileNameSearch,
  fileUploading,
  fileUploadProgress,
  parseFileParsing,
  listLoading,
  filterStatus,
  stripItems,
  loadFileList,
  handlePageChange,
  handlePageSizeChange,
  handleFileUploadChange,
  handleParseFile,
  handleDeleteFile,
  selectStrip,
  onRowClick,
  handleDrop,
} = useDocManage()
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">文档管理</div>
        <div class="page-sub">上传原始文档并启动解析</div>
      </div>
    </div>

    <StatusStrip :items="stripItems" :active="filterStatus" @select="selectStrip" />

    <FileUploadZone
      :uploading="fileUploading"
      :progress="fileUploadProgress"
      @change="handleFileUploadChange"
      @drop="handleDrop"
    />

    <div class="card section-gap">
      <div class="card-head">
        <div>
          <div class="card-title">文档列表</div>
          <div class="card-sub">共 {{ fileTotal }} 份文档</div>
        </div>
        <div style="display: flex; gap: 8px; align-items: center; flex-wrap: wrap">
          <div style="position: relative; width: 220px">
            <span
              v-html="iconHtml('search')"
              style="position: absolute; left: 10px; top: 9px; color: var(--text-3); width: 15px; height: 15px"
            ></span>
            <input
              v-model="fileNameSearch"
              class="input"
              style="padding-left: 32px"
              placeholder="搜索文件名…"
              @keyup.enter="filePage = 1; loadFileList()"
            />
          </div>
          <button class="btn btn-ghost btn-sm" @click="filePage = 1; loadFileList()">搜索</button>
        </div>
      </div>

      <FileListTable
        :files="fileList"
        :parsing="parseFileParsing"
        :loading="listLoading"
        @row-click="onRowClick"
        @parse="handleParseFile"
        @delete="handleDeleteFile"
      />

      <div v-if="fileTotal > 0" class="pager">
        <span class="total">共 {{ fileTotal }} 份文档</span>
        <el-pagination
          v-model:current-page="filePage"
          v-model:page-size="filePageSize"
          :total="fileTotal"
          :page-sizes="[20, 50, 100]"
          layout="sizes, prev, pager, next, jumper"
          background
          @current-change="handlePageChange"
          @size-change="handlePageSizeChange"
        />
      </div>
    </div>
  </div>
</template>

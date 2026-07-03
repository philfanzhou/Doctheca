<script setup lang="ts">
import { ref, computed, onMounted, inject, type Ref } from 'vue'
import { createDocApiClient, type DocumentFileDetail } from '../services/docApi'

const client = createDocApiClient()

const selectedFileIdForLayout = inject<Ref<string | null>>('selectedFileIdForLayout', ref(null))
const viewLayoutPdf = inject<(fileId: string) => void>('viewLayoutPdf', () => {})

const fileId = computed(() => selectedFileIdForLayout.value)
const fileDetail = ref<DocumentFileDetail | null>(null)
const loading = ref(true)
const error = ref('')

async function loadFileDetail() {
  if (!fileId.value) {
    error.value = 'Missing fileId parameter'
    loading.value = false
    return
  }
  try {
    loading.value = true
    const response = await client.getDocumentFile(fileId.value)
    fileDetail.value = response.data
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to load file details'
  } finally {
    loading.value = false
  }
}

function goBack() {
  viewLayoutPdf('')
}

onMounted(() => {
  loadFileDetail()
})
</script>

<template>
  <div class="page-header">
    <div style="display: flex; align-items: center; gap: 12px">
      <button class="btn btn-secondary btn-small" @click="goBack">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6" /></svg>
        Back
      </button>
      <div>
        <h1 class="page-title" style="margin-bottom: 0">Layout PDF</h1>
        <p class="page-subtitle" v-if="fileDetail">{{ fileDetail.fileName }}</p>
      </div>
    </div>
  </div>

  <div class="card">
    <div class="card-body">
      <div v-if="loading" style="text-align: center; padding: 40px; color: var(--text-muted)">Loading...</div>
      <div v-else-if="error" style="text-align: center; padding: 40px; color: var(--danger)">{{ error }}</div>
      <div v-else-if="!fileDetail?.parse" style="text-align: center; padding: 40px; color: var(--text-muted)">
        This file has not been parsed yet. Please parse it first.
      </div>
      <div v-else-if="!fileDetail.parse.layoutPdfUrl" style="text-align: center; padding: 40px; color: var(--text-muted)">
        Layout PDF is not available for this file. It may have been parsed before the layout PDF feature was introduced.
      </div>
      <div v-else style="height: calc(100vh - 200px); min-height: 600px">
        <iframe
          :src="fileDetail.parse.layoutPdfUrl"
          style="width: 100%; height: 100%; border: 1px solid var(--border-color); border-radius: 6px"
        ></iframe>
      </div>
    </div>
  </div>
</template>

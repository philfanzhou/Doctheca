import { ref, onMounted, onUnmounted, watch, inject } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentFileDetail } from '../../services/docApi'
import { useToast } from '../../composables/useToast'

const client = createDocApiClient()

export function useDocumentDetail(docId: string | null | undefined) {
  const { success: toastSuccess, error: toastError } = useToast()
  const backToDocs = inject<() => void>('backToDocs')

  const detail = ref<DocumentFileDetail | null>(null)
  const loading = ref(false)
  const parsing = ref(false)
  const deleting = ref<string | null>(null)
  const pollTimer = ref<number | null>(null)

  async function loadDetail() {
    if (!docId) return
    loading.value = true
    try {
      const resp = await client.getDocumentFile(docId)
      detail.value = resp.data
      const hasActive = detail.value?.parses.some(
        (p) => p.status === 'pending' || p.status === 'parsing'
      )
      if (hasActive) startPolling()
    } catch (e) {
      toastError(e instanceof Error ? e.message : '加载文档详情失败')
    } finally {
      loading.value = false
    }
  }

  function startPolling() {
    if (pollTimer.value !== null) return
    pollTimer.value = window.setInterval(async () => {
      if (!docId) return
      try {
        const resp = await client.getDocumentFile(docId)
        detail.value = resp.data
        const hasActive = detail.value?.parses.some(
          (p) => p.status === 'pending' || p.status === 'parsing'
        )
        if (!hasActive) stopPolling()
      } catch {
        stopPolling()
      }
    }, 5000)
  }

  function stopPolling() {
    if (pollTimer.value !== null) {
      clearInterval(pollTimer.value)
      pollTimer.value = null
    }
  }

  async function handleParse(modelVersion: string) {
    if (!docId) return
    parsing.value = true
    try {
      await client.parseDocumentFile(docId, modelVersion)
      await loadDetail()
      toastSuccess(`已触发 ${modelVersion === 'vlm' ? 'VLM' : 'Pipeline'} 解析`)
    } catch (e) {
      toastError(e instanceof Error ? e.message : '解析请求失败')
    } finally {
      parsing.value = false
    }
  }

  async function handleDeleteFile() {
    if (!docId) return
    try {
      await ElMessageBox.confirm('确定删除此文件？所有解析记录将一并删除。', '删除确认', {
        confirmButtonText: '删除',
        cancelButtonText: '取消',
        type: 'warning',
      })
    } catch {
      return
    }
    try {
      await client.deleteDocumentFile(docId)
      toastSuccess('删除成功')
      backToDocs?.()
    } catch (e) {
      toastError(e instanceof Error ? e.message : '删除失败')
    }
  }

  async function handleDeleteParse(parseId: string) {
    try {
      await ElMessageBox.confirm('确定删除此解析记录？原始文件不会被删除。', '删除确认', {
        confirmButtonText: '删除',
        cancelButtonText: '取消',
        type: 'warning',
      })
    } catch {
      return
    }
    deleting.value = parseId
    try {
      await client.deleteDocumentParse(parseId)
      await loadDetail()
      toastSuccess('删除成功')
    } catch (e) {
      toastError(e instanceof Error ? e.message : '删除失败')
    } finally {
      deleting.value = null
    }
  }

  watch(() => docId, () => {
    stopPolling()
    detail.value = null
    loadDetail()
  })

  onMounted(loadDetail)

  onUnmounted(() => {
    stopPolling()
  })

  return {
    detail,
    loading,
    parsing,
    deleting,
    loadDetail,
    handleParse,
    handleDeleteFile,
    handleDeleteParse,
  }
}

import { ref, computed, onMounted, onUnmounted, inject } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentFile } from '../../services/docApi'
import { useToast } from '../../composables/useToast'

const client = createDocApiClient()

export function useDocManage() {
  const { success: toastSuccess, error: toastError } = useToast()
  const openDocDetail = inject<(id: string) => void>('openDocDetail')

  const fileList = ref<DocumentFile[]>([])
  const fileTotal = ref(0)
  const filePage = ref(1)
  const filePageSize = ref(20)
  const fileNameSearch = ref('')
  const fileUploading = ref(false)
  const fileUploadProgress = ref(0)
  const parseFileParsing = ref(false)
  const pollTimer = ref<number | null>(null)
  const listLoading = ref(false)
  const filterStatus = ref<string>('all')

  async function loadFileList() {
    listLoading.value = true
    try {
      const response = await client.listDocumentFiles(
        filePage.value,
        filePageSize.value,
        filterStatus.value === 'all' ? undefined : filterStatus.value,
        fileNameSearch.value || undefined
      )
      fileList.value = response.data as DocumentFile[]
      fileTotal.value = response.total
    } catch (e) {
      toastError(e instanceof Error ? e.message : '加载文件列表失败')
    } finally {
      listLoading.value = false
    }
  }

  function handlePageChange(newPage: number) {
    filePage.value = newPage
    loadFileList()
  }

  function handlePageSizeChange(newSize: number) {
    filePageSize.value = newSize
    filePage.value = 1
    loadFileList()
  }

  async function handleFileUploadChange(event: Event) {
    const input = event.target as HTMLInputElement
    const file = input.files?.[0]
    if (!file) return

    fileUploading.value = true
    fileUploadProgress.value = 0

    try {
      await client.uploadDocumentFile(file, (progressEvent) => {
        if (progressEvent.total) {
          fileUploadProgress.value = Math.round((progressEvent.loaded / progressEvent.total) * 100)
        }
      })
      filePage.value = 1
      fileNameSearch.value = ''
      await loadFileList()
      toastSuccess('上传成功')
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : '上传失败'
      toastError(msg)
    } finally {
      fileUploading.value = false
      fileUploadProgress.value = 0
    }
    input.value = ''
  }

  async function handleParseFile(id: string, modelVersion: string) {
    parseFileParsing.value = true
    try {
      await client.parseDocumentFile(id, modelVersion)
      await loadFileList()
      startPolling()
      toastSuccess(`已触发 ${modelVersion === 'vlm' ? 'VLM' : 'Pipeline'} 解析`)
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : '解析请求失败'
      toastError(msg)
    } finally {
      parseFileParsing.value = false
    }
  }

  async function handleDeleteFile(id: string) {
    try {
      await ElMessageBox.confirm('确定删除此文件？', '删除确认', {
        confirmButtonText: '删除',
        cancelButtonText: '取消',
        type: 'warning',
      })
    } catch {
      return
    }
    try {
      await client.deleteDocumentFile(id)
      if (fileList.value.length <= 1 && filePage.value > 1) {
        filePage.value--
      }
      await loadFileList()
      toastSuccess('删除成功')
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : '删除失败'
      toastError(msg)
    }
  }

  function startPolling() {
    if (pollTimer.value !== null) return
    pollTimer.value = window.setInterval(async () => {
      await loadFileList()
      const hasActive = fileList.value.some(
        (f) => f.parseStatus === 'pending' || f.parseStatus === 'parsing'
      )
      if (!hasActive) {
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

  const stripItems = computed(() => {
    const all = fileTotal.value
    const unparsed = fileList.value.filter((f) => f.parseStatus === null || f.parseStatus === 'unparsed').length
    const parsing = fileList.value.filter((f) => f.parseStatus === 'parsing' || f.parseStatus === 'pending').length
    const failed = fileList.value.filter((f) => f.parseStatus === 'failed').length
    return [
      { key: 'all', label: '全部文档', count: all, color: '#4F46E5' },
      { key: 'unparsed', label: '待解析', count: unparsed, color: '#9AA0B6' },
      { key: 'parsing', label: '解析中', count: parsing, color: '#0EA5E9' },
      { key: 'failed', label: '解析失败', count: failed, color: '#EF4444' },
    ]
  })

  function selectStrip(key: string) {
    filterStatus.value = key
    filePage.value = 1
    loadFileList()
  }

  function onRowClick(f: DocumentFile) {
    openDocDetail?.(f.id)
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault()
    if (!e.dataTransfer?.files.length) return
    const input = document.createElement('input')
    input.type = 'file'
    const dt = new DataTransfer()
    dt.items.add(e.dataTransfer.files[0])
    input.files = dt.files
    handleFileUploadChange({ target: input } as unknown as Event)
  }

  onMounted(async () => {
    await loadFileList()
    const hasActive = fileList.value.some(
      (f) => f.parseStatus === 'pending' || f.parseStatus === 'parsing'
    )
    if (hasActive) startPolling()
  })

  onUnmounted(() => {
    stopPolling()
  })

  return {
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
  }
}

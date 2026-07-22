import { ref, computed, onMounted } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createDocApiClient, type DocumentParse } from '../../services/docApi'
import { useToast } from '../../composables/useToast'

const client = createDocApiClient()

export function useParseResults() {
  const { success: toastSuccess, error: toastError } = useToast()

  const parseList = ref<DocumentParse[]>([])
  const parseTotal = ref(0)
  const parsePage = ref(1)
  const parsePageSize = ref(20)
  const parseSearch = ref('')
  const parseDeleting = ref<string | null>(null)
  const listLoading = ref(false)
  const filterStatus = ref<string>('all')

  async function loadParseList() {
    listLoading.value = true
    try {
      const response = await client.listDocumentParses(
        parsePage.value,
        parsePageSize.value,
        parseSearch.value || undefined,
        filterStatus.value
      )
      parseList.value = response.data as DocumentParse[]
      parseTotal.value = response.total
    } catch (e) {
      toastError(e instanceof Error ? e.message : '加载解析记录失败')
    } finally {
      listLoading.value = false
    }
  }

  function handleParsePageChange(newPage: number) {
    parsePage.value = newPage
    loadParseList()
  }

  function handleParsePageSizeChange(newSize: number) {
    parsePageSize.value = newSize
    parsePage.value = 1
    loadParseList()
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
    parseDeleting.value = parseId
    try {
      await client.deleteDocumentParse(parseId)
      if (parseList.value.length <= 1 && parsePage.value > 1) {
        parsePage.value--
      }
      await loadParseList()
      toastSuccess('删除成功')
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : '删除失败'
      toastError(msg)
    } finally {
      parseDeleting.value = null
    }
  }

  const stripItems = computed(() => {
    const all = parseTotal.value
    const parsed = parseList.value.filter((p) => p.status === 'parsed').length
    const parsing = parseList.value.filter((p) => p.status === 'parsing' || p.status === 'pending').length
    const failed = parseList.value.filter((p) => p.status === 'failed').length
    return [
      { key: 'all', label: '全部记录', count: all, color: '#4F46E5' },
      { key: 'parsed', label: '已解析', count: parsed, color: '#10B981' },
      { key: 'parsing', label: '解析中', count: parsing, color: '#0EA5E9' },
      { key: 'failed', label: '失败', count: failed, color: '#EF4444' },
    ]
  })

  function selectStrip(key: string) {
    filterStatus.value = key
    parsePage.value = 1
    loadParseList()
  }

  onMounted(() => {
    loadParseList()
  })

  return {
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
  }
}

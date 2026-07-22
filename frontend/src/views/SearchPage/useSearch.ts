import { ref, computed } from 'vue'
import { createDocApiClient, type SearchResult } from '../../services/docApi'
import { useToast } from '../../composables/useToast'

const client = createDocApiClient()

export interface SearchFilters {
  blockType: string
  blockSubType: string
  pageNumber: number | undefined
  textLevel: number | undefined
  textFormat: string
  parseId: string
  documentFileId: string
  hasImage: boolean
}

export function useSearch() {
  const { error: toastError } = useToast()

  const searchQuery = ref('')
  const searchPhrase = ref(false)
  const searchLoading = ref(false)
  const searchResults = ref<SearchResult[]>([])
  const searchTotalCount = ref(0)
  const searchNextToken = ref('')
  const hasSearched = ref(false)
  const showAdvancedFilter = ref(false)

  const filters = ref<SearchFilters>({
    blockType: '',
    blockSubType: '',
    pageNumber: undefined,
    textLevel: undefined,
    textFormat: '',
    parseId: '',
    documentFileId: '',
    hasImage: false,
  })

  const hasMinerUFilter = computed(() => {
    return !!(
      filters.value.blockType ||
      filters.value.blockSubType ||
      filters.value.pageNumber !== undefined ||
      filters.value.textLevel !== undefined ||
      filters.value.textFormat ||
      filters.value.parseId ||
      filters.value.documentFileId ||
      filters.value.hasImage
    )
  })

  async function handleSearch() {
    if (!searchQuery.value.trim()) return
    searchLoading.value = true
    try {
      const response = await client.searchTest(
        searchQuery.value.trim(),
        searchPhrase.value,
        20,
        undefined,
        filters.value.blockType || undefined,
        filters.value.blockSubType || undefined,
        filters.value.pageNumber,
        filters.value.textLevel,
        filters.value.textFormat || undefined,
        filters.value.parseId || undefined,
        filters.value.documentFileId || undefined,
        filters.value.hasImage || undefined
      )
      searchResults.value = response.results
      searchTotalCount.value = response.totalCount
      searchNextToken.value = response.nextPageToken
      hasSearched.value = true
    } catch (error) {
      toastError(error instanceof Error ? error.message : '检索失败')
    } finally {
      searchLoading.value = false
    }
  }

  function clearSearch() {
    searchQuery.value = ''
    searchPhrase.value = false
    searchResults.value = []
    searchTotalCount.value = 0
    searchNextToken.value = ''
    hasSearched.value = false
  }

  function clearFilters() {
    filters.value = {
      blockType: '',
      blockSubType: '',
      pageNumber: undefined,
      textLevel: undefined,
      textFormat: '',
      parseId: '',
      documentFileId: '',
      hasImage: false,
    }
  }

  function applyFiltersAndSearch() {
    showAdvancedFilter.value = false
    handleSearch()
  }

  return {
    searchQuery,
    searchPhrase,
    searchLoading,
    searchResults,
    searchTotalCount,
    searchNextToken,
    hasSearched,
    showAdvancedFilter,
    filters,
    hasMinerUFilter,
    handleSearch,
    clearSearch,
    clearFilters,
    applyFiltersAndSearch,
  }
}

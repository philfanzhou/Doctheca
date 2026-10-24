<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { iconHtml } from '../../utils/icons'
import { useToast } from '../../composables/useToast'
import { getApiMessage, getParseStructure, getDocumentStructure, type StructureTree } from '../../services/structureApi'
import {
  getAudioFileErrors,
  getOwnerAudioPlaybackLink,
  previewAudio,
  commitAudio,
  listAudio,
  saveAudio,
  type AudioFiles,
  type AudioFileError,
  type AudioStorageInfo,
  type StructureAudio,
} from '../../services/audioApi'
import { formatBytes, isSystemFile, linkState, nodeOptions, savePayload } from '../../utils/audioLinks'

// One in-memory audio draft for a document current tree or a compatible real-parse entry.
// Preview proposes targets; one explicit Save commits new files and existing edits/removals.

const props = defineProps<{ docId?: string | null; documentMode?: boolean }>()
const navigate = inject<(page: string, id?: string | null) => void>('navigate')
const { success: toastSuccess, error: toastError } = useToast()

type Filter = 'all' | 'flagged' | 'linked'

const parseId = computed(() => props.docId ?? '')
const tree = ref<StructureTree | null>(null)
const items = ref<StructureAudio[]>([])
const draftItems = ref<StructureAudio[]>([])
const selectedFiles = ref<AudioFiles>([])
const commonTarget = ref<string | null>()
let draftAbort: AbortController | null = null
const allItems = computed(() => [...items.value, ...draftItems.value])
const newIds = computed(() => new Set(draftItems.value.map(item => item.id)))
const storage = ref<AudioStorageInfo | null>(null)
const edited = ref<Record<string, string | null>>({})
const removed = ref<string[]>([])
const filter = ref<Filter>('all')
const loading = ref(false)
const saving = ref(false)
const uploading = ref(false)
const progress = ref(0)
const importErrors = ref<AudioFileError[]>([])
const folderInput = ref<HTMLInputElement | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

const player = ref<HTMLAudioElement | null>(null)
const playingItem = ref<StructureAudio | null>(null)
const playbackError = ref('')
const playbackLoading = ref(false)
let playbackRequest = 0
let playbackAbort: AbortController | null = null
let playbackExpiry = 0

function stopPlayback() {
  playbackRequest++
  playbackAbort?.abort()
  playbackAbort = null
  if (player.value) {
    player.value.pause()
    player.value.removeAttribute('src')
    player.value.load()
  }
  playingItem.value = null
  playbackLoading.value = false
  playbackExpiry = 0
}

function mediaFailed() {
  if (!playingItem.value || playbackLoading.value) return
  const expired = playbackExpiry > 0 && Date.now() >= playbackExpiry
  stopPlayback()
  playbackError.value = expired ? '播放链接已过期，请重新点击播放。' : '音频无法播放，请检查网络或文件格式后重试。'
}

async function play(item: StructureAudio) {
  if (saving.value || removedSet.value.has(item.id)) return
  stopPlayback()
  playbackError.value = ''
  playingItem.value = item
  playbackLoading.value = true
  const request = playbackRequest
  const controller = new AbortController()
  playbackAbort = controller
  try {
    const response = await getOwnerAudioPlaybackLink(parseId.value, !!props.documentMode, item.id, controller.signal)
    if (request !== playbackRequest) return
    playbackExpiry = Date.parse(response.data.expiresAt)
    if (!Number.isFinite(playbackExpiry) || Date.now() >= playbackExpiry) {
      stopPlayback()
      playbackError.value = '播放链接已过期，请重新点击播放。'
      return
    }
    await nextTick()
    if (request !== playbackRequest || !player.value) return
    player.value.src = response.data.url
    playbackLoading.value = false
    await player.value.play()
  } catch (error) {
    if (request !== playbackRequest) return
    const status = (error as { response?: { status?: number } })?.response?.status
    stopPlayback()
    playbackError.value = status === 404 ? '音频已不存在，请重新加载列表。'
      : status === 503 ? '音频播放暂时不可用，请稍后重试。'
      : '无法播放音频，请检查网络或文件格式后重试。'
  }
}

const options = computed(() => nodeOptions(tree.value?.nodes ?? []))
const policyText = computed(() => tree.value?.definition?.levels.map(level => `${level.name}：${level.required ? '必需' : '可选'}`).join('；') ?? '')
const hasNodes = computed(() => options.value.length > 0)
const removedSet = computed(() => new Set(removed.value))
const payload = computed(() => savePayload(items.value, edited.value, removedSet.value))
const dirty = computed(() => draftItems.value.length > 0 || payload.value.links.length > 0 || payload.value.removeIds.length > 0)
const flaggedCount = computed(() => allItems.value.filter((item) => linkState(item).flagged || item.requiresReview).length)
const linkedCount = computed(() => allItems.value.filter((item) => edited.value[item.id]).length)
const visible = computed(() =>
  allItems.value.filter((item) => {
    if (filter.value === 'flagged') return linkState(item).flagged || item.requiresReview
    if (filter.value === 'linked') return !!edited.value[item.id]
    return true
  })
)
const limitsText = computed(() => {
  const info = storage.value
  if (!info) return ''
  return `支持 ${info.extensions.join(' / ')} 与 ZIP；单个文件不超过 ${formatBytes(info.maxFileBytes)}，单次导入不超过 ${formatBytes(info.maxArchiveBytes)}，最多 ${info.maxFiles} 个文件。`
})

function applyItems(list: StructureAudio[], keepEdits: boolean) {
  const previous = keepEdits ? edited.value : {}
  items.value = list
  edited.value = Object.fromEntries(list.map((item) => [item.id, item.id in previous ? previous[item.id] : item.nodeId]))
  removed.value = keepEdits ? removed.value.filter((id) => list.some((item) => item.id === id)) : []
}

let loadVersion = 0
async function load() {
  const version = ++loadVersion
  if (!parseId.value) return
  stopPlayback()
  playbackError.value = ''
  loading.value = true
  importErrors.value = []
  try {
    const [structure, audio] = await Promise.all([props.documentMode ? getDocumentStructure(parseId.value) : getParseStructure(parseId.value), listAudio(parseId.value, !!props.documentMode)])
    if (version !== loadVersion) return
    tree.value = structure.data
    storage.value = audio.data.storage
    clearPicked()
    applyItems(audio.data.items, false)
  } catch (e) {
    if (version === loadVersion) toastError(getApiMessage(e, '加载官方音频失败'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function reload() {
  if (saving.value || uploading.value || loading.value) return
  if (dirty.value && !(await confirm('放弃未保存的修改并重新加载？'))) return
  await load()
}

async function confirm(message: string): Promise<boolean> {
  try {
    await ElMessageBox.confirm(message, '确认', { confirmButtonText: '确定', cancelButtonText: '取消', type: 'warning' })
    return true
  } catch {
    return false
  }
}

function pick(kind: 'folder' | 'files') {
  ;(kind === 'folder' ? folderInput : fileInput).value?.click()
}

async function onPicked(event: Event) {
  if (saving.value || uploading.value || loading.value) return
  const input = event.target as HTMLInputElement
  const picked = Array.from(input.files ?? [])
    .map((file) => ({ file, path: file.webkitRelativePath || file.name }))
    .filter(({ path }) => !isSystemFile(path))
  input.value = ''
  if (!picked.length) {
    toastError('没有可导入的音频文件')
    return
  }
  uploading.value = true
  progress.value = 0
  importErrors.value = []
  const controller = new AbortController()
  draftAbort = controller
  try {
    const response = await previewAudio(parseId.value, !!props.documentMode, picked, controller.signal, (event) => {
      if (event.total) progress.value = Math.round((event.loaded / event.total) * 100)
    })
    if (controller.signal.aborted) return
    const existing = new Set(allItems.value.filter(item => !removedSet.value.has(item.id)).map(item => item.sourcePath))
    if (response.data.items.some(item => existing.has(item.sourcePath))) {
      toastError('所选文件包含已保存或已选择的路径，请先取消重复文件。')
      return
    }
    selectedFiles.value.push(...picked)
    for (const item of response.data.items) {
      const id = 'draft:' + item.sourcePath
      draftItems.value.push({ id, sourcePath: item.sourcePath, folder: item.sourcePath.includes('/') ? item.sourcePath.slice(0, item.sourcePath.lastIndexOf('/')) : '',
        fileName: item.sourcePath.slice(item.sourcePath.lastIndexOf('/') + 1), contentType: item.contentType, sizeBytes: item.sizeBytes,
        nodeId: item.suggestedNodeId, nodePath: item.suggestedNodePath, matchStatus: item.matchStatus, linkSource: null, createdAt: '', updatedAt: '' })
      edited.value[id] = commonTarget.value === undefined ? item.suggestedNodeId : commonTarget.value
    }
    toastSuccess(`已选择 ${response.data.items.length} 个文件，确认目标后点击保存`)
  } catch (e) {
    importErrors.value = getAudioFileErrors(e)
    if (!controller.signal.aborted) toastError(getApiMessage(e, '读取文件失败'))
  } finally {
    uploading.value = false
  }
}

function clearPicked() {
  draftAbort?.abort()
  for (const item of draftItems.value) delete edited.value[item.id]
  draftItems.value = []
  selectedFiles.value = []
}
function setCommonTarget(nodeId: string | null) {
  commonTarget.value = nodeId
  for (const item of draftItems.value) edited.value[item.id] = nodeId
}

function updateNode(id: string, nodeId: string | null) {
  if (saving.value) return
  edited.value[id] = nodeId
}

function toggleRemove(item: StructureAudio) {
  if (saving.value) return
  if (playingItem.value?.id === item.id) stopPlayback()
  removed.value = removedSet.value.has(item.id)
    ? removed.value.filter((id) => id !== item.id)
    : [...removed.value, item.id]
}

async function save() {
  if (!dirty.value || saving.value || uploading.value || loading.value) return
  saving.value = true
  stopPlayback()
  try {
    if (payload.value.removeIds.length && !(await confirm(`删除 ${payload.value.removeIds.length} 个音频文件？删除后需重新导入。`))) return
    importErrors.value = []
    draftAbort = new AbortController()
    if (draftItems.value.length) {
      await commitAudio(parseId.value, !!props.documentMode, selectedFiles.value,
        draftItems.value.map(item => ({ sourcePath: item.sourcePath, nodeId: edited.value[item.id] ?? null })),
        payload.value.links, payload.value.removeIds, draftAbort.signal, event => { if (event.total) progress.value = Math.round(event.loaded / event.total * 100) })
      // The single commit also changed old rows; reload the complete committed set.
      const response = await listAudio(parseId.value, !!props.documentMode)
      clearPicked()
      applyItems(response.data.items, false)
    } else {
      const response = await saveAudio(parseId.value, !!props.documentMode, payload.value.links, payload.value.removeIds, draftAbort.signal)
      applyItems(response.data.items, false)
    }
    toastSuccess('音频关联已保存')
  } catch (e) {
    importErrors.value = getAudioFileErrors(e)
    toastError(getApiMessage(e, '保存失败'))
  } finally {
    saving.value = false
  }
}

function back() {
  navigate?.(props.documentMode ? 'document-structure' : 'structure', parseId.value)
}

function beforeUnload(event: BeforeUnloadEvent) {
  if (dirty.value || uploading.value || saving.value) { event.preventDefault(); event.returnValue = '' }
}

const setNavigationGuard = inject<(guard: (() => Promise<boolean>) | null) => void>('setNavigationGuard')
async function canLeave(): Promise<boolean> {
  if (uploading.value || saving.value) return false
  return !dirty.value || await confirm('音频有未保存的修改，离开后将丢失。继续？')
}
watch([parseId, () => props.documentMode], load)

onMounted(() => {
  setNavigationGuard?.(canLeave)
  window.addEventListener('beforeunload', beforeUnload)
  void load()
})

onBeforeUnmount(() => {
  loadVersion++
  draftAbort?.abort()
  stopPlayback()
  setNavigationGuard?.(null)
  window.removeEventListener('beforeunload', beforeUnload)
})
</script>

<template>
  <div class="audio-page" v-loading="loading">
    <button class="back-btn" @click="back">
      <span v-html="iconHtml('back')"></span>
      返回结构标注
    </button>

    <div class="page-head">
      <div>
        <div class="page-title">官方音频</div>
        <div class="page-sub">
          {{ tree?.fileName }} · {{ allItems.length }} 个文件 · 已关联 {{ linkedCount }} · 待处理 {{ flaggedCount }}
        </div>
      </div>
      <div class="page-actions">
        <span v-if="dirty" class="badge amber"><span class="dot"></span>未保存</span>
        <button class="btn btn-ghost btn-sm" @click="reload">
          <span v-html="iconHtml('refresh')"></span>重新加载
        </button>
        <button class="btn btn-sm" :disabled="saving || uploading || loading || !dirty" @click="save">
          <span v-html="iconHtml('check')"></span>保存
        </button>
      </div>
    </div>

    <div v-if="storage && !storage.available" class="error-box audio-warning" role="status">
      <span v-html="iconHtml('warnTri')"></span>
      <span>音频存储未配置（{{ storage.reason }}）。导入不可用，已有文件的关联仍可查看和调整。</span>
    </div>
    <div v-if="tree && !hasNodes" class="error-box audio-warning" role="status">
      <span v-html="iconHtml('warnTri')"></span>
      <span>这本书还没有保存章节。请先在“结构标注”中添加章节，再选择音频。</span>
    </div>

    <div class="card audio-import">
      <div class="card-title">导入音频</div>
      <p class="td-sub">
        选择整个文件夹、多个文件或 ZIP 压缩包。系统按文件夹名和文件名建议章节；请在下表检查或修改每个目标。选文件不会保存，点击“保存”才一次提交全部修改。{{ limitsText }}
      </p>
      <p v-if="policyText" class="td-sub" data-testid="audio-policy">{{ policyText }}。必需层级改变后，已有文件会保留；保存时须修正或删除全部待复核文件。</p>
      <el-select :model-value="commonTarget" @update:model-value="setCommonTarget" clearable :value-on-clear="null" placeholder="统一指定新文件目标（可逐项修改）" aria-label="新文件统一目标" :disabled="saving || uploading" style="width: 100%">
        <el-option v-for="option in options" :key="option.id" :label="option.label" :value="option.id" />
      </el-select>
      <div class="audio-import-actions">
        <button class="btn btn-sm" :disabled="saving || uploading || loading || !hasNodes || !storage?.available" @click="pick('folder')">
          <span v-html="iconHtml('upload')"></span>选择文件夹
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="saving || uploading || loading || !hasNodes || !storage?.available" @click="pick('files')">
          <span v-html="iconHtml('file')"></span>选择文件或 ZIP
        </button>
        <span v-if="uploading || saving" class="td-sub mono">处理中 {{ progress }}%</span>
        <button v-if="draftItems.length || uploading" class="btn btn-ghost btn-sm" :disabled="saving" @click="clearPicked">取消所选文件</button>
        <input ref="folderInput" type="file" webkitdirectory multiple hidden data-testid="audio-folder-input" @change="onPicked" />
        <input
          ref="fileInput"
          type="file"
          multiple
          hidden
          accept=".mp3,.m4a,.wav,.ogg,.flac,.zip"
          data-testid="audio-file-input"
          @change="onPicked"
        />
      </div>
      <div v-if="importErrors.length" class="error-box audio-errors" role="alert">
        <div>以下文件需要处理，本次没有提交修改：</div>
        <div v-for="(error, i) in importErrors" :key="i">{{ error.file || '导入' }}：{{ error.reason }}</div>
      </div>
    </div>

    <div v-if="playingItem" class="card audio-player" role="region" aria-label="音频播放器">
      <div>{{ playingItem.fileName }}</div>
      <span v-if="playbackLoading" role="status">正在获取播放链接…</span>
      <audio ref="player" controls preload="none" @error="mediaFailed"></audio>
      <button class="btn btn-ghost btn-sm" @click="stopPlayback">关闭播放</button>
    </div>
    <div v-if="playbackError" class="error-box" role="alert">{{ playbackError }}</div>

    <div class="card">
      <div class="audio-filter" role="tablist" aria-label="筛选">
        <button class="btn btn-sm" :class="filter === 'all' ? '' : 'btn-ghost'" role="tab" :aria-selected="filter === 'all'" @click="filter = 'all'">
          全部 {{ allItems.length }}
        </button>
        <button class="btn btn-sm" :class="filter === 'flagged' ? '' : 'btn-ghost'" role="tab" :aria-selected="filter === 'flagged'" @click="filter = 'flagged'">
          待处理 {{ flaggedCount }}
        </button>
        <button class="btn btn-sm" :class="filter === 'linked' ? '' : 'btn-ghost'" role="tab" :aria-selected="filter === 'linked'" @click="filter = 'linked'">
          已关联 {{ linkedCount }}
        </button>
      </div>

      <div class="table-wrap">
        <table class="data-table audio-table">
          <thead>
            <tr>
              <th>文件</th>
              <th>关联节点</th>
              <th>状态</th>
              <th style="text-align: right">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in visible" :key="item.id" :class="{ removed: removedSet.has(item.id) }" :data-audio-path="item.sourcePath">
              <td>
                <div class="audio-name">{{ item.fileName }}</div>
                <div class="td-sub mono">{{ item.folder || '（根目录）' }} · {{ formatBytes(item.sizeBytes) }}</div>
              </td>
              <td class="audio-node">
                <el-select
                  :model-value="edited[item.id]"
                  @update:model-value="updateNode(item.id, $event)"
                  filterable
                  clearable
                  placeholder="未关联"
                  :aria-label="`关联节点 ${item.sourcePath}`"
                  :disabled="saving || removedSet.has(item.id)"
                  :value-on-clear="null"
                  style="width: 100%"
                >
                  <el-option v-for="option in options" :key="option.id" :label="option.label" :value="option.id">
                    <span :style="{ paddingLeft: option.depth * 14 + 'px' }">{{ option.label }}</span>
                  </el-option>
                </el-select>
              </td>
              <td>
                <span class="badge" :class="linkState(item).tone"><span class="dot"></span>{{ newIds.has(item.id) && item.nodeId ? '建议章节' : linkState(item).label }}</span>
                <span v-if="newIds.has(item.id)" class="badge amber audio-pending">待保存新文件</span>
                <span v-if="item.requiresReview" class="badge amber">需要复核：{{ item.reviewReason }}</span>
                <span v-if="removedSet.has(item.id)" class="badge red audio-pending">待删除</span>
                <span v-else-if="edited[item.id] !== item.nodeId" class="badge amber audio-pending">未保存</span>
              </td>
              <td class="audio-actions">
                <button class="btn btn-ghost btn-sm" :disabled="saving || newIds.has(item.id) || removedSet.has(item.id)" :aria-label="`播放 ${item.sourcePath}`" @click="play(item)">播放</button>
                <button
                  v-if="!newIds.has(item.id)"
                  class="btn btn-sm"
                  :class="removedSet.has(item.id) ? 'btn-ghost' : 'btn-danger'"
                  :aria-label="`${removedSet.has(item.id) ? '撤销删除' : '删除'} ${item.sourcePath}`"
                  :disabled="saving"
                  @click="toggleRemove(item)"
                >
                  <span v-html="iconHtml(removedSet.has(item.id) ? 'refresh' : 'trash')"></span>{{ removedSet.has(item.id) ? '撤销' : '删除' }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
        <div v-if="!visible.length && !loading" class="empty">
          {{ allItems.length ? '没有符合筛选条件的文件' : '还没有导入音频' }}
        </div>
      </div>
    </div>

  </div>
</template>

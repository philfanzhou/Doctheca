<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { iconHtml } from '../../utils/icons'
import { useToast } from '../../composables/useToast'
import { getApiMessage, getParseStructure, type StructureTree } from '../../services/structureApi'
import {
  getAudioFileErrors,
  getAudioPlaybackLink,
  importParseAudio,
  listParseAudio,
  saveParseAudio,
  type AudioFileError,
  type AudioStorageInfo,
  type StructureAudio,
} from '../../services/audioApi'
import { formatBytes, isSystemFile, linkState, nodeOptions, savePayload } from '../../utils/audioLinks'

// Official audio of one parse (issue #90): import a folder, files or a ZIP, review the
// pre-filled node of every file, change or clear links, remove files, and save.

const props = defineProps<{ docId?: string | null }>()
const navigate = inject<(page: string, id?: string | null) => void>('navigate')
const { success: toastSuccess, error: toastError } = useToast()

type Filter = 'all' | 'flagged' | 'linked'

const parseId = computed(() => props.docId ?? '')
const tree = ref<StructureTree | null>(null)
const items = ref<StructureAudio[]>([])
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
    const response = await getAudioPlaybackLink(parseId.value, item.id, controller.signal)
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
const hasNodes = computed(() => options.value.length > 0)
const removedSet = computed(() => new Set(removed.value))
const payload = computed(() => savePayload(items.value, edited.value, removedSet.value))
const dirty = computed(() => payload.value.links.length > 0 || payload.value.removeIds.length > 0)
const flaggedCount = computed(() => items.value.filter((item) => linkState(item).flagged).length)
const linkedCount = computed(() => items.value.filter((item) => item.nodeId).length)
const visible = computed(() =>
  items.value.filter((item) => {
    if (filter.value === 'flagged') return linkState(item).flagged
    if (filter.value === 'linked') return !!item.nodeId
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

async function load() {
  if (!parseId.value) return
  stopPlayback()
  playbackError.value = ''
  loading.value = true
  importErrors.value = []
  try {
    const [structure, audio] = await Promise.all([getParseStructure(parseId.value), listParseAudio(parseId.value)])
    tree.value = structure.data
    storage.value = audio.data.storage
    applyItems(audio.data.items, false)
  } catch (e) {
    toastError(getApiMessage(e, '加载官方音频失败'))
  } finally {
    loading.value = false
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
  try {
    const response = await importParseAudio(parseId.value, picked, (event) => {
      if (event.total) progress.value = Math.round((event.loaded / event.total) * 100)
    })
    const result = response.data
    const known = new Set(items.value.map((item) => item.id))
    const merged = [...items.value, ...result.items.filter((item) => !known.has(item.id))]
    merged.sort((a, b) => (a.sourcePath < b.sourcePath ? -1 : a.sourcePath > b.sourcePath ? 1 : 0))
    applyItems(merged, true)
    toastSuccess(`已导入 ${result.imported} 个文件：${result.linked} 个已自动关联，${result.flagged} 个待处理`)
    if (result.flagged > 0) filter.value = 'flagged'
  } catch (e) {
    importErrors.value = getAudioFileErrors(e)
    toastError(getApiMessage(e, '导入失败'))
  } finally {
    uploading.value = false
  }
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
    const response = await saveParseAudio(parseId.value, payload.value.links, payload.value.removeIds)
    applyItems(response.data.items, false)
    toastSuccess('音频关联已保存')
  } catch (e) {
    toastError(getApiMessage(e, '保存失败'))
  } finally {
    saving.value = false
  }
}

function back() {
  navigate?.('structure', parseId.value)
}

function beforeUnload(event: BeforeUnloadEvent) {
  if (dirty.value || uploading.value || saving.value) { event.preventDefault(); event.returnValue = '' }
}

const setNavigationGuard = inject<(guard: (() => Promise<boolean>) | null) => void>('setNavigationGuard')
async function canLeave(): Promise<boolean> {
  if (uploading.value || saving.value) return false
  return !dirty.value || await confirm('音频有未保存的修改，离开后将丢失。继续？')
}
watch(parseId, load)

onMounted(() => {
  setNavigationGuard?.(canLeave)
  window.addEventListener('beforeunload', beforeUnload)
  void load()
})

onBeforeUnmount(() => {
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
          {{ tree?.fileName }} · {{ items.length }} 个文件 · 已关联 {{ linkedCount }} · 待处理 {{ flaggedCount }}
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
      <span>该解析还没有标注结构节点。请先在“结构标注”中标注节点，再导入音频。</span>
    </div>

    <div class="card audio-import">
      <div class="card-title">导入音频</div>
      <p class="td-sub">
        选择整个文件夹、多个文件或 ZIP 压缩包。系统会按文件夹名和文件名匹配结构节点并自动关联到最深的节点；文件夹与文件名不一致、匹配不唯一或没有匹配的文件会标记为待处理。{{ limitsText }}
      </p>
      <div class="audio-import-actions">
        <button class="btn btn-sm" :disabled="saving || uploading || loading || !hasNodes || !storage?.available" @click="pick('folder')">
          <span v-html="iconHtml('upload')"></span>选择文件夹
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="saving || uploading || loading || !hasNodes || !storage?.available" @click="pick('files')">
          <span v-html="iconHtml('file')"></span>选择文件或 ZIP
        </button>
        <span v-if="uploading" class="td-sub mono">上传中 {{ progress }}%</span>
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
        <div>以下文件无法导入，本次没有保存任何文件：</div>
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
          全部 {{ items.length }}
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
                <span class="badge" :class="linkState(item).tone"><span class="dot"></span>{{ linkState(item).label }}</span>
                <span v-if="removedSet.has(item.id)" class="badge red audio-pending">待删除</span>
                <span v-else-if="edited[item.id] !== item.nodeId" class="badge amber audio-pending">未保存</span>
              </td>
              <td class="audio-actions">
                <button class="btn btn-ghost btn-sm" :disabled="saving || removedSet.has(item.id)" :aria-label="`播放 ${item.sourcePath}`" @click="play(item)">播放</button>
                <button
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
          {{ items.length ? '没有符合筛选条件的文件' : '还没有导入音频' }}
        </div>
      </div>
    </div>

  </div>
</template>

<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { iconHtml } from '../../utils/icons'
import { useToast } from '../../composables/useToast'
import {
  getAllParseBlocks,
  getApiMessage,
  getParseStructure,
  getStructureFieldErrors,
  newId,
  reindexParse,
  saveParseStructure,
  type ParseBlock,
  type StructureFieldError,
  type StructureNode,
  type StructureTree,
} from '../../services/structureApi'
import {
  allowedLevels,
  countNodes,
  flatten,
  insertNode,
  removeNode,
  renameNode,
  setNodeRange,
  type TreeLevel,
  type TreeNode,
} from '../../utils/structureTree'
import { headingLevel, htmlToText, isTableBlock, suggestName, tableRows, type TableCell } from '../../utils/blockContent'

// Continuous reading view of one parse: select a block range (click, shift-click, or drag),
// mark it as a node of a configured level, edit names, and save the whole tree.

const props = defineProps<{ docId?: string | null }>()
const navigate = inject<(page: string, id?: string | null) => void>('navigate')
const { success: toastSuccess, error: toastError } = useToast()

interface RenderedBlock {
  block: ParseBlock
  kind: 'image' | 'table' | 'heading' | 'text'
  heading: number
  rows: TableCell[][] | null
  text: string
}

const parseId = computed(() => props.docId ?? '')
const meta = ref<StructureTree | null>(null)
const blocks = shallowRef<RenderedBlock[]>([])
const nodes = ref<TreeNode[]>([])
const savedSnapshot = ref('[]')
const loading = ref(false)
const saving = ref(false)
const reindexing = ref(false)
const serverErrors = ref<{ label: string; reason: string }[]>([])

const anchor = ref<number | null>(null)
const focus = ref<number | null>(null)
const dragging = ref(false)
const markLevelId = ref('')
const markName = ref('')
const activeNodeId = ref<string | null>(null)

const levels = computed<TreeLevel[]>(() =>
  (meta.value?.definition?.levels ?? []).map((level) => ({ id: level.id, name: level.name, parentLevelId: level.parentLevelId }))
)
const levelNames = computed(() => new Map(levels.value.map((level) => [level.id, level.name])))
const dirty = computed(() => JSON.stringify(nodes.value) !== savedSnapshot.value)
const flatNodes = computed(() => flatten(nodes.value))
const nodeCount = computed(() => countNodes(nodes.value))

const selection = computed(() => {
  if (anchor.value === null || focus.value === null) return null
  return { start: Math.min(anchor.value, focus.value), end: Math.max(anchor.value, focus.value) }
})
const selectionCount = computed(() => {
  const range = selection.value
  if (!range) return 0
  return blocks.value.filter((b) => b.block.sequence >= range.start && b.block.sequence <= range.end).length
})
const allowed = computed(() =>
  selection.value ? allowedLevels(nodes.value, levels.value, selection.value.start, selection.value.end) : []
)

// Per-block marking: depth of the deepest node covering it, and the nodes that start there.
const coverage = computed(() => {
  const depthBySequence = new Map<number, number>()
  const startsAt = new Map<number, { node: TreeNode; depth: number }[]>()
  for (const { node, depth } of flatNodes.value) {
    const list = startsAt.get(node.startSequence) ?? []
    list.push({ node, depth })
    startsAt.set(node.startSequence, list)
    for (const { block } of blocks.value) {
      if (block.sequence < node.startSequence || block.sequence > node.endSequence) continue
      depthBySequence.set(block.sequence, Math.max(depthBySequence.get(block.sequence) ?? -1, depth))
    }
  }
  return { depthBySequence, startsAt }
})

watch(allowed, (list) => {
  if (!list.some((level) => level.id === markLevelId.value)) markLevelId.value = list[0]?.id ?? ''
})

watch(
  () => selection.value?.start,
  (start) => {
    if (start === undefined) return
    const first = blocks.value.find((b) => b.block.sequence === start)?.block
    markName.value = first ? suggestName(first.content, first.contentFormat) : ''
  }
)

function render(block: ParseBlock): RenderedBlock {
  if (block.imageUrl) {
    return { block, kind: 'image', heading: 0, rows: null, text: htmlToText(block.content) }
  }
  if (isTableBlock(block.blockType, block.contentFormat)) {
    const rows = tableRows(block.content)
    return { block, kind: 'table', heading: 0, rows, text: rows ? '' : htmlToText(block.content) }
  }
  const text = (block.contentFormat ?? '').toLowerCase() === 'html' ? htmlToText(block.content) : (block.content ?? '')
  const heading = headingLevel(block.subType, block.blockType)
  return { block, kind: heading > 0 ? 'heading' : 'text', heading, rows: null, text }
}

function toTreeNodes(list: StructureNode[]): TreeNode[] {
  return list.map((node) => ({
    id: node.id,
    levelId: node.levelId,
    name: node.name,
    startSequence: node.startSequence,
    endSequence: node.endSequence,
    children: toTreeNodes(node.children),
  }))
}

function applyTree(tree: StructureTree) {
  meta.value = tree
  nodes.value = toTreeNodes(tree.nodes)
  savedSnapshot.value = JSON.stringify(nodes.value)
}

async function load() {
  if (!parseId.value) return
  loading.value = true
  serverErrors.value = []
  try {
    const [structure, allBlocks] = await Promise.all([getParseStructure(parseId.value), getAllParseBlocks(parseId.value)])
    applyTree(structure.data)
    blocks.value = allBlocks.map(render)
    clearSelection()
  } catch (e) {
    toastError(getApiMessage(e, '加载结构标注失败'))
  } finally {
    loading.value = false
  }
}

async function reload() {
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

function clearSelection() {
  anchor.value = null
  focus.value = null
}

function onBlockMouseDown(event: MouseEvent, sequence: number) {
  if (event.button !== 0) return
  if (event.shiftKey && anchor.value !== null) {
    focus.value = sequence
    return
  }
  anchor.value = sequence
  focus.value = sequence
  dragging.value = true
}

function onBlockMouseEnter(sequence: number) {
  if (dragging.value) focus.value = sequence
}

function stopDragging() {
  dragging.value = false
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') clearSelection()
}

function isSelected(sequence: number): boolean {
  const range = selection.value
  return !!range && sequence >= range.start && sequence <= range.end
}

function mark() {
  const range = selection.value
  if (!range) return
  const result = insertNode(nodes.value, levels.value, {
    id: newId(),
    levelId: markLevelId.value,
    name: markName.value,
    startSequence: range.start,
    endSequence: range.end,
  })
  if (!result.ok) {
    toastError(result.reason)
    return
  }
  nodes.value = result.nodes
  clearSelection()
}

function rename(node: TreeNode, value: string) {
  nodes.value = renameNode(nodes.value, node.id, value)
}

function selectNodeRange(node: TreeNode) {
  anchor.value = node.startSequence
  focus.value = node.endSequence
  scrollToSequence(node.startSequence)
}

function applySelectionToNode(node: TreeNode) {
  const range = selection.value
  if (!range) {
    toastError('请先在正文中选择范围')
    return
  }
  const result = setNodeRange(nodes.value, node.id, range.start, range.end)
  if (!result.ok) {
    toastError(result.reason)
    return
  }
  nodes.value = result.nodes
  clearSelection()
}

async function deleteNode(node: TreeNode) {
  if (node.children.length && !(await confirm(`删除「${node.name}」及其 ${countNodes(node.children)} 个下级节点？`))) return
  nodes.value = removeNode(nodes.value, node.id)
}

function scrollToSequence(sequence: number) {
  activeNodeId.value = null
  document.querySelector(`[data-seq="${sequence}"]`)?.scrollIntoView({ block: 'start', behavior: 'smooth' })
}

function focusNode(node: TreeNode) {
  scrollToSequence(node.startSequence)
  activeNodeId.value = node.id
}

// Maps "nodes[0].children[1].endSequence" back to the node name for readable errors.
function describeErrors(errors: StructureFieldError[]): { label: string; reason: string }[] {
  return errors.map((error) => {
    let list: TreeNode[] = nodes.value
    let node: TreeNode | undefined
    const pattern = /(nodes|children)\[(\d+)\]/g
    let match: RegExpExecArray | null
    while ((match = pattern.exec(error.field)) !== null) {
      node = [...list].sort((a, b) => a.startSequence - b.startSequence)[Number(match[2])] ?? list[Number(match[2])]
      if (!node) break
      list = node.children
    }
    const field = error.field.split('.').pop() ?? error.field
    return { label: node ? `「${node.name}」${field}` : error.field, reason: error.reason }
  })
}

async function save() {
  saving.value = true
  serverErrors.value = []
  try {
    // Siblings are sent in start order so error paths map back to the same positions.
    const payload = JSON.parse(JSON.stringify(nodes.value)) as TreeNode[]
    const sortDeep = (list: TreeNode[]) => {
      list.sort((a, b) => a.startSequence - b.startSequence)
      list.forEach((node) => sortDeep(node.children))
    }
    sortDeep(payload)
    const response = await saveParseStructure(parseId.value, payload)
    applyTree(response.data)
    toastSuccess('结构已保存')
    if (!response.searchIndexUpdated) toastError('检索索引未能更新，可稍后点击“重建索引”')
  } catch (e) {
    const errors = getStructureFieldErrors(e)
    serverErrors.value = describeErrors(errors)
    toastError(errors.length ? '保存失败：结构校验未通过' : getApiMessage(e, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function reindex() {
  reindexing.value = true
  try {
    await reindexParse(parseId.value)
    toastSuccess('检索索引已更新')
  } catch (e) {
    toastError(getApiMessage(e, '重建索引失败'))
  } finally {
    reindexing.value = false
  }
}

function back() {
  if (meta.value) navigate?.('detail', meta.value.documentFileId)
  else navigate?.('results', null)
}

function beforeUnload(event: BeforeUnloadEvent) {
  if (dirty.value) event.preventDefault()
}

watch(parseId, load)

onMounted(() => {
  window.addEventListener('mouseup', stopDragging)
  window.addEventListener('keydown', onKeyDown)
  window.addEventListener('beforeunload', beforeUnload)
  void load()
})

onUnmounted(() => {
  window.removeEventListener('mouseup', stopDragging)
  window.removeEventListener('keydown', onKeyDown)
  window.removeEventListener('beforeunload', beforeUnload)
})
</script>

<template>
  <div class="structure-page" v-loading="loading">
    <button class="back-btn" @click="back">
      <span v-html="iconHtml('back')"></span>
      返回文档详情
    </button>

    <div class="page-head">
      <div>
        <div class="page-title">结构标注</div>
        <div class="page-sub">
          {{ meta?.fileName }}
          <template v-if="meta?.definition"> · 结构定义：{{ meta.definition.name }}</template>
          · {{ blocks.length }} 个内容块 · {{ nodeCount }} 个节点
        </div>
      </div>
      <div class="page-actions">
        <span v-if="dirty" class="badge amber"><span class="dot"></span>未保存</span>
        <button class="btn btn-ghost btn-sm" @click="reload">
          <span v-html="iconHtml('refresh')"></span>重新加载
        </button>
        <button class="btn btn-ghost btn-sm" :disabled="reindexing" @click="reindex">
          <span v-html="iconHtml('search')"></span>重建索引
        </button>
        <button class="btn btn-sm" :disabled="saving || !meta?.definition" @click="save">
          <span v-html="iconHtml('check')"></span>保存
        </button>
      </div>
    </div>

    <div v-if="meta && !meta.definition" class="error-box structure-warning">
      <span v-html="iconHtml('warnTri')"></span>
      <span>该文档尚未选择结构定义。请返回文档详情，在“文件信息”中选择结构定义后再标注。</span>
    </div>

    <div v-if="serverErrors.length" class="error-box structure-errors" role="alert">
      <div v-for="(error, i) in serverErrors" :key="i">{{ error.label }}：{{ error.reason }}</div>
    </div>

    <div class="structure-layout">
      <aside class="card structure-tree" aria-label="结构树">
        <div class="card-title">结构树</div>
        <div v-if="!flatNodes.length" class="td-sub tree-empty">在右侧正文中选择范围并标注节点</div>
        <div
          v-for="{ node, depth } in flatNodes"
          :key="node.id"
          class="tree-node"
          :class="[`depth-${Math.min(depth, 3)}`, { active: activeNodeId === node.id }]"
          :style="{ marginLeft: depth * 14 + 'px' }"
        >
          <div class="tree-node-head">
            <span class="badge indigo">{{ levelNames.get(node.levelId) ?? '?' }}</span>
            <button class="link-btn mono" :title="'定位到 #' + node.startSequence" @click="focusNode(node)">
              #{{ node.startSequence }}–#{{ node.endSequence }}
            </button>
          </div>
          <input
            class="input tree-name"
            :value="node.name"
            maxlength="500"
            :aria-label="`${levelNames.get(node.levelId) ?? ''} 名称`"
            @change="rename(node, ($event.target as HTMLInputElement).value)"
          />
          <div class="tree-actions">
            <button class="btn btn-ghost btn-sm" @click="selectNodeRange(node)">选中范围</button>
            <button class="btn btn-ghost btn-sm" :disabled="!selection" @click="applySelectionToNode(node)">用选区更新</button>
            <button class="btn btn-danger btn-sm" :aria-label="`删除 ${node.name}`" @click="deleteNode(node)">
              <span v-html="iconHtml('trash')"></span>
            </button>
          </div>
        </div>
      </aside>

      <section class="structure-reader">
        <div class="mark-bar card">
          <template v-if="selection">
            <span class="mark-range mono">已选 #{{ selection.start }}–#{{ selection.end }}（{{ selectionCount }} 块）</span>
            <el-select v-model="markLevelId" placeholder="层级" style="width: 150px" aria-label="标注层级" :disabled="!allowed.length">
              <el-option v-for="level in allowed" :key="level.id" :label="level.name" :value="level.id" />
            </el-select>
            <input v-model="markName" class="input mark-name" maxlength="500" placeholder="节点名称" aria-label="节点名称" @keyup.enter="mark" />
            <button class="btn btn-sm" :disabled="!allowed.length || !markLevelId" @click="mark">
              <span v-html="iconHtml('plus')"></span>标注
            </button>
            <button class="btn btn-ghost btn-sm" @click="clearSelection">取消选择</button>
            <span v-if="!allowed.length && meta?.definition" class="td-sub">此范围内没有可用的下级层级</span>
          </template>
          <span v-else class="td-sub">单击内容块选择起点，按住 Shift 单击选择终点，或按住鼠标拖动选择；Esc 取消选择</span>
        </div>

        <div class="block-list" :class="{ dragging }">
          <template v-for="item in blocks" :key="item.block.id">
            <div
              v-for="start in coverage.startsAt.get(item.block.sequence) ?? []"
              :key="start.node.id"
              class="node-start"
              :class="`depth-${Math.min(start.depth, 3)}`"
              :style="{ marginLeft: start.depth * 10 + 'px' }"
            >
              <span class="badge indigo">{{ levelNames.get(start.node.levelId) ?? '?' }}</span>
              {{ start.node.name }}
            </div>
            <div
              class="block"
              :data-seq="item.block.sequence"
              :class="[
                `kind-${item.kind}`,
                { selected: isSelected(item.block.sequence) },
                coverage.depthBySequence.has(item.block.sequence) ? `marked depth-${Math.min(coverage.depthBySequence.get(item.block.sequence)!, 3)}` : '',
              ]"
              @mousedown="onBlockMouseDown($event, item.block.sequence)"
              @mouseenter="onBlockMouseEnter(item.block.sequence)"
            >
              <span class="block-meta mono">#{{ item.block.sequence }} · P{{ item.block.pageNumber }} · {{ item.block.blockType }}</span>
              <figure v-if="item.kind === 'image'" class="block-image">
                <img :src="item.block.imageUrl!" loading="lazy" alt="" draggable="false" />
                <figcaption v-if="item.text">{{ item.text }}</figcaption>
              </figure>
              <table v-else-if="item.kind === 'table' && item.rows" class="block-table">
                <tbody>
                  <tr v-for="(row, r) in item.rows" :key="r">
                    <component
                      :is="cell.header ? 'th' : 'td'"
                      v-for="(cell, c) in row"
                      :key="c"
                      :rowspan="cell.rowSpan"
                      :colspan="cell.colSpan"
                    >{{ cell.text }}</component>
                  </tr>
                </tbody>
              </table>
              <div v-else-if="item.kind === 'heading'" class="block-heading" :class="`h${item.heading}`">{{ item.text }}</div>
              <div v-else class="block-text">{{ item.text }}</div>
            </div>
          </template>
          <div v-if="!blocks.length && !loading" class="empty">该解析没有可显示的内容块</div>
        </div>
      </section>
    </div>
  </div>
</template>

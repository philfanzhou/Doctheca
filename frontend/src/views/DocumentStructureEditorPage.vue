<script setup lang="ts">
import { ref, computed, inject, watch, onMounted, onUnmounted } from 'vue'
import { ElMessageBox } from 'element-plus'
import { getDocumentFile } from '../services/documentApi'
import type { DocumentFileDetail } from '../services/types'
import { getDocumentStructure, saveDocumentStructure, getAllParseBlocks, newId, getApiMessage, getStructureFieldErrors,
  type DocumentNodeInput, type StructureTree, type ParseBlock } from '../services/structureApi'
import { documentDraft, documentRows, moveSibling, clearBinding } from '../utils/documentTree'
import { useToast } from '../composables/useToast'
const props = defineProps<{ docId?: string | null }>()
const navigate = inject<(page: string, id: string) => void>('navigate')
const guard = inject<(fn: (() => Promise<boolean>) | null) => void>('setNavigationGuard')
const { success } = useToast()
const detail = ref<DocumentFileDetail | null>(null)
const tree = ref<StructureTree | null>(null)
const nodes = ref<DocumentNodeInput[]>([])
const saved = ref('[]')
const dirty = computed(() => JSON.stringify(nodes.value) !== saved.value)
const rows = computed(() => documentRows(nodes.value))
const parentId = ref('')
const levelId = ref('')
const name = ref('')
const selectedId = ref('')
const selected = computed(() => rows.value.find(r => r.node.id === selectedId.value)?.node)
const selectedParentLevel = computed(() => rows.value.find(r => r.node.id === parentId.value)?.node.levelId ?? null)
const levels = computed(() => tree.value?.definition?.levels.filter(l => l.parentLevelId === selectedParentLevel.value) ?? [])
const parseId = ref('')
const parses = computed(() => detail.value?.parses.filter(p => p.status === 'parsed') ?? [])
const blocks = ref<ParseBlock[]>([])
const start = ref<number | null>(null)
const end = ref<number | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const fields = ref<{ field: string; reason: string }[]>([])
watch(levels, value => { if (!value.some(l => l.id === levelId.value)) levelId.value = value[0]?.id ?? '' })
let blockRequest = 0
watch(parseId, async id => {
  const request = ++blockRequest; blocks.value = []; start.value = null; end.value = null
  if (!id) return
  try { const loaded = await getAllParseBlocks(id); if (request === blockRequest) blocks.value = loaded }
  catch (e) { error.value = getApiMessage(e, '加载正文失败') }
})
let loadRequest = 0
async function load() {
  if (!props.docId) return
  const request = ++loadRequest; const documentId = props.docId
  loading.value = true; error.value = ''
  try {
    const [book, result] = await Promise.all([getDocumentFile(documentId), getDocumentStructure(documentId)])
    if (request !== loadRequest) return
    detail.value = book.data; tree.value = result.data; nodes.value = documentDraft(result.data.nodes)
    saved.value = JSON.stringify(nodes.value); selectedId.value = ''; parentId.value = ''; parseId.value = ''
  } catch (e) { if (request === loadRequest) error.value = getApiMessage(e, '加载章节失败') }
  finally { if (request === loadRequest) loading.value = false }
}
function add() {
  if (!name.value.trim() || !levelId.value) return
  const node: DocumentNodeInput = { id: newId(), levelId: levelId.value, name: name.value.trim(), parseId: null,
    startSequence: null, endSequence: null, children: [] }
  const parent = rows.value.find(r => r.node.id === parentId.value)?.node
  ;(parent ? parent.children : nodes.value).push(node); name.value = ''; selectedId.value = node.id
}
async function remove(siblings: DocumentNodeInput[], node: DocumentNodeInput) {
  try { await ElMessageBox.confirm('删除此章节及其子章节？关联音频将解除关联并保留文件。', '删除章节', { confirmButtonText: '删除', cancelButtonText: '取消' }) }
  catch { return }
  siblings.splice(siblings.indexOf(node), 1); selectedId.value = ''; parentId.value = ''
}
async function clearAll() {
  try { await ElMessageBox.confirm('清空全部章节？', '清空章节', { confirmButtonText: '清空', cancelButtonText: '取消' }) }
  catch { return }
  nodes.value = []; selectedId.value = ''; parentId.value = ''
}
function bind() {
  if (!selected.value || !parseId.value || start.value === null || end.value === null) return
  selected.value.parseId = parseId.value; selected.value.startSequence = start.value; selected.value.endSequence = end.value
}
async function save() {
  if (!props.docId) return
  saving.value = true; error.value = ''; fields.value = []
  try {
    const response = await saveDocumentStructure(props.docId, nodes.value)
    tree.value = response.data; nodes.value = documentDraft(response.data.nodes); saved.value = JSON.stringify(nodes.value)
    success(response.searchIndexUpdated ? '章节已保存' : '章节已保存，搜索索引待更新')
  } catch (e) { fields.value = getStructureFieldErrors(e); error.value = getApiMessage(e, '保存失败') }
  finally { saving.value = false }
}
async function mayLeave() {
  if (!dirty.value) return true
  try { await ElMessageBox.confirm('放弃未保存的章节更改？', '未保存更改', { confirmButtonText: '放弃', cancelButtonText: '继续编辑' }); return true } catch { return false }
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
onMounted(() => { guard?.(mayLeave); window.addEventListener('beforeunload', beforeUnload); void load() })
onUnmounted(() => { guard?.(null); window.removeEventListener('beforeunload', beforeUnload); blockRequest++; loadRequest++ })
watch(() => props.docId, load)
</script>
<template>
  <div class="document-editor" v-loading="loading">
    <button class="btn btn-ghost" @click="docId && navigate?.('detail', docId)">返回书籍详情</button>
    <button class="btn" @click="docId && navigate?.('document-audio', docId)">管理书籍音频</button>
    <h2>{{ detail?.fileName }} · 书籍章节</h2>
    <p>章节顺序由上下移动决定。正文绑定单独保存；无源文件时可直接创建章节。音频关联独立于正文绑定保存。</p>
    <p v-if="!tree?.definition">请先在书籍详情选择结构定义。</p>
    <form v-else class="card authoring-controls" @submit.prevent="add">
      <label>上级章节 <select v-model="parentId" class="input" aria-label="上级章节"><option value="">书籍根目录</option><option v-for="r in rows" :key="r.node.id" :value="r.node.id">{{ '　'.repeat(r.depth) }}{{ r.node.name }}</option></select></label>
      <label>章节层级 <select v-model="levelId" class="input" aria-label="章节层级"><option v-for="l in levels" :key="l.id" :value="l.id">{{ l.name }}</option></select></label>
      <label>新章节名称 <input v-model="name" class="input" aria-label="新章节名称" maxlength="500" /></label>
      <button type="submit" class="btn" :disabled="!levelId || !name.trim()">添加章节</button>
    </form>
    <p v-if="error" role="alert">{{ error }}</p><p v-for="f in fields" :key="f.field + f.reason" class="field-error">{{ f.field }}: {{ f.reason }}</p>
    <div class="card chapter-list">
      <div v-for="r in rows" :key="r.node.id" class="chapter-row" :data-node-id="r.node.id" :style="{ paddingLeft: `${12 + r.depth * 24}px` }">
        <button class="btn btn-ghost btn-sm" @click="selectedId = r.node.id">选择</button>
        <span>{{ tree?.definition?.levels.find(l => l.id === r.node.levelId)?.name }}</span>
        <input v-model="r.node.name" class="input chapter-name" :aria-label="`章节名称 ${r.node.id}`" maxlength="500" />
        <span class="binding-state">{{ r.node.parseId ? `#${r.node.startSequence}–#${r.node.endSequence}` : '未绑定正文' }}</span>
        <button class="btn btn-ghost btn-sm" aria-label="上移" :disabled="r.siblings[0] === r.node" @click="moveSibling(r.siblings, r.node.id, -1)">↑</button>
        <button class="btn btn-ghost btn-sm" aria-label="下移" :disabled="r.siblings[r.siblings.length - 1] === r.node" @click="moveSibling(r.siblings, r.node.id, 1)">↓</button>
        <button class="btn btn-ghost btn-sm" @click="clearBinding(r.node)">解除正文绑定</button>
        <button class="btn btn-danger btn-sm" @click="remove(r.siblings, r.node)">删除章节</button>
      </div>
      <p v-if="!rows.length">暂无章节。选择层级并添加第一个章节。</p>
    </div>
    <button class="btn" :disabled="saving || !dirty" @click="save">保存章节</button>
    <button class="btn btn-ghost" :disabled="!rows.length" @click="clearAll">清空章节</button>
    <div class="card authoring-controls">
      <h3>显式绑定已有章节</h3><p>先选择章节和已成功解析的真实源。绑定不会重排章节。</p>
      <p>当前章节：{{ selected?.name || '未选择' }}</p>
      <label>正文解析 <select v-model="parseId" aria-label="正文解析" class="input"><option value="">不加载正文</option><option v-for="p in parses" :key="p.id" :value="p.id">{{ p.modelVersion }} · {{ p.parsedAt }} · {{ p.id }}</option></select></label>
      <template v-if="parseId">
        <label>起始块 <select v-model="start" aria-label="起始块" class="input"><option :value="null">选择</option><option v-for="b in blocks" :key="b.id" :value="b.sequence">#{{ b.sequence }}</option></select></label>
        <label>结束块 <select v-model="end" aria-label="结束块" class="input"><option :value="null">选择</option><option v-for="b in blocks" :key="b.id" :value="b.sequence">#{{ b.sequence }}</option></select></label>
        <button class="btn" :disabled="!selected || start === null || end === null" @click="bind">绑定到所选章节</button>
        <div class="document-blocks"><p v-for="b in blocks" :key="b.id" :data-seq="b.sequence">#{{ b.sequence }} {{ b.content }}</p></div>
      </template>
      <p v-else-if="!parses.length">尚无成功解析；可先编排章节，再附加文件并显式解析。</p>
    </div>
  </div>
</template>
<style scoped>.authoring-controls { padding: 16px; margin: 16px 0; display: grid; gap: 10px; } label { display: grid; gap: 4px; } .chapter-row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 10px; border-bottom: 1px solid var(--border-2); } .chapter-name { width: 260px; } .binding-state { color: var(--text-3); } .document-blocks { max-height: 360px; overflow: auto; white-space: pre-wrap; } .field-error { color: var(--danger); }</style>

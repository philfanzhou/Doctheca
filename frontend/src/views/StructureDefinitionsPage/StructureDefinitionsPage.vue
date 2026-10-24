<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { iconHtml } from '../../utils/icons'
import { formatTime } from '../../utils/format'
import { useToast } from '../../composables/useToast'
import {
  createStructureDefinition,
  deleteStructureDefinition,
  getApiMessage,
  getStructureFieldErrors,
  listStructureDefinitions,
  newId,
  updateStructureDefinition,
  type StructureDefinition,
  type StructureFieldError,
  type StructureLevelInput,
} from '../../services/structureApi'

// Structure definitions are runtime data: an administrator names a kind of document and its
// levels (each level optionally under a parent level). No level name is built in.

type DraftLevel = StructureLevelInput

const { success: toastSuccess, error: toastError } = useToast()

const definitions = ref<StructureDefinition[]>([])
const loading = ref(false)
const saving = ref(false)
const editingId = ref<string | null>(null) // null = new definition
const editorOpen = ref(false)
const draftName = ref('')
const draftLevels = ref<DraftLevel[]>([])
const fieldErrors = ref<StructureFieldError[]>([])

async function load() {
  loading.value = true
  try {
    definitions.value = (await listStructureDefinitions()).data
  } catch (e) {
    toastError(getApiMessage(e, '加载结构定义失败'))
  } finally {
    loading.value = false
  }
}

function startCreate() {
  editingId.value = null
  draftName.value = ''
  draftLevels.value = [{ id: newId(), name: '', parentLevelId: null, required: false }]
  fieldErrors.value = []
  editorOpen.value = true
}

function startEdit(definition: StructureDefinition) {
  editingId.value = definition.id
  draftName.value = definition.name
  draftLevels.value = definition.levels.map((level) => ({
    id: level.id,
    name: level.name,
    parentLevelId: level.parentLevelId,
    required: level.required,
  }))
  fieldErrors.value = []
  editorOpen.value = true
}

function addLevel() {
  const last = draftLevels.value[draftLevels.value.length - 1]
  // A new level defaults to a child of the last one, the common "next level down" case.
  draftLevels.value.push({ id: newId(), name: '', parentLevelId: last ? last.id : null, required: false })
}

function removeLevel(index: number) {
  const [removed] = draftLevels.value.splice(index, 1)
  for (const level of draftLevels.value) {
    if (level.parentLevelId === removed.id) level.parentLevelId = removed.parentLevelId
  }
}

function moveLevel(index: number, delta: number) {
  const target = index + delta
  if (target < 0 || target >= draftLevels.value.length) return
  const list = draftLevels.value
  ;[list[index], list[target]] = [list[target], list[index]]
}

function parentOptions(level: DraftLevel) {
  return draftLevels.value.filter((candidate) => candidate.id !== level.id)
}

function levelLabel(id: string | null): string {
  if (!id) return ''
  const level = draftLevels.value.find((l) => l.id === id)
  return level?.name || '（未命名层级）'
}

function errorFor(field: string): string | undefined {
  return fieldErrors.value.find((error) => error.field === field)?.reason
}

const generalErrors = computed(() =>
  fieldErrors.value.filter((error) => error.field === 'name' || error.field === 'levels')
)

// Indented preview of the configured hierarchy.
const preview = computed(() => {
  const rows: { name: string; depth: number }[] = []
  const visit = (parent: string | null, depth: number, seen: Set<string>) => {
    for (const level of draftLevels.value.filter((l) => l.parentLevelId === parent)) {
      if (seen.has(level.id)) continue
      seen.add(level.id)
      rows.push({ name: level.name || '（未命名层级）', depth })
      visit(level.id, depth + 1, seen)
    }
  }
  visit(null, 0, new Set())
  return rows
})

async function save() {
  saving.value = true
  fieldErrors.value = []
  const levels = draftLevels.value.map((level) => ({
    id: level.id,
    name: level.name.trim(),
    parentLevelId: level.parentLevelId,
    required: level.required,
  }))
  try {
    if (editingId.value) {
      await updateStructureDefinition(editingId.value, draftName.value.trim(), levels)
    } else {
      await createStructureDefinition(draftName.value.trim(), levels)
    }
    toastSuccess('结构定义已保存')
    editorOpen.value = false
    await load()
  } catch (e) {
    fieldErrors.value = getStructureFieldErrors(e)
    toastError(fieldErrors.value.length ? '请修正标出的问题' : getApiMessage(e, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function remove(definition: StructureDefinition) {
  try {
    await ElMessageBox.confirm(`确定删除结构定义「${definition.name}」？已选择它的文档将变为未设置。`, '删除确认', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await deleteStructureDefinition(definition.id)
    toastSuccess('已删除')
    if (editingId.value === definition.id) editorOpen.value = false
    await load()
  } catch (e) {
    const status = (e as { response?: { status?: number } })?.response?.status
    toastError(status === 409 ? '该结构定义已被标注的结构节点使用，无法删除' : getApiMessage(e, '删除失败'))
  }
}

function levelTree(definition: StructureDefinition): { name: string; depth: number; required: boolean }[] {
  const rows: { name: string; depth: number; required: boolean }[] = []
  const visit = (parent: string | null, depth: number) => {
    for (const level of definition.levels.filter((l) => l.parentLevelId === parent)) {
      rows.push({ name: level.name, depth, required: level.required })
      visit(level.id, depth + 1)
    }
  }
  visit(null, 0)
  return rows
}

onMounted(load)
</script>

<template>
  <div>
    <div class="page-head">
      <div>
        <div class="page-title">结构定义</div>
        <div class="page-sub">配置文档的结构层级（如 单元 → 板块、章 → 节），随后在解析结果中按层级手工标注</div>
      </div>
      <div class="page-actions">
        <button class="btn btn-ghost btn-sm" @click="load">
          <span v-html="iconHtml('refresh')"></span>刷新
        </button>
        <button class="btn btn-sm" @click="startCreate">
          <span v-html="iconHtml('plus')"></span>新建结构定义
        </button>
      </div>
    </div>

    <div class="structure-defs">
      <div class="card" v-loading="loading">
        <div class="card-head">
          <div>
            <div class="card-title">已配置的结构定义</div>
            <div class="card-sub">共 {{ definitions.length }} 个</div>
          </div>
        </div>
        <div v-if="!definitions.length && !loading" class="empty">
          <span v-html="iconHtml('book')"></span><br />
          还没有结构定义，点击右上角新建
        </div>
        <div v-else class="def-list">
          <div
            v-for="definition in definitions"
            :key="definition.id"
            class="def-item"
            :class="{ active: editorOpen && editingId === definition.id }"
          >
            <div class="def-item-head">
              <div>
                <div class="def-name">{{ definition.name }}</div>
                <div class="td-sub">{{ definition.levels.length }} 个层级 · 更新于 {{ formatTime(definition.updatedAt) }}</div>
              </div>
              <div class="def-actions">
                <button class="btn btn-ghost btn-sm" @click="startEdit(definition)">编辑</button>
                <button class="btn btn-danger btn-sm" @click="remove(definition)">
                  <span v-html="iconHtml('trash')"></span>删除
                </button>
              </div>
            </div>
            <div class="def-levels">
              <span
                v-for="(row, i) in levelTree(definition)"
                :key="i"
                class="def-level"
                :style="{ marginLeft: row.depth * 18 + 'px' }"
              >
                <span class="badge indigo">{{ row.depth + 1 }}</span>{{ row.name }}
                <span v-if="row.required" class="badge amber">音频必需策略</span>
              </span>
            </div>
          </div>
        </div>
      </div>

      <div v-if="editorOpen" class="card def-editor">
        <div class="card-head">
          <div>
            <div class="card-title">{{ editingId ? '编辑结构定义' : '新建结构定义' }}</div>
            <div class="card-sub">层级的顺序决定显示顺序；上级为空表示顶层</div>
            <div class="card-sub">音频必需仅记录策略，音频校验尚未启用；必需层级的所有上级也必须设为必需。</div>
          </div>
        </div>

        <label class="filter-label" for="structure-definition-name">名称</label>
        <input
          id="structure-definition-name"
          v-model="draftName"
          class="input"
          maxlength="200"
          placeholder="如：英语教材"
        />
        <div v-if="errorFor('name')" class="field-error">{{ errorFor('name') }}</div>

        <div class="level-head">
          <span class="filter-label">层级</span>
          <button class="btn btn-ghost btn-sm" @click="addLevel">
            <span v-html="iconHtml('plus')"></span>添加层级
          </button>
        </div>
        <div v-for="(level, index) in draftLevels" :key="level.id" class="level-row">
          <div class="level-fields">
            <input
              v-model="level.name"
              class="input"
              maxlength="100"
              placeholder="层级名称"
              :aria-label="`层级 ${index + 1} 名称`"
            />
            <el-select
              v-model="level.parentLevelId"
              placeholder="（顶层）"
              clearable
              :aria-label="`层级 ${index + 1} 上级`"
              style="width: 180px"
            >
              <el-option v-for="option in parentOptions(level)" :key="option.id" :label="option.name || '（未命名层级）'" :value="option.id" />
            </el-select>
            <label><input v-model="level.required" type="checkbox" :aria-label="`层级 ${index + 1} 音频必需`" /> 音频必需</label>
            <button class="btn btn-ghost btn-sm" :disabled="index === 0" title="上移" @click="moveLevel(index, -1)">↑</button>
            <button class="btn btn-ghost btn-sm" :disabled="index === draftLevels.length - 1" title="下移" @click="moveLevel(index, 1)">↓</button>
            <button class="btn btn-danger btn-sm" :disabled="draftLevels.length === 1" title="删除层级" @click="removeLevel(index)">
              <span v-html="iconHtml('trash')"></span>
            </button>
          </div>
          <div class="td-sub">{{ level.parentLevelId ? `上级：${levelLabel(level.parentLevelId)}` : '顶层' }}</div>
          <div v-for="field in ['name', 'id', 'parentLevelId', 'required']" :key="field">
            <div v-if="errorFor(`levels[${index}].${field}`)" class="field-error">{{ errorFor(`levels[${index}].${field}`) }}</div>
          </div>
        </div>

        <div v-for="(error, i) in generalErrors.filter((e) => e.field === 'levels')" :key="i" class="field-error">
          {{ error.reason }}
        </div>

        <div class="level-preview">
          <div class="filter-label">层级预览</div>
          <div v-for="(row, i) in preview" :key="i" :style="{ paddingLeft: row.depth * 18 + 'px' }" class="td-sub">
            └ {{ row.name }}
          </div>
        </div>

        <div class="editor-foot">
          <button class="btn btn-ghost" @click="editorOpen = false">取消</button>
          <button class="btn" :disabled="saving" @click="save">保存</button>
        </div>
      </div>
    </div>
  </div>
</template>

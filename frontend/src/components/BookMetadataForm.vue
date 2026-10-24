<script setup lang="ts">
import { reactive, ref, onMounted, onUnmounted, inject } from 'vue'
import { ElMessageBox } from 'element-plus'
import { createBook, updateBook } from '../services/documentApi'
import type { DocumentFileDetail } from '../services/types'
import { listStructureDefinitions, getStructureFieldErrors, getApiMessage, type StructureDefinition } from '../services/structureApi'
const props = defineProps<{ detail?: DocumentFileDetail }>()
const emit = defineEmits<{ saved: [id: string]; cancel: [] }>()
const input = reactive({ fileName: props.detail?.fileName ?? '', subject: props.detail?.subject ?? '', grade: props.detail?.grade ?? '', year: props.detail?.year ?? '', structureDefinitionId: props.detail?.structureDefinitionId ?? '' })
let initial = JSON.stringify(input)
const definitions = ref<StructureDefinition[]>([])
const errors = ref<{ field: string; reason: string }[]>([])
const message = ref('')
const saving = ref(false)
const guard = inject<(fn: (() => Promise<boolean>) | null) => void>('setNavigationGuard')
async function mayLeave() {
  if (JSON.stringify(input) === initial) return true
  try { await ElMessageBox.confirm('放弃未保存的书籍信息？', '未保存更改', { confirmButtonText: '放弃', cancelButtonText: '继续编辑' }); return true } catch { return false }
}
onMounted(async () => { guard?.(mayLeave); try { definitions.value = (await listStructureDefinitions()).data } catch (e) { message.value = getApiMessage(e, '加载结构定义失败') } })
onUnmounted(() => guard?.(null))
async function save() {
  errors.value = []; message.value = ''; saving.value = true
  try { const response = props.detail ? await updateBook(props.detail.id, input) : await createBook(input); initial = JSON.stringify(input); emit('saved', response.data.id) }
  catch (e) { errors.value = getStructureFieldErrors(e); message.value = getApiMessage(e, '保存失败') }
  finally { saving.value = false }
}
function beforeUnload(event: BeforeUnloadEvent) { if (JSON.stringify(input) !== initial) { event.preventDefault(); event.returnValue = '' } }
onMounted(() => window.addEventListener('beforeunload', beforeUnload))
onUnmounted(() => window.removeEventListener('beforeunload', beforeUnload))
</script>
<template>
  <form class="card book-form section-gap" @submit.prevent="save">
    <h3>{{ detail ? '编辑书籍信息' : '新建书籍（无需文件）' }}</h3>
    <label>书名 <input v-model="input.fileName" aria-label="书名" class="input" maxlength="500" required /></label>
    <label>学科 <input v-model="input.subject" aria-label="学科" class="input" maxlength="50" /></label>
    <label>年级 <input v-model="input.grade" aria-label="年级" class="input" maxlength="20" /></label>
    <label>年份 <input v-model="input.year" aria-label="年份" class="input" maxlength="10" /></label>
    <label>结构定义 <select v-model="input.structureDefinitionId" aria-label="书籍结构定义" class="input"><option value="">未设置</option><option v-for="d in definitions" :key="d.id" :value="d.id">{{ d.name }}</option></select></label>
    <p v-if="message" role="alert">{{ message }}</p><p v-for="e in errors" :key="e.field" class="field-error">{{ e.field }}: {{ e.reason }}</p>
    <button class="btn" type="submit" :disabled="saving">保存书籍</button>
    <button class="btn btn-ghost" type="button" @click="mayLeave().then(ok => ok && emit('cancel'))">取消</button>
  </form>
</template>
<style scoped>.book-form { padding: 20px; display: grid; gap: 12px; } label { display: grid; gap: 4px; } .field-error { color: var(--danger); }</style>

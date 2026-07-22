<script setup lang="ts">
import { ref } from 'vue'
import { iconHtml } from '../../utils/icons'

const props = defineProps<{
  uploading: boolean
  progress: number
}>()

const emit = defineEmits<{
  (e: 'change', event: Event): void
  (e: 'drop', event: DragEvent): void
}>()
const fileUploadInput = ref<HTMLInputElement | null>(null)

function onClick() {
  fileUploadInput.value?.click()
}

defineExpose({ fileUploadInput })
</script>

<template>
  <div
    class="upload-zone"
    :class="{ drag: false }"
    @click="onClick"
    @dragover.prevent
    @drop="emit('drop', $event)"
  >
    <span v-html="iconHtml('upload')"></span>
    <div class="upload-title">
      {{ props.uploading ? `正在上传 ${props.progress}%` : '点击或拖拽文件到此处上传' }}
    </div>
    <div class="upload-hint">支持 PDF / DOCX / PPTX，单文件不超过 200 MB</div>
    <div
      v-if="props.uploading"
      class="progress-track"
      style="max-width: 340px; margin: 12px auto 0"
    >
      <div class="progress-fill" :style="{ width: props.progress + '%' }"></div>
    </div>
    <input
      ref="fileUploadInput"
      type="file"
      accept=".pdf,.docx,.doc,.pptx,.ppt"
      hidden
      @change="emit('change', $event)"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, watch, nextTick, onUnmounted } from 'vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  (e: 'close'): void
}>()

const modal = ref<HTMLElement | null>(null)
const overlay = ref<HTMLElement | null>(null)

watch(
  () => props.open,
  (v) => {
    if (v) {
      document.body.style.overflow = 'hidden'
      nextTick(() => {
        overlay.value?.classList.add('open')
        modal.value?.classList.add('open')
      })
    } else {
      overlay.value?.classList.remove('open')
      modal.value?.classList.remove('open')
      document.body.style.overflow = ''
    }
  }
)

onUnmounted(() => {
  document.body.style.overflow = ''
})

function onEsc(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) emit('close')
}
if (typeof window !== 'undefined') {
  window.addEventListener('keydown', onEsc)
  onUnmounted(() => window.removeEventListener('keydown', onEsc))
}
</script>

<template>
  <Teleport to="body">
    <div class="modal-wrap">
      <div
        class="overlay"
        style="position: absolute"
        ref="overlay"
        @click="emit('close')"
      ></div>
      <div class="modal" ref="modal">
        <slot></slot>
      </div>
    </div>
  </Teleport>
</template>

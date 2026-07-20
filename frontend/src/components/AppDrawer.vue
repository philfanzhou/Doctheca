<script setup lang="ts">
import { ref, watch, nextTick, onUnmounted } from 'vue'
import { Icon } from '../utils/icons'

const props = defineProps<{
  open: boolean
  title?: string
  subtitle?: string
}>()

const emit = defineEmits<{
  (e: 'close'): void
}>()

const overlay = ref<HTMLElement | null>(null)
const drawer = ref<HTMLElement | null>(null)

watch(
  () => props.open,
  (v) => {
    if (v) {
      document.body.style.overflow = 'hidden'
      nextTick(() => {
        overlay.value?.classList.add('open')
        drawer.value?.classList.add('open')
      })
    } else {
      overlay.value?.classList.remove('open')
      drawer.value?.classList.remove('open')
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
    <div class="overlay" ref="overlay" @click="emit('close')"></div>
    <div class="drawer" ref="drawer">
      <div class="drawer-head" v-if="$slots.head || props.title">
        <slot name="head">
          <div class="feed-ico" style="background: var(--primary-soft); color: var(--primary)">
            <Icon name="eye" />
          </div>
          <div style="flex: 1">
            <div class="drawer-title">{{ props.title }}</div>
            <div class="drawer-sub" v-if="props.subtitle">{{ props.subtitle }}</div>
          </div>
        </slot>
        <button class="icon-btn" @click="emit('close')">
          <Icon name="x" />
        </button>
      </div>
      <div class="drawer-body">
        <slot></slot>
      </div>
      <div class="drawer-foot" v-if="$slots.foot">
        <slot name="foot"></slot>
      </div>
    </div>
  </Teleport>
</template>

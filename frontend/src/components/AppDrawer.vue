<script setup lang="ts">
import { ref, watch, onBeforeUnmount } from 'vue'
import { Icon } from '../utils/icons'

const props = defineProps<{
  open: boolean
  title?: string
  subtitle?: string
}>()

const emit = defineEmits<{
  (e: 'close'): void
}>()

const drawer = ref<HTMLElement | null>(null)
let locked = false
let previousOverflow = ''
let opener: HTMLElement | null = null

function release() {
  if (!locked) return
  if (drawer.value?.contains(document.activeElement) && opener?.isConnected) {
    opener.focus({ preventScroll: true })
  }
  document.body.style.overflow = previousOverflow
  locked = false
  opener = null
}

watch(
  () => props.open,
  (open) => {
    if (open && !locked) {
      previousOverflow = document.body.style.overflow
      opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
      document.body.style.overflow = 'hidden'
      locked = true
    } else if (!open) {
      release()
    }
  },
  { immediate: true }
)

onBeforeUnmount(release)

function onEsc(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) emit('close')
}
if (typeof window !== 'undefined') {
  window.addEventListener('keydown', onEsc)
  onBeforeUnmount(() => window.removeEventListener('keydown', onEsc))
}
</script>

<template>
  <Teleport to="body">
    <div class="overlay drawer-overlay" :class="{ open: props.open }" :inert="!props.open" @click="emit('close')"></div>
    <div class="drawer" :class="{ open: props.open }" :inert="!props.open" :aria-hidden="!props.open" ref="drawer">
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
        <button class="icon-btn" aria-label="关闭" @click="emit('close')">
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

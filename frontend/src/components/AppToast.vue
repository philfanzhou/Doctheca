<script setup lang="ts">
import { useToast, escapeHtml } from '../composables/useToast'
import { iconHtml } from '../utils/icons'
import { computed } from 'vue'

const { toasts } = useToast()

const rendered = computed(() =>
  toasts.value.map((t) => ({
    id: t.id,
    type: t.type,
    html:
      (t.type === 'success' ? iconHtml('check') : iconHtml('alert')) +
      `<span>${escapeHtml(t.message)}</span>`,
  }))
)
</script>

<template>
  <Teleport to="body">
    <div id="toast-root">
      <div
        v-for="t in rendered"
        :key="t.id"
        :data-toast-id="t.id"
        class="toast"
        :class="t.type"
        v-html="t.html"
      ></div>
    </div>
  </Teleport>
</template>

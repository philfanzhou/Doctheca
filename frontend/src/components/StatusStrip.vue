<script setup lang="ts">
defineProps<{
  items: Array<{ key: string; label: string; count: number; color: string }>
  active: string
}>()

const emit = defineEmits<{
  (e: 'select', key: string): void
}>()
</script>

<template>
  <div class="strip">
    <button
      v-for="item in items"
      :key="item.key"
      class="strip-item"
      :class="{ active: active === item.key }"
      @click="emit('select', item.key)"
    >
      <span class="strip-dot" :style="{ background: item.color }"></span>
      <span>
        <span class="strip-num">{{ item.count }}</span><br />
        <span class="strip-label">{{ item.label }}</span>
      </span>
    </button>
  </div>
</template>

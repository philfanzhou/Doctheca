<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { Icon } from '../utils/icons'

const props = defineProps<{
  crumb: string
  showHamburger: boolean
  sidebarOpen: boolean
  username: string
}>()

const emit = defineEmits<{
  (e: 'toggle-sidebar'): void
  (e: 'logout'): void
}>()

const clock = ref('')
let timer: number | null = null

function tick() {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  clock.value = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(
    d.getHours()
  )}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

onMounted(() => {
  tick()
  timer = window.setInterval(tick, 1000)
})

onUnmounted(() => {
  if (timer !== null) clearInterval(timer)
})
</script>

<template>
  <header class="topbar">
    <button
      class="hamburger icon-btn"
      v-if="props.showHamburger"
      @click="emit('toggle-sidebar')"
    >
      <Icon name="menu" />
    </button>
    <div class="crumb">
      <span>文档库</span>
      <Icon name="chev" />
      <b>{{ props.crumb }}</b>
    </div>
    <div class="top-right">
      <span class="env-tag">内网环境</span>
      <span class="clock">{{ clock }}</span>
      <span class="topbar-user">{{ props.username }}</span>
      <button class="btn btn-ghost btn-sm" type="button" @click="emit('logout')">
        退出
      </button>
    </div>
  </header>
</template>

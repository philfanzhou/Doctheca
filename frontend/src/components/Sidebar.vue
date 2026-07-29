<script setup lang="ts">
import { ref, onMounted, nextTick, computed } from 'vue'
import { Icon, type IconName } from '../utils/icons'

interface NavItem {
  key: string
  label: string
  icon: IconName
}

const props = defineProps<{
  active: string
  navItems: NavItem[]
  open: boolean
  username: string
}>()

const emit = defineEmits<{
  (e: 'navigate', key: string): void
  (e: 'close'): void
}>()

const navRef = ref<HTMLElement | null>(null)
const indicator = ref<HTMLElement | null>(null)
const initials = computed(() =>
  props.username.trim().slice(0, 2).toUpperCase() || 'AD'
)

function moveIndicator() {
  if (!indicator.value || !navRef.value) return
  const act = navRef.value.querySelector<HTMLElement>('.nav-item.active')
  if (!act) {
    indicator.value.style.opacity = '0'
    return
  }
  indicator.value.style.opacity = '1'
  indicator.value.style.transform = `translateY(${act.offsetTop}px)`
}

function handleNavigate(key: string) {
  emit('navigate', key)
  emit('close')
}

onMounted(() => {
  nextTick(moveIndicator)
})
defineExpose({ moveIndicator })
</script>

<template>
  <aside class="sidebar" :class="{ open: props.open }">
    <div class="brand">
      <div class="brand-mark">若</div>
      <div class="brand-text">若愚学习平台<span>管理控制台</span></div>
    </div>
    <nav class="nav" ref="navRef">
      <div class="nav-indicator" ref="indicator"></div>
      <div class="nav-label">ruoyu.doclibrary</div>
      <button
        v-for="item in props.navItems"
        :key="item.key"
        class="nav-item"
        :class="{ active: props.active === item.key }"
        @click="handleNavigate(item.key)"
      >
        <Icon :name="item.icon" />
        <span>{{ item.label }}</span>
      </button>
    </nav>
    <div class="sidebar-foot">
      <div class="admin-chip">
        <div class="avatar">{{ initials }}</div>
        <div>
          <div class="name">{{ props.username }}</div>
          <div class="role">文档库管理员</div>
        </div>
      </div>
    </div>
  </aside>
</template>

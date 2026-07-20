<script setup lang="ts">
import { ref, onMounted, watch, nextTick, computed } from 'vue'

interface Segment {
  value: number
  color: string
  label: string
}

const props = defineProps<{
  segments: Segment[]
  centerLabel?: string
  centerValue?: number | string
}>()

const wrap = ref<HTMLElement | null>(null)
const R = 54
const C = 2 * Math.PI * R

const layout = computed(() => {
  const total = props.segments.reduce((s, p) => s + p.value, 0) || 1
  let off = C * 0.25
  return props.segments.map((p) => {
    const len = (p.value / total) * C
    const seg = {
      ...p,
      dash: `${Math.max(len - 3, 0)} ${C - len + 3}`,
      offset: off,
    }
    off -= len
    return seg
  })
})

function runDraws() {
  if (!wrap.value) return
  wrap.value
    .querySelectorAll<SVGCircleElement>('.donut-seg')
    .forEach((s, i) => {
      const dash = s.dataset.dash || '0 0'
      s.style.strokeDasharray = `0 ${C}`
      requestAnimationFrame(() => {
        s.style.transition = `stroke-dasharray .9s cubic-bezier(.22,.61,.36,1) ${i * 0.12}s`
        s.style.strokeDasharray = dash
      })
    })
}

onMounted(runDraws)
watch(() => props.segments, () => nextTick(runDraws), { deep: true })
</script>

<template>
  <svg
    viewBox="0 0 140 140"
    style="width: 150px; height: 150px; flex-shrink: 0"
    ref="wrap"
  >
    <circle
      v-for="(s, i) in layout"
      :key="i"
      class="donut-seg"
      cx="70"
      cy="70"
      :r="R"
      fill="none"
      :stroke="s.color"
      stroke-width="13"
      :data-dash="s.dash"
      :stroke-dashoffset="s.offset"
      stroke-linecap="round"
    />
    <text
      v-if="centerValue !== undefined"
      x="70"
      y="66"
      text-anchor="middle"
      style="font-size: 24px; font-weight: 680; fill: var(--text); letter-spacing: -.5px"
    >
      {{ centerValue }}
    </text>
    <text
      v-if="centerLabel"
      x="70"
      y="84"
      text-anchor="middle"
      style="font-size: 10.5px; fill: var(--text-3)"
    >
      {{ centerLabel }}
    </text>
  </svg>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch, nextTick } from 'vue'

const props = defineProps<{
  data: number[]
  labels?: string[]
  width?: number
  height?: number
}>()

function escHtml(s: string): string {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

const svgWrap = ref<HTMLElement | null>(null)
const pad = 30
const w = computed(() => props.width ?? 620)
const h = computed(() => props.height ?? 200)

// Geometry is a computed so the chart re-renders whenever props change
// (previously it was built once during setup and never updated).
const path = computed<{ line: string; area: string; dots: string; grid: string; labels: string }>(() => {
  const wv = w.value
  const hv = h.value
  const data = props.data
  if (!data.length) return { line: '', area: '', dots: '', grid: '', labels: '' }
  const rawMax = Math.max(...data)
  const rawMin = Math.min(...data)
  const X = (i: number) => pad + i * ((wv - pad - 8) / Math.max(data.length - 1, 1))
  // Flat data (all-equal, incl. all-zero) makes the scaled range collapse to 0,
  // producing 0/0 -> NaN coordinates. Render a valid horizontal baseline instead.
  const flat = rawMax === rawMin
  const max = rawMax * 1.08
  const min = rawMin * 0.9
  const Y = (v: number) => (flat ? hv - 26 : hv - 26 - ((v - min) / (max - min)) * (hv - 48))
  let d = `M${X(0)},${Y(data[0])}`
  for (let i = 1; i < data.length; i++) {
    const x0 = X(i - 1), y0 = Y(data[i - 1]), x1 = X(i), y1 = Y(data[i])
    const cx = (x0 + x1) / 2
    d += ` C${cx},${y0} ${cx},${y1} ${x1},${y1}`
  }
  const gridLines = [0.25, 0.5, 0.75].map((f) => hv - 26 - f * (hv - 48))
  const grid = gridLines.map((y) => `<line class="chart-grid-line" x1="${pad}" y1="${y}" x2="${wv - 8}" y2="${y}"/>`).join('')
  const dotIdx = [6, 13, 20, 27].filter((i) => i < data.length)
  const dots = dotIdx.map((i) => `<circle class="chart-dot" cx="${X(i)}" cy="${Y(data[i])}" r="3.2"/>`).join('')
  // No built-in default labels: axis text is rendered only when the caller passes them.
  const lbls = props.labels ?? []
  const labels = lbls.map((t, i) => {
    const idx = Math.round((i * (data.length - 1)) / Math.max(lbls.length - 1, 1))
    return `<text class="chart-axis" x="${X(idx)}" y="${hv - 8}" text-anchor="middle">${escHtml(t)}</text>`
  }).join('')
  return {
    line: d,
    area: `${d} L${X(data.length - 1)},${hv - 26} L${X(0)},${hv - 26} Z`,
    dots,
    grid,
    labels,
  }
})

function runDraws() {
  if (!svgWrap.value) return
  const line = svgWrap.value.querySelector<SVGPathElement>('.chart-line')
  if (line) {
    const len = line.getTotalLength()
    line.style.strokeDasharray = String(len)
    line.style.strokeDashoffset = String(len)
    requestAnimationFrame(() => {
      line.style.transition = 'stroke-dashoffset 1.1s cubic-bezier(.22,.61,.36,1)'
      line.style.strokeDashoffset = '0'
    })
  }
  const area = svgWrap.value.querySelector<SVGPathElement>('.chart-area')
  if (area) {
    area.style.transition = 'opacity .8s ease .5s'
    area.style.opacity = '.9'
  }
  svgWrap.value
    .querySelectorAll<SVGCircleElement>('.chart-dot')
    .forEach((d, i) => {
      d.style.transition = `opacity .3s ease ${0.5 + i * 0.05}s`
      d.style.opacity = '1'
    })
}

onMounted(runDraws)
watch(() => props.data, () => nextTick(runDraws))
</script>

<template>
  <div class="chart-box" ref="svgWrap">
    <svg :viewBox="`0 0 ${w} ${h}`" style="width: 100%; height: auto">
      <defs>
        <linearGradient id="areaGrad" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stop-color="#4F46E5" stop-opacity=".14" />
          <stop offset="1" stop-color="#4F46E5" stop-opacity="0" />
        </linearGradient>
      </defs>
      <g v-html="path.grid"></g>
      <path class="chart-area" :d="path.area" />
      <path class="chart-line" :d="path.line" />
      <g v-html="path.dots"></g>
      <g v-html="path.labels"></g>
    </svg>
  </div>
</template>

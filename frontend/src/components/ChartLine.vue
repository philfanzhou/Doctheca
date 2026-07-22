<script setup lang="ts">
import { ref, onMounted, watch, nextTick } from 'vue'

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
const w = props.width ?? 620
const h = props.height ?? 200
const pad = 30

function buildPath(): { line: string; area: string; dots: string; grid: string; labels: string } {
  const data = props.data
  if (!data.length) return { line: '', area: '', dots: '', grid: '', labels: '' }
  const max = Math.max(...data) * 1.08
  const min = Math.min(...data) * 0.9
  const X = (i: number) => pad + i * ((w - pad - 8) / (data.length - 1))
  const Y = (v: number) => h - 26 - ((v - min) / (max - min)) * (h - 48)
  let d = `M${X(0)},${Y(data[0])}`
  for (let i = 1; i < data.length; i++) {
    const x0 = X(i - 1), y0 = Y(data[i - 1]), x1 = X(i), y1 = Y(data[i])
    const cx = (x0 + x1) / 2
    d += ` C${cx},${y0} ${cx},${y1} ${x1},${y1}`
  }
  const gridLines = [0.25, 0.5, 0.75].map((f) => h - 26 - f * (h - 48))
  const grid = gridLines.map((y) => `<line class="chart-grid-line" x1="${pad}" y1="${y}" x2="${w - 8}" y2="${y}"/>`).join('')
  const dotIdx = [6, 13, 20, 27].filter((i) => i < data.length)
  const dots = dotIdx.map((i) => `<circle class="chart-dot" cx="${X(i)}" cy="${Y(data[i])}" r="3.2"/>`).join('')
  const defaultLabels = ['6/22', '6/27', '7/2', '7/7', '7/12', '7/17', '今天']
  const lbls = props.labels ?? defaultLabels
  const labels = lbls.map((t, i) => {
    const idx = Math.round((i * (data.length - 1)) / Math.max(lbls.length - 1, 1))
    return `<text class="chart-axis" x="${X(idx)}" y="${h - 8}" text-anchor="middle">${escHtml(t)}</text>`
  }).join('')
  return {
    line: d,
    area: `${d} L${X(data.length - 1)},${h - 26} L${X(0)},${h - 26} Z`,
    dots,
    grid,
    labels,
  }
}

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

const path = buildPath()
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

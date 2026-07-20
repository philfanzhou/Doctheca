// Count-up animation composable — cubic ease-out 900ms, tabular-nums.
// Ported from prototype runCounters(). Sets target via data-count attribute.
import { onMounted, onUpdated, type Ref } from 'vue'

function runCounters(root: ParentNode | null) {
  if (!root) return
  const nodes = Array.from(root.querySelectorAll<HTMLElement>('[data-count]'))
  nodes.forEach((elm) => {
    if (elm.dataset.counted === '1') return
    elm.dataset.counted = '1'
    const target = parseFloat(elm.dataset.count || '0')
    const dur = 900
    const t0 = performance.now()
    const suffix = elm.dataset.suffix || ''
    const isInt = Number.isInteger(target)
    const tick = (t: number) => {
      const p = Math.min((t - t0) / dur, 1)
      const e = 1 - Math.pow(1 - p, 3)
      const v = target * e
      elm.textContent = (isInt ? String(Math.round(v)) : v.toFixed(1)).replace(
        /\B(?=(\d{3})+(?!\d))/g,
        ','
      ) + suffix
      if (p < 1) requestAnimationFrame(tick)
    }
    requestAnimationFrame(tick)
  })
}

export function useCountUp(root: Ref<HTMLElement | null>) {
  onMounted(() => runCounters(root.value))
  onUpdated(() => runCounters(root.value))
}

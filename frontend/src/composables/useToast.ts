// Toast notification composable — global single-instance.
// Replaces ElMessage with the prototype's dark toast visual (top-right, spring entrance).
import { ref, readonly } from 'vue'

export interface ToastItem {
  id: number
  message: string
  type: 'success' | 'error'
}

const toasts = ref<ToastItem[]>([])
let counter = 0

function push(message: string, type: ToastItem['type'] = 'success') {
  const id = ++counter
  toasts.value.push({ id, message, type })
  setTimeout(() => {
    // Mark leaving via DOM class, then remove after animation
    const el = document.querySelector(`[data-toast-id="${id}"]`)
    el?.classList.add('out')
    setTimeout(() => {
      toasts.value = toasts.value.filter((t) => t.id !== id)
    }, 240)
  }, 2600)
}

export function useToast() {
  return {
    toasts: readonly(toasts),
    success: (msg: string) => push(msg, 'success'),
    error: (msg: string) => push(msg, 'error'),
  }
}

// Helper: escape user-provided text for safe inclusion in v-html
export function escapeHtml(s: string): string {
  return String(s).replace(
    /[&<>"']/g,
    (c) =>
      ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;',
      })[c] as string
  )
}

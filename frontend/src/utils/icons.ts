// SVG icon registry — the icons the console uses, ported from the
// admin-console redesign prototype. Each icon renders raw SVG markup through v-html.
// Stroke 1.6, line-cap/join round; matches the prototype's linear icon style.
import { h, type VNode } from 'vue'

type IconName =
  | 'grid'
  | 'app'
  | 'shield'
  | 'file'
  | 'search'
  | 'upload'
  | 'back'
  | 'x'
  | 'check'
  | 'clock'
  | 'refresh'
  | 'eye'
  | 'alert'
  | 'zap'
  | 'book'
  | 'image'
  | 'chev'
  | 'menu'
  | 'logout'
  | 'plus'
  | 'doc'
  | 'warnTri'
  | 'trash'

const ICON_PATHS: Record<IconName, string> = {
  grid: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.8"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.8"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.8"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.8"/>',
  app: '<rect x="4" y="4" width="16" height="16" rx="3.5"/><path d="M9 12h6M12 9v6"/>',
  shield: '<path d="M12 3.5l7 2.6v5.2c0 4.4-2.9 7.6-7 9.2-4.1-1.6-7-4.8-7-9.2V6.1z"/><path d="M9.2 11.8l2 2 3.6-3.8"/>',
  file: '<path d="M6 3.5h7.5L18.5 9v11a1.5 1.5 0 0 1-1.5 1.5H6A1.5 1.5 0 0 1 4.5 20V5A1.5 1.5 0 0 1 6 3.5z"/><path d="M13.5 3.5V9H18.5"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4.5 4.5"/>',
  upload: '<path d="M12 16V5.5M7.5 9.5L12 5l4.5 4.5"/><path d="M4.5 16.5v2A1.5 1.5 0 0 0 6 20h12a1.5 1.5 0 0 0 1.5-1.5v-2"/>',
  back: '<path d="M15 5.5L8.5 12l6.5 6.5"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  check: '<path d="M4.5 12.5l5 5 10-11"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
  refresh: '<path d="M20 12a8 8 0 1 1-2.3-5.6"/><path d="M20 3.5V8h-4.5"/>',
  eye: '<path d="M2.5 12S6 5.8 12 5.8 21.5 12 21.5 12 18 18.2 12 18.2 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="2.8"/>',
  alert: '<path d="M12 4L2.8 19.5h18.4z"/><path d="M12 10v4M12 16.8v.2"/>',
  zap: '<path d="M13 2.5L4.5 13.5H11l-1 8L18.5 10.5H12z"/>',
  book: '<path d="M4.5 5.5A2 2 0 0 1 6.5 3.5H19.5v15H6.5a2 2 0 0 0-2 2z"/><path d="M4.5 18.5V5.5"/><path d="M9 8h6"/>',
  image: '<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><circle cx="9" cy="10" r="1.8"/><path d="M4.5 17.5l4.5-4.5 3.5 3.5 3-3 4 4"/>',
  chev: '<path d="M9 6l6 6-6 6"/>',
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  logout: '<path d="M14 4.5H7A1.5 1.5 0 0 0 5.5 6v12A1.5 1.5 0 0 0 7 19.5h7"/><path d="M10 12h10M17 8.5l3.5 3.5-3.5 3.5"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  doc: '<path d="M6 3.5h7.5L18.5 9v11A1.5 1.5 0 0 1 17 21.5H6A1.5 1.5 0 0 1 4.5 20V5A1.5 1.5 0 0 1 6 3.5z"/><path d="M8.5 13h7M8.5 16.5h5"/>',
  warnTri: '<circle cx="12" cy="12" r="8.5"/><path d="M12 8v4.5M12 15.8v.2"/>',
  trash: '<path d="M4 7h16M9 7V5a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2M6 7l1 13a2 2 0 0 0 2 1.9h6a2 2 0 0 0 2-1.9L18 7"/>',
}

function iconSvg(name: IconName): string {
  const path = ICON_PATHS[name]
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${path}</svg>`
}

export function Icon({ name }: { name: IconName }): VNode {
  return h('span', {
    class: 'icon',
    innerHTML: iconSvg(name),
  })
}

export function iconHtml(name: IconName): string {
  return iconSvg(name)
}

export type { IconName }

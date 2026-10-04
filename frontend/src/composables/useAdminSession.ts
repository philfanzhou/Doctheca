import { readonly, ref } from 'vue'
import { getSession, getCsrf, logout as logoutRequest, type AdminSession } from '../services/authApi'
import { setAuthFailureHandlers } from '../services/httpClient'

type SessionStatus = 'checking' | 'authenticated' | 'anonymous'
const status = ref<SessionStatus>('checking')
const session = ref<AdminSession | null>(null)
const message = ref('')
const forbiddenSignal = ref(0)
let generation = 0
let initializationPromise: Promise<void> | null = null
let csrfPromise: Promise<string> | null = null
let csrf: string | null = null
let logoutPromise: Promise<void> | null = null
let navigating = false

function clearSession(reason: string): void {
  generation++
  session.value = null
  csrf = null
  csrfPromise = null
  status.value = 'anonymous'
  message.value = reason
}
async function csrfCredential(): Promise<string> {
  if (csrf) return csrf
  if (status.value !== 'authenticated') throw new Error('Re-authentication is required')
  if (csrfPromise) return csrfPromise
  const current = generation
  const pending = getCsrf().then((value) => {
    if (current !== generation || status.value !== 'authenticated') throw new Error('Session changed')
    csrf = value
    return value
  }).finally(() => { if (csrfPromise === pending) csrfPromise = null })
  csrfPromise = pending
  return pending
}
setAuthFailureHandlers({
  getCsrf: csrfCredential,
  onAuthenticationRequired: () => clearSession('会话已过期，请重新认证。'),
  onForbidden: () => { forbiddenSignal.value++ },
})

function readAuthResult(): void {
  const url = new URL(location.href)
  const errors = url.searchParams.getAll('authError')
  const results = url.searchParams.getAll('authResult')
  const messages: Record<string, string> = {
    cancelled: '已取消登录，请重试。', notAdmin: '该账户没有管理员权限。',
    signInFailed: '登录失败，请重试。', identityUnavailable: '认证服务暂时不可用，请稍后重试。',
    logoutReturnFailed: '退出回跳失败，请重新登录。',
  }
  if (errors.length === 1 && results.length === 0) message.value = messages[errors[0]!] ?? ''
  else if (results.length === 1 && results[0] === 'signedOut' && errors.length === 0) message.value = '已退出登录。'
  url.searchParams.delete('authError')
  url.searchParams.delete('authResult')
  history.replaceState(null, '', url.pathname + url.search + url.hash)
}
async function initialize(): Promise<void> {
  if (initializationPromise) return initializationPromise
  const current = generation
  readAuthResult()
  const resultMessage = message.value
  status.value = 'checking'
  initializationPromise = (async () => {
    try {
      const value = await getSession()
      if (generation !== current) return
      generation++
      csrf = null
      session.value = value
      status.value = 'authenticated'
      message.value = ''
    } catch {
      if (generation === current) clearSession(resultMessage || '请使用 SignaCore 登录。')
      else if (resultMessage) message.value = resultMessage
    }
  })().finally(() => { initializationPromise = null })
  return initializationPromise
}
export function adminReturnUrl(hash: string): string {
  if (/^#(overview|docs|results|search)$/.test(hash)) return '/' + hash
  if (/^#detail(?:\/[A-Za-z0-9_-]+)?$/.test(hash)) return '/' + hash
  return '/'
}
function login(): void {
  if (navigating) return
  navigating = true
  location.assign('/admin/auth/oidc/start?returnUrl=' + encodeURIComponent(adminReturnUrl(location.hash)))
}
async function logout(): Promise<void> {
  if (logoutPromise) return logoutPromise
  // Invalidate initialization before requesting the credential; keep only the live session
  // and its CSRF value until the single sign-out request has been sent.
  generation++
  csrfPromise = null
  logoutPromise = (async () => {
    try {
      const result = await logoutRequest()
      clearSession('本地已退出，SignaCore 会话可能仍然有效。')
      if (result.logoutUrl) location.assign(result.logoutUrl)
    } catch {
      clearSession('退出失败，请重新认证。')
    }
  })().finally(() => { logoutPromise = null })
  return logoutPromise
}
export function useAdminSession() {
  return { status: readonly(status), session: readonly(session), message: readonly(message),
    forbiddenSignal: readonly(forbiddenSignal), initialize, login, logout }
}

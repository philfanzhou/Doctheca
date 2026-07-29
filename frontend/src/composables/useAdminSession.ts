import { readonly, ref } from 'vue'
import { AxiosError } from 'axios'
import {
  getSession,
  login as loginRequest,
  logout as logoutRequest,
  refreshSession,
  type AdminSession,
} from '../services/authApi'
import { setAuthFailureHandlers } from '../services/httpClient'

type SessionStatus = 'checking' | 'authenticated' | 'anonymous'

const status = ref<SessionStatus>('checking')
const session = ref<AdminSession | null>(null)
const forbiddenSignal = ref(0)
let initializationPromise: Promise<void> | null = null

function clearSession(): void {
  session.value = null
  status.value = 'anonymous'
}

setAuthFailureHandlers({
  onAuthenticationRequired: clearSession,
  onForbidden: () => {
    forbiddenSignal.value += 1
  },
})

async function initialize(): Promise<void> {
  if (initializationPromise) return initializationPromise

  initializationPromise = (async () => {
    status.value = 'checking'
    try {
      session.value = await getSession()
      status.value = 'authenticated'
      return
    } catch (error) {
      if (!(error instanceof AxiosError) || error.response?.status !== 401) {
        clearSession()
        return
      }
    }

    try {
      session.value = await refreshSession()
      status.value = 'authenticated'
    } catch {
      clearSession()
    }
  })().finally(() => {
    initializationPromise = null
  })

  return initializationPromise
}

async function login(username: string, password: string): Promise<void> {
  session.value = await loginRequest(username, password)
  status.value = 'authenticated'
}

async function logout(): Promise<void> {
  try {
    await logoutRequest()
  } finally {
    clearSession()
  }
}

export function useAdminSession() {
  return {
    status: readonly(status),
    session: readonly(session),
    forbiddenSignal: readonly(forbiddenSignal),
    initialize,
    login,
    logout,
  }
}

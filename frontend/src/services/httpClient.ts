import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { getProblemDetailsTitle } from './error'

interface RetriableRequestConfig extends InternalAxiosRequestConfig {
  docthecaRetry?: boolean
}

interface AuthFailureHandlers {
  onAuthenticationRequired?: () => void
  onForbidden?: () => void
}

const handlers: AuthFailureHandlers = {}

export const authHttpClient = axios.create({
  timeout: 30000,
  withCredentials: true,
})

export const httpClient = axios.create({
  timeout: 30000,
  withCredentials: true,
})

let refreshPromise: Promise<void> | null = null

function isAuthRequest(config: InternalAxiosRequestConfig | undefined): boolean {
  return config?.url?.startsWith('/admin/auth/') ?? false
}

// Unhandled exceptions on the marked JSON admin endpoints arrive as application/problem+json
// with a fixed, safe `title`. Normalizing that title into AxiosError.message on the single
// response-error path of both clients gives every existing `e.message` consumer a readable
// error without touching each call site; business JSON errors (data.message) and the
// 401 refresh/single-retry and 403 handling below stay exactly as they are.
function normalizeProblemDetailsMessage(error: AxiosError): AxiosError {
  const title = getProblemDetailsTitle(error.response)
  if (title !== null) {
    error.message = title
  }

  return error
}

authHttpClient.interceptors.response.use(
  (response) => response,
  (error: AxiosError) => Promise.reject(normalizeProblemDetailsMessage(error))
)

async function refreshAuthentication(): Promise<void> {
  if (!refreshPromise) {
    refreshPromise = authHttpClient
      .post('/admin/auth/refresh')
      .then(() => undefined)
      .finally(() => {
        refreshPromise = null
      })
  }

  return refreshPromise
}

httpClient.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    normalizeProblemDetailsMessage(error)
    const status = error.response?.status
    const config = error.config as RetriableRequestConfig | undefined

    if (status === 403) {
      handlers.onForbidden?.()
      return Promise.reject(error)
    }

    if (
      status !== 401 ||
      !config ||
      config.docthecaRetry ||
      isAuthRequest(config)
    ) {
      return Promise.reject(error)
    }

    config.docthecaRetry = true
    try {
      await refreshAuthentication()
      return await httpClient.request(config)
    } catch {
      handlers.onAuthenticationRequired?.()
      return Promise.reject(error)
    }
  }
)

export function setAuthFailureHandlers(nextHandlers: AuthFailureHandlers): void {
  handlers.onAuthenticationRequired = nextHandlers.onAuthenticationRequired
  handlers.onForbidden = nextHandlers.onForbidden
}

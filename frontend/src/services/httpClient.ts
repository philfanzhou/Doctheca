import axios, { AxiosError } from 'axios'
import { getProblemDetailsTitle } from './error'

interface AuthFailureHandlers {
  onAuthenticationRequired?: () => void
  onForbidden?: () => void
  getCsrf?: () => Promise<string>
}
const handlers: AuthFailureHandlers = {}
export const authHttpClient = axios.create({ timeout: 30000, withCredentials: true })
export const httpClient = axios.create({ timeout: 30000, withCredentials: true })

for (const client of [authHttpClient, httpClient]) {
  client.interceptors.request.use(async (config) => {
    if (!['get', 'head', 'options'].includes((config.method ?? 'get').toLowerCase())) {
      if (!handlers.getCsrf) throw new Error('Anti-forgery credential unavailable')
      config.headers.set('X-CSRF-TOKEN', await handlers.getCsrf())
    }
    return config
  })
  client.interceptors.response.use((response) => response, (error: AxiosError) => {
    const title = getProblemDetailsTitle(error.response)
    if (title !== null) error.message = title
    if (error.response?.status === 401) handlers.onAuthenticationRequired?.()
    if (error.response?.status === 403) handlers.onForbidden?.()
    return Promise.reject(error)
  })
}
export function setAuthFailureHandlers(next: AuthFailureHandlers): void {
  Object.assign(handlers, next)
}

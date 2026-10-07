import { authHttpClient } from './httpClient'

export interface AdminSession {
  authenticated: true
  reason: 'authenticated'
  username: string
  expiresAt: string
}
interface AuthResponse<T> { success: boolean; data: T }
export async function getSession(): Promise<AdminSession> {
  const response = await authHttpClient.get<AuthResponse<AdminSession>>('/admin/auth/oidc/session')
  if (!response.data.success || response.data.data?.authenticated !== true) throw new Error('Session unavailable')
  return response.data.data
}
export async function getCsrf(): Promise<string> {
  // The hosted-login client answers {"token": "..."} directly (issue #70).
  const response = await authHttpClient.get<{ token: string }>('/admin/auth/oidc/csrf')
  const value = response.data?.token
  if (!value) throw new Error('Anti-forgery credential unavailable')
  return value
}
export type SignOutOutcome = 'signedOut' | 'localOnly'
export async function logout(): Promise<SignOutOutcome> {
  try {
    const response = await authHttpClient.post<{ outcome?: string }>('/admin/auth/oidc/logout')
    if (response.data?.outcome === 'local_only') return 'localOnly'
    if (response.data?.outcome === 'csrf_rejected') throw new Error('Sign-out request was rejected.')
    return 'signedOut'
  } catch (error) {
    const data = (error as { response?: { data?: { outcome?: string } } })?.response?.data
    if (data?.outcome === 'csrf_rejected') throw new Error('Sign-out request was rejected.')
    if (data?.outcome === 'local_only') return 'localOnly'
    // The prepared sign-out answers with a redirect chain that leaves this origin, so the
    // browser reports a network error after the local session was revoked and the upstream
    // logout was requested (issue #70 D4).
    return 'signedOut'
  }
}

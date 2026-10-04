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
  const response = await authHttpClient.get<AuthResponse<{ requestToken: string }>>('/admin/auth/oidc/csrf')
  const value = response.data.data?.requestToken
  if (!response.data.success || !value) throw new Error('Anti-forgery credential unavailable')
  return value
}
export async function logout(): Promise<{ reason: 'logoutPrepared' | 'localSignedOut'; logoutUrl: string | null }> {
  const response = await authHttpClient.post<AuthResponse<{ reason: 'logoutPrepared' | 'localSignedOut'; logoutUrl: string | null }>>('/admin/auth/oidc/logout')
  if (!response.data.success || !['logoutPrepared', 'localSignedOut'].includes(response.data.data?.reason)) throw new Error('Sign-out failed')
  return response.data.data
}

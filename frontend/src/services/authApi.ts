import { authHttpClient } from './httpClient'

export interface AdminSession {
  userId?: string
  username: string
  roles: string[]
  expiresAt: number
}

interface AuthResponse<T> {
  success: boolean
  data: T
}

export async function login(
  username: string,
  password: string
): Promise<AdminSession> {
  const response = await authHttpClient.post<AuthResponse<AdminSession>>(
    '/admin/auth/login',
    { username, password }
  )
  return response.data.data
}

export async function getSession(): Promise<AdminSession> {
  const response = await authHttpClient.get<AuthResponse<AdminSession>>(
    '/admin/auth/session'
  )
  return response.data.data
}

export async function refreshSession(): Promise<AdminSession> {
  const response = await authHttpClient.post<AuthResponse<AdminSession>>(
    '/admin/auth/refresh'
  )
  return response.data.data
}

export async function logout(): Promise<void> {
  await authHttpClient.post('/admin/auth/logout')
}

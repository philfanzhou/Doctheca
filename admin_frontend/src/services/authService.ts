import axios, { type AxiosInstance } from 'axios'

export interface AuthTokens {
  accessToken: string
  refreshToken: string
  expiresIn: number
  expiresAt: number
}

export interface UserInfo {
  userId: string
  username: string
  authMethod: string
  roles: string[]
  permissions: string[]
}

export interface LoginResponse {
  success: boolean
  data: {
    accessToken: string
    refreshToken: string
    expiresIn: number
    expiresAt: number
    userInfo: UserInfo
  }
}

export interface RefreshResponse {
  success: boolean
  data: {
    accessToken: string
    refreshToken: string
    expiresIn: number
    expiresAt: number
  }
}

const TOKEN_KEY = 'docretrieval_auth_tokens'
const USER_KEY = 'docretrieval_auth_user'

class AuthService {
  private tokens: AuthTokens | null = null
  private user: UserInfo | null = null
  private refreshPromise: Promise<AuthTokens | null> | null = null

  constructor() {
    this.loadFromStorage()
  }

  private loadFromStorage() {
    try {
      const tokensJson = localStorage.getItem(TOKEN_KEY)
      const userJson = localStorage.getItem(USER_KEY)
      if (tokensJson) {
        this.tokens = JSON.parse(tokensJson)
      }
      if (userJson) {
        this.user = JSON.parse(userJson)
      }
    } catch {
      this.clear()
    }
  }

  private saveToStorage() {
    if (this.tokens) {
      localStorage.setItem(TOKEN_KEY, JSON.stringify(this.tokens))
    } else {
      localStorage.removeItem(TOKEN_KEY)
    }
    if (this.user) {
      localStorage.setItem(USER_KEY, JSON.stringify(this.user))
    } else {
      localStorage.removeItem(USER_KEY)
    }
  }

  getAccessToken(): string | null {
    return this.tokens?.accessToken ?? null
  }

  getRefreshToken(): string | null {
    return this.tokens?.refreshToken ?? null
  }

  getUser(): UserInfo | null {
    return this.user
  }

  isAuthenticated(): boolean {
    if (!this.tokens) return false
    // Check if token is expired (with 30s buffer)
    const now = Math.floor(Date.now() / 1000)
    return this.tokens.expiresAt > now + 30
  }

  canRefresh(): boolean {
    return !!this.tokens?.refreshToken
  }

  setTokens(tokens: AuthTokens, user?: UserInfo) {
    this.tokens = tokens
    if (user) this.user = user
    this.saveToStorage()
  }

  clear() {
    this.tokens = null
    this.user = null
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
  }

  async login(username: string, password: string): Promise<UserInfo> {
    const response = await axios.post<LoginResponse>('/admin/auth/login', {
      username,
      password
    })

    if (!response.data.success) {
      throw new Error('登录失败')
    }

    const { accessToken, refreshToken, expiresIn, expiresAt, userInfo } = response.data.data
    this.setTokens({ accessToken, refreshToken, expiresIn, expiresAt }, userInfo)
    return userInfo
  }

  async refresh(): Promise<AuthTokens | null> {
    // Prevent concurrent refresh requests
    if (this.refreshPromise) return this.refreshPromise

    this.refreshPromise = this.doRefresh()
    try {
      return await this.refreshPromise
    } finally {
      this.refreshPromise = null
    }
  }

  private async doRefresh(): Promise<AuthTokens | null> {
    const refreshToken = this.getRefreshToken()
    if (!refreshToken) return null

    try {
      const response = await axios.post<RefreshResponse>('/admin/auth/refresh', {
        refreshToken
      })

      if (!response.data.success) {
        this.clear()
        return null
      }

      const { accessToken, refreshToken: newRefreshToken, expiresIn, expiresAt } = response.data.data
      const tokens: AuthTokens = { accessToken, refreshToken: newRefreshToken, expiresIn, expiresAt }
      this.setTokens(tokens)
      return tokens
    } catch {
      this.clear()
      return null
    }
  }

  async logout(): Promise<void> {
    try {
      await axios.post('/admin/auth/logout')
    } catch {
      // Ignore errors on logout
    }
    this.clear()
  }

  async getMe(): Promise<UserInfo | null> {
    try {
      const token = this.getAccessToken()
      if (!token) return null
      const response = await axios.get('/admin/auth/me', {
        headers: { Authorization: `Bearer ${token}` }
      })
      if (response.data.success) {
        this.user = response.data.data
        this.saveToStorage()
        return this.user
      }
      return null
    } catch {
      return null
    }
  }
}

export const authService = new AuthService()

// Create axios instance with JWT interceptor
export function createAuthenticatedClient(): AxiosInstance {
  const client = axios.create({
    timeout: 30000
  })

  // Request interceptor: add Authorization header
  client.interceptors.request.use(async (config) => {
    let token = authService.getAccessToken()

    // If token is expired but we have a refresh token, try to refresh
    if (!authService.isAuthenticated() && authService.canRefresh()) {
      const newTokens = await authService.refresh()
      if (newTokens) {
        token = newTokens.accessToken
      }
    }

    if (token) {
      config.headers.Authorization = `Bearer ${token}`
    }

    return config
  })

  // Response interceptor: handle 401
  client.interceptors.response.use(
    (response) => response,
    async (error) => {
      const originalRequest = error.config

      if (error.response?.status === 401 && !originalRequest._retry) {
        originalRequest._retry = true

        const newTokens = await authService.refresh()
        if (newTokens) {
          originalRequest.headers.Authorization = `Bearer ${newTokens.accessToken}`
          return client(originalRequest)
        }

        // Refresh failed, redirect to login
        authService.clear()
        window.location.href = '/login'
      }

      return Promise.reject(error)
    }
  )

  return client
}

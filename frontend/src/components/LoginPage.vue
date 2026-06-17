<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage } from 'element-plus'
import { authService } from '../services/authService'

const emit = defineEmits<{
  (e: 'loginSuccess'): void
}>()

const username = ref('')
const password = ref('')
const loading = ref(false)

async function handleLogin() {
  if (!username.value.trim() || !password.value) {
    ElMessage.warning('请输入用户名和密码')
    return
  }

  loading.value = true
  try {
    await authService.login(username.value.trim(), password.value)
    ElMessage.success('登录成功')
    emit('loginSuccess')
  } catch (error: unknown) {
    const msg = error && typeof error === 'object' && 'response' in error
      ? (error as { response?: { status?: number } }).response?.status === 401
        ? '用户名或密码错误'
        : '登录失败，请检查网络连接'
      : '登录失败'
    ElMessage.error(msg)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-header">
        <div class="login-logo">DR</div>
        <h1 class="login-title">DocRetrieval Admin</h1>
        <p class="login-subtitle">文档检索管理后台</p>
      </div>
      <form class="login-form" @submit.prevent="handleLogin">
        <div class="form-group">
          <label>用户名</label>
          <div class="input-wrap">
            <input
              v-model="username"
              type="text"
              placeholder="请输入用户名"
              autocomplete="username"
              :disabled="loading"
            />
          </div>
        </div>
        <div class="form-group">
          <label>密码</label>
          <div class="input-wrap">
            <input
              v-model="password"
              type="password"
              placeholder="请输入密码"
              autocomplete="current-password"
              :disabled="loading"
              @keyup.enter="handleLogin"
            />
          </div>
        </div>
        <button class="btn btn-primary login-btn" :disabled="loading" type="submit">
          <svg v-if="loading" class="spinner" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M21 12a9 9 0 1 1-6.219-8.56" />
          </svg>
          {{ loading ? '登录中...' : '登录' }}
        </button>
      </form>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-primary, #f5f7fa);
}

.login-card {
  width: 400px;
  background: var(--bg-card, #fff);
  border-radius: 12px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.08);
  padding: 40px 32px;
}

.login-header {
  text-align: center;
  margin-bottom: 32px;
}

.login-logo {
  width: 56px;
  height: 56px;
  border-radius: 12px;
  background: var(--primary-color, #409eff);
  color: #fff;
  font-size: 20px;
  font-weight: 700;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  margin-bottom: 16px;
}

.login-title {
  font-size: 22px;
  font-weight: 600;
  color: var(--text-primary, #303133);
  margin: 0 0 4px;
}

.login-subtitle {
  font-size: 14px;
  color: var(--text-secondary, #909399);
  margin: 0;
}

.login-form .form-group {
  margin-bottom: 20px;
}

.login-form label {
  display: block;
  font-size: 13px;
  font-weight: 500;
  color: var(--text-primary, #303133);
  margin-bottom: 6px;
}

.login-btn {
  width: 100%;
  margin-top: 8px;
  height: 40px;
  font-size: 15px;
}
</style>

<script setup lang="ts">
import { ref } from 'vue'
import { AxiosError } from 'axios'
import { useAdminSession } from '../composables/useAdminSession'

const { login } = useAdminSession()
const username = ref('')
const password = ref('')
const submitting = ref(false)
const errorMessage = ref('')

async function submit(): Promise<void> {
  if (!username.value.trim() || !password.value) {
    errorMessage.value = '请输入用户名和密码。'
    return
  }

  submitting.value = true
  errorMessage.value = ''
  try {
    await login(username.value.trim(), password.value)
    password.value = ''
  } catch (error) {
    if (error instanceof AxiosError && error.response?.status === 403) {
      errorMessage.value = '该账户没有管理员权限。'
    } else if (error instanceof AxiosError && error.response?.status === 502) {
      errorMessage.value = '认证服务暂时不可用，请稍后重试。'
    } else {
      errorMessage.value = '用户名或密码错误。'
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="login-page doclibrary-admin">
    <section class="login-card" aria-labelledby="login-title">
      <div class="login-brand">
        <div class="brand-mark">若</div>
        <div>
          <div class="login-product">若愚学习平台</div>
          <div class="login-service">DocLibrary 管理后台</div>
        </div>
      </div>

      <div class="login-heading">
        <h1 id="login-title">管理员登录</h1>
        <p>请使用 Identity 管理员账户继续。</p>
      </div>

      <form class="login-form" @submit.prevent="submit">
        <label for="username">用户名</label>
        <el-input
          id="username"
          v-model="username"
          autocomplete="username"
          :disabled="submitting"
          placeholder="请输入用户名"
          size="large"
        />

        <label for="password">密码</label>
        <el-input
          id="password"
          v-model="password"
          type="password"
          autocomplete="current-password"
          show-password
          :disabled="submitting"
          placeholder="请输入密码"
          size="large"
        />

        <p v-if="errorMessage" class="login-error" role="alert">
          {{ errorMessage }}
        </p>

        <button class="btn login-submit" type="submit" :disabled="submitting">
          {{ submitting ? '正在登录…' : '登录' }}
        </button>
      </form>
    </section>
  </main>
</template>

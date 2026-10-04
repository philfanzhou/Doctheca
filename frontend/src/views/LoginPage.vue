<script setup lang="ts">
import { useAdminSession } from '../composables/useAdminSession'
const { login, message } = useAdminSession()
</script>

<template>
  <main class="login-page doctheca-admin">
    <section class="login-card" aria-labelledby="login-title">
      <div class="login-brand">
        <div class="brand-mark">若</div>
        <div>
          <div class="login-product">若愚学习平台</div>
          <div class="login-service">Doctheca 管理后台</div>
        </div>
      </div>

      <div class="login-heading">
        <h1 id="login-title">管理员登录</h1>
        <p>请使用 SignaCore 管理员账户继续。</p>
      </div>

      <form class="login-form" @submit.prevent="login">
        <p v-if="message" class="login-error" role="alert">{{ message }}</p>
        <button class="btn login-submit" type="submit">使用 SignaCore 登录</button>
      </form>
    </section>
  </main>
</template>

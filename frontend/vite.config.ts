import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    host: '0.0.0.0',
    port: 5174,
    proxy: {
      '/admin': {
        target: 'http://localhost:5012',
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: '../wwwroot'
  }
})

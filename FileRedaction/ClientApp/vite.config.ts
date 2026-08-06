import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:60267',
        changeOrigin: true,
        secure: false
      }
    }
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true
  }
})

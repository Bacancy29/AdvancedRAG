import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The .NET API runs on :5080; the dev server proxies /api so the browser sees one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
})

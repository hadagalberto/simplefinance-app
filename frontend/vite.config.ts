// Vite build and dev-server settings for the SimpleFinance web client.
import { fileURLToPath } from 'node:url'

import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Absolute path of src/, so '@/x' resolves the same way tsconfig.app.json "paths" does.
const srcDir = fileURLToPath(new URL('./src', import.meta.url))

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': srcDir,
    },
  },
  server: {
    port: 5173,
  },
  preview: {
    port: 4173,
  },
})

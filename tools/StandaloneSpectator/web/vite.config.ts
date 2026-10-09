import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { proxy: Object.fromEntries(['/ui/config', '/status', '/login', '/watch', '/leave', '/state'].map(path => [path, 'http://127.0.0.1:18743'])) },
})

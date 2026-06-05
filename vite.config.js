import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // C# HAL — 遥测 + 硬件控制 + 配置持久化
      "/api": { target: "http://localhost:3100", changeOrigin: true },
      "/ws": { target: "ws://localhost:3100", ws: true },
    },
    watch: {
      ignored: ["**/server/**", "**/uxtu-reference/**", "**/_*/**", "**/_*"],
    },
  },
})

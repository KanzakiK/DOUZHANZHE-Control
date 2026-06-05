import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // C# HAL 后端 — 遥测 + 硬件控制
      "/api/telemetry": { target: "http://localhost:3100", changeOrigin: true },
      "/api/control": { target: "http://localhost:3100", changeOrigin: true },
      "/api/health": { target: "http://localhost:3100", changeOrigin: true },
      "/api/discover": { target: "http://localhost:3100", changeOrigin: true },
      "/api/smu": { target: "http://localhost:3100", changeOrigin: true },
      "/ws": { target: "ws://localhost:3100", ws: true },
      // 已迁移到 C# HAL
      "/api/uxtu": { target: "http://localhost:3100", changeOrigin: true },
      "/api/system": { target: "http://localhost:3100", changeOrigin: true },
      "/api/ryzenadj": { target: "http://localhost:3100", changeOrigin: true },
      "/api/fan": { target: "http://localhost:3100", changeOrigin: true },
      "/api/custom-params": { target: "http://localhost:3100", changeOrigin: true },
      "/api/ui-state": { target: "http://localhost:3100", changeOrigin: true },
      "/api/default-config": { target: "http://localhost:3100", changeOrigin: true },
    },
    watch: {
      ignored: ["**/server/**", "**/uxtu-reference/**", "**/_*/**", "**/_*"],
    },
  },
})

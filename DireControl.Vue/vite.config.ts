import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import vueDevTools from 'vite-plugin-vue-devtools'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    vueDevTools(),
  ],
  server: {
    proxy: {
      '/api': 'http://localhost:5010',
      '/swagger': 'http://localhost:5010',
      '/hubs': {
        target: 'http://localhost:5010',
        ws: true,
      },
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    },
  },
  build: {
    rollupOptions: {
      output: {
        // Keep the big third-party libraries out of the app entry chunk so the
        // initial load parses only what the map view actually needs.
        manualChunks: {
          vuetify: ['vuetify'],
          leaflet: ['leaflet', 'leaflet.heat'],
          charts: ['chart.js', 'vue-chartjs'],
          signalr: ['@microsoft/signalr'],
        },
      },
    },
  },
})

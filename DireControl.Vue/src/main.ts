import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import 'leaflet/dist/leaflet.css'

import App from './App.vue'
import router from './router'

const THEME_STORAGE_KEY = 'direcontrol-theme'
const storedTheme = localStorage.getItem(THEME_STORAGE_KEY)
const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches
const initialTheme = storedTheme ?? (prefersDark ? 'dark' : 'light')

const vuetify = createVuetify({
  components,
  directives,
  display: {
    mobileBreakpoint: 768,
  },
  theme: {
    defaultTheme: initialTheme,
  },
})

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(vuetify)

app.mount('#app')

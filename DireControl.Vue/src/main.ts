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
  // Component defaults matching the app's design: 8-10px radii, outlined
  // compact inputs everywhere.
  defaults: {
    VCard: { rounded: 'lg' },
    VBtn: { rounded: 'lg' },
    VTextField: { variant: 'outlined', density: 'compact' },
    VSelect: { variant: 'outlined', density: 'compact' },
    VCombobox: { variant: 'outlined', density: 'compact' },
    VAutocomplete: { variant: 'outlined', density: 'compact' },
  },
  theme: {
    defaultTheme: initialTheme,
    themes: {
      // Tokens from the overhaul mock (plan/ux-overhaul-mock-2026-11/src/01-head.html):
      // semantic roles plus three packet-source colours — `rf` (heard over the
      // air), `is` (APRS-IS) and `own` (sent by this station). Use color="rf" /
      // "is" / "own" rather than ad-hoc hex; charts read them from useTheme().
      light: {
        dark: false,
        colors: {
          background: '#f1f4f3',
          surface: '#ffffff',
          'surface-variant': '#e8edec',
          'surface-light': '#e8edec',
          'surface-bright': '#f8faf9',
          'on-surface': '#162022',
          'on-surface-variant': '#4c5c5f',
          primary: '#006e68',
          'on-primary': '#ffffff',
          secondary: '#4c5c5f',
          success: '#20803e',
          warning: '#a86800',
          error: '#c02430',
          info: '#1c60ba',
          rf: '#b05c00',
          is: '#744ace',
          own: '#1668d6',
          'on-background': '#162022',
        },
      },
      dark: {
        dark: true,
        colors: {
          background: '#0d1213',
          surface: '#151c1d',
          'surface-variant': '#1f282a',
          'surface-light': '#1f282a',
          'surface-bright': '#1a2223',
          'on-surface': '#dee8e6',
          'on-surface-variant': '#9caeac',
          primary: '#54d4c4',
          'on-primary': '#04201e',
          secondary: '#9caeac',
          success: '#5cc878',
          warning: '#ecb040',
          error: '#f66e74',
          info: '#6eaafa',
          rf: '#f4a23c',
          is: '#b092fc',
          own: '#6caaff',
          'on-background': '#dee8e6',
        },
      },
    },
  },
})

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(vuetify)

app.mount('#app')

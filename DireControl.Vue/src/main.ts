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
  // Component defaults matching the design mock: 8-10px radii, outlined
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
      // Both themes define the same semantic roles plus two custom packet-source
      // colors: `rf` (heard over the air) and `is` (heard via APRS-IS). Use
      // color="rf" / color="is" instead of ad-hoc greens and blues.
      light: {
        dark: false,
        colors: {
          background: '#f2f4f7',
          surface: '#ffffff',
          'surface-light': '#eef1f5',
          'surface-bright': '#ffffff',
          'on-background': '#1b2733',
          'on-surface': '#1b2733',
          primary: '#1467c8',
          secondary: '#4d6070',
          success: '#1a7f37',
          warning: '#9a6700',
          error: '#cf222e',
          info: '#0e7490',
          rf: '#b45309',
          is: '#7c4dd8',
        },
      },
      dark: {
        dark: true,
        colors: {
          // Blue-tinted dark ground from the design mock, not stock #121212.
          background: '#0e1116',
          surface: '#161b22',
          'surface-light': '#1d242e',
          'surface-bright': '#242d38',
          'on-background': '#e6edf3',
          'on-surface': '#e6edf3',
          primary: '#4d9fff',
          secondary: '#9fb0c0',
          success: '#3fb950',
          warning: '#d29922',
          error: '#f85149',
          info: '#3ec1dd',
          rf: '#f0883e',
          is: '#a371f7',
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

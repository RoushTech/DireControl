import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { aliases } from 'vuetify/iconsets/mdi-svg'
import 'vuetify/styles'
import 'leaflet/dist/leaflet.css'

import { mdiKebab } from './plugins/mdiIcons'

import App from './App.vue'
import router from './router'

const THEME_STORAGE_KEY = 'direcontrol-theme'
const storedTheme = localStorage.getItem(THEME_STORAGE_KEY)
const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches
const initialTheme = storedTheme ?? (prefersDark ? 'dark' : 'light')

const vuetify = createVuetify({
  components,
  directives,
  // SVG paths from @mdi/js instead of the icon font; existing `mdi-*` strings
  // are resolved by the custom set in plugins/mdiIcons.ts.
  icons: {
    defaultSet: 'mdi',
    aliases,
    sets: { mdi: mdiKebab },
  },
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

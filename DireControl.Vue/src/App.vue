<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useTheme } from 'vuetify'
import { useMessagesStore } from '@/stores/messagesStore'
import { useAlertsStore } from '@/stores/alertsStore'
import { useUiStore } from '@/stores/uiStore'
import { getStatus } from '@/api/statusApi'
import { getAbout } from '@/api/aboutApi'

const THEME_STORAGE_KEY = 'direcontrol-theme'

const router = useRouter()
const route = useRoute()
const theme = useTheme()
const messagesStore = useMessagesStore()
const alertsStore = useAlertsStore()
const uiStore = useUiStore()

const isDark = ref(theme.global.current.value.dark)
const apiOffline = ref(false)
const direwolfDisconnected = ref(false)
const showShortcutsDialog = ref(false)
const version = ref<string | null>(null)
const mobileDrawerOpen = ref(false)

const shortcuts = [
  { key: 'M', description: 'Open compose message' },
  { key: 'F', description: 'Focus station search' },
  { key: 'B', description: 'Go to Beacon Stream' },
  { key: 'Esc', description: 'Close panel / deselect station' },
  { key: '?', description: 'Show this shortcuts overlay' },
]

// Persist theme changes
watch(isDark, (dark) => {
  theme.global.name.value = dark ? 'dark' : 'light'
  localStorage.setItem(THEME_STORAGE_KEY, dark ? 'dark' : 'light')
})

function toggleTheme() {
  isDark.value = !isDark.value
}

// Status polling
let statusInterval: ReturnType<typeof setInterval> | null = null

async function pollStatus() {
  try {
    const status = await getStatus()
    apiOffline.value = false
    direwolfDisconnected.value = !status.direwolfConnected
  } catch {
    apiOffline.value = true
    direwolfDisconnected.value = false
  }
}

// Global keyboard shortcuts
function onKeydown(e: KeyboardEvent) {
  // Don't fire when user is typing in an input
  const target = e.target as HTMLElement
  if (
    target.tagName === 'INPUT' ||
    target.tagName === 'TEXTAREA' ||
    target.isContentEditable
  ) {
    return
  }

  switch (e.key) {
    case '?':
      e.preventDefault()
      showShortcutsDialog.value = true
      break
    case 'Escape':
      window.dispatchEvent(new CustomEvent('shortcut:esc'))
      break
    case 'm':
    case 'M':
      e.preventDefault()
      uiStore.triggerCompose()
      router.push('/messages')
      break
    case 'f':
    case 'F':
      e.preventDefault()
      window.dispatchEvent(new CustomEvent('shortcut:focus-search'))
      break
    case 'b':
    case 'B':
      e.preventDefault()
      router.push('/beacons')
      break
  }
}

onMounted(async () => {
  alertsStore.startSignalR()

  try {
    await messagesStore.fetchInbox()
  } catch {
    // ignore — count shows 0 until fetched
  }

  try {
    await alertsStore.fetchAlerts()
  } catch {
    // ignore — count shows 0 until fetched
  }

  try {
    const about = await getAbout()
    version.value = about.version
  } catch {
    // ignore — version display is non-critical
  }

  await pollStatus()
  statusInterval = setInterval(pollStatus, 10_000)

  window.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  if (statusInterval !== null) clearInterval(statusInterval)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <v-app>
    <!-- Mobile navigation drawer -->
    <v-navigation-drawer
      v-if="!route.meta.isPopOut"
      v-model="mobileDrawerOpen"
      temporary
      location="left"
    >
      <v-list density="compact" nav>
        <v-list-item
          to="/"
          prepend-icon="mdi-map"
          title="Map"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/beacons"
          prepend-icon="mdi-radio-tower"
          title="Beacon Stream"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/messages"
          prepend-icon="mdi-message-text"
          title="Messages"
          @click="mobileDrawerOpen = false"
        >
          <template #append>
            <v-badge
              v-if="messagesStore.unreadCount > 0"
              :content="messagesStore.unreadCount"
              color="error"
              inline
            />
          </template>
        </v-list-item>
        <v-list-item
          to="/alerts"
          prepend-icon="mdi-bell"
          title="Alerts"
          @click="mobileDrawerOpen = false"
        >
          <template #append>
            <v-badge
              v-if="alertsStore.unacknowledgedCount > 0"
              :content="alertsStore.unacknowledgedCount"
              color="warning"
              inline
            />
          </template>
        </v-list-item>
        <v-list-item
          to="/statistics"
          prepend-icon="mdi-chart-bar"
          title="Statistics"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/settings"
          prepend-icon="mdi-cog"
          title="Settings"
          @click="mobileDrawerOpen = false"
        />
      </v-list>
    </v-navigation-drawer>

    <!-- App bar — hidden in pop-out windows -->
    <v-app-bar v-if="!route.meta.isPopOut" density="compact" color="surface">
      <!-- Hamburger button — mobile only -->
      <v-app-bar-nav-icon
        class="d-flex d-mobile-nav-hide"
        size="small"
        @click="mobileDrawerOpen = !mobileDrawerOpen"
      />

      <v-app-bar-title class="font-weight-bold">
        DireControl
        <span v-if="version" class="text-caption text-medium-emphasis ml-2">{{ version }}</span>
      </v-app-bar-title>
      <template #append>
        <!-- Desktop nav — hidden on mobile -->
        <div class="desktop-nav">
          <v-btn to="/" variant="text" size="small">Map</v-btn>
          <v-btn to="/beacons" variant="text" size="small">Beacon Stream</v-btn>

          <v-btn to="/messages" variant="text" size="small" class="position-relative">
            Messages
            <v-badge
              v-if="messagesStore.unreadCount > 0"
              :content="messagesStore.unreadCount"
              color="error"
              floating
            />
          </v-btn>

          <v-btn to="/alerts" variant="text" size="small" class="position-relative">
            Alerts
            <v-badge
              v-if="alertsStore.unacknowledgedCount > 0"
              :content="alertsStore.unacknowledgedCount"
              color="warning"
              floating
            />
          </v-btn>

          <v-btn to="/statistics" variant="text" size="small">Statistics</v-btn>
          <v-btn to="/settings" variant="text" size="small">Settings</v-btn>
        </div>

        <v-btn
          :icon="isDark ? 'mdi-weather-sunny' : 'mdi-weather-night'"
          variant="text"
          size="small"
          :aria-label="isDark ? 'Switch to light mode' : 'Switch to dark mode'"
          @click="toggleTheme"
        />

        <v-btn
          icon="mdi-keyboard-outline"
          variant="text"
          size="small"
          aria-label="Keyboard shortcuts"
          class="desktop-nav"
          @click="showShortcutsDialog = true"
        />
      </template>
    </v-app-bar>

    <!-- Status banners -->
    <v-banner
      v-if="apiOffline"
      color="error"
      density="compact"
      icon="mdi-wifi-off"
      lines="one"
      :sticky="true"
    >
      <v-banner-text>Backend API is unreachable — retrying…</v-banner-text>
    </v-banner>

    <v-banner
      v-else-if="direwolfDisconnected"
      color="warning"
      density="compact"
      icon="mdi-radio-tower"
      lines="one"
      :sticky="true"
    >
      <v-banner-text>Direwolf is not connected — no new packets will be received</v-banner-text>
    </v-banner>

    <v-main class="fill-height">
      <router-view />
    </v-main>

    <!-- Toast notifications for alerts -->
    <div class="toast-stack">
      <v-slide-y-reverse-transition group>
        <v-alert
          v-for="toast in alertsStore.toasts"
          :key="toast.id"
          v-show="toast.show"
          :color="toast.color"
          variant="tonal"
          density="compact"
          closable
          class="toast-item"
          @click:close="alertsStore.dismissToast(toast.id)"
        >
          {{ toast.message }}
        </v-alert>
      </v-slide-y-reverse-transition>
    </div>

    <!-- Keyboard shortcuts overlay -->
    <v-dialog v-model="showShortcutsDialog" max-width="420">
      <v-card>
        <v-card-title class="d-flex align-center">
          <v-icon class="mr-2">mdi-keyboard-outline</v-icon>
          Keyboard Shortcuts
        </v-card-title>
        <v-card-text>
          <v-table density="compact">
            <tbody>
              <tr v-for="shortcut in shortcuts" :key="shortcut.key">
                <td class="py-1">
                  <kbd class="shortcut-key">{{ shortcut.key }}</kbd>
                </td>
                <td class="py-1 text-medium-emphasis">{{ shortcut.description }}</td>
              </tr>
            </tbody>
          </v-table>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="showShortcutsDialog = false">Close</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-app>
</template>

<style>
html,
body,
#app {
  height: 100%;
  margin: 0;
  overflow: hidden;
}

.toast-stack {
  position: fixed;
  bottom: 16px;
  right: 16px;
  z-index: 10000;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 360px;
  pointer-events: none;
}

.toast-item {
  pointer-events: all;
}

.shortcut-key {
  display: inline-block;
  padding: 2px 6px;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
  font-family: monospace;
  font-size: 0.875em;
  background: rgba(var(--v-theme-surface-variant), 0.5);
}

/* Desktop nav: shown on wide screens, hidden on mobile */
.desktop-nav {
  display: flex;
  align-items: center;
}

/* Hamburger: hidden on desktop, shown on mobile */
.d-mobile-nav-hide {
  display: none !important;
}

@media (max-width: 768px) {
  .desktop-nav {
    display: none !important;
  }

  .d-mobile-nav-hide {
    display: inline-flex !important;
  }
}
</style>

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useTheme } from 'vuetify'
import { useMessagesStore } from '@/stores/messagesStore'
import { useAlertsStore } from '@/stores/alertsStore'
import { useToastStore } from '@/stores/toastStore'
import { usePacketHubStore } from '@/stores/packetHub'
import { useUiStore } from '@/stores/uiStore'
import { getStatus, reconnectAprsIs } from '@/api/statusApi'
import { ModemStates, modemStateLabels, type ModemState } from '@/api/modemApi'
import { getAbout } from '@/api/aboutApi'
import { recordServerSync } from '@/utils/serverTime'
import { timeAgo } from '@/utils/time'
import { useTick } from '@/composables/useTick'

const THEME_STORAGE_KEY = 'direcontrol-theme'
const CLOCK_SYNC_INTERVAL_MS = 5 * 60 * 1000

let clockSyncTimer: ReturnType<typeof setInterval> | null = null

const router = useRouter()
const route = useRoute()
const theme = useTheme()
const messagesStore = useMessagesStore()
const alertsStore = useAlertsStore()
const toastStore = useToastStore()
const packetHub = usePacketHubStore()
const uiStore = useUiStore()
const { now } = useTick(5000)

const isDark = ref(theme.global.current.value.dark)
const apiOffline = ref(false)
const direwolfDisconnected = ref(false)
const modemState = ref<ModemState>(ModemStates.Disabled)
const aprsIsState = ref('Disabled')
const aprsIsServerName = ref<string | null>(null)
const aprsIsFilter = ref('')
const aprsIsSessionPacketCount = ref(0)
const aprsIsFirstDisconnectedAt = ref<string | null>(null)
const aprsIsLastConnectAttemptAt = ref<string | null>(null)
const aprsIsFailedAttempts = ref(0)
const aprsIsLastError = ref<string | null>(null)
const aprsIsReconnecting = ref(false)
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

// ─── Nav groups ──────────────────────────────────────────────────────────────
const activityRoutes = ['/beacons', '/radio', '/logs']
const commsRoutes = ['/messages', '/alerts']
const insightsRoutes = ['/statistics', '/network']

const activityActive = computed(() => activityRoutes.includes(route.path))
const commsActive = computed(
  () => commsRoutes.includes(route.path) || route.path.startsWith('/stations/'),
)
const insightsActive = computed(() => insightsRoutes.includes(route.path))

const commsBadgeCount = computed(() => messagesStore.unreadCount + alertsStore.unacknowledgedCount)

// Persist theme changes
watch(isDark, (dark) => {
  theme.global.name.value = dark ? 'dark' : 'light'
  localStorage.setItem(THEME_STORAGE_KEY, dark ? 'dark' : 'light')
})

function toggleTheme() {
  isDark.value = !isDark.value
}

// ─── Status polling + live pill ──────────────────────────────────────────────
let statusInterval: ReturnType<typeof setInterval> | null = null

const aprsIsStateColor = computed(() => {
  switch (aprsIsState.value) {
    case 'Connected':
      return 'success'
    case 'Connecting':
      return 'warning'
    case 'AuthFailed':
      return 'error'
    case 'Disconnected':
      return 'warning'
    default:
      return 'grey'
  }
})

const aprsIsStateLabel = computed(() => {
  switch (aprsIsState.value) {
    case 'Connected':
      return 'Connected'
    case 'Connecting':
      return 'Connecting…'
    case 'AuthFailed':
      return 'Auth Failed'
    case 'Disconnected':
      return 'Disconnected'
    default:
      return 'Disabled'
  }
})

/**
 * One pill for "is this dashboard live" — collapses the API, the realtime hub,
 * and the RF backend into a single glanceable state with detail in the popover.
 */
const livePill = computed<{ label: string; color: string; icon: string }>(() => {
  if (apiOffline.value) return { label: 'Offline', color: 'error', icon: 'mdi-wifi-off' }
  if (packetHub.state === 'disconnected')
    return { label: 'No live feed', color: 'error', icon: 'mdi-lan-disconnect' }
  if (packetHub.state === 'connecting')
    return { label: 'Connecting', color: 'warning', icon: 'mdi-lan-pending' }
  return { label: 'Live', color: 'success', icon: 'mdi-access-point' }
})

const modemStateColor = computed(() => {
  switch (modemState.value) {
    case ModemStates.Running:
      return 'success'
    case ModemStates.Error:
      return 'error'
    default:
      return 'grey'
  }
})

const lastPacketLabel = computed(() => {
  if (packetHub.lastPacketAt === null) return 'none this session'
  return timeAgo(new Date(packetHub.lastPacketAt).toISOString(), now.value)
})

async function pollStatus() {
  try {
    const status = await getStatus()
    apiOffline.value = false
    direwolfDisconnected.value = !status.direwolfConnected
    modemState.value = status.modemState
    aprsIsState.value = status.aprsIsState
    aprsIsServerName.value = status.aprsIsServerName
    aprsIsFilter.value = status.aprsIsFilter
    aprsIsSessionPacketCount.value = status.aprsIsSessionPacketCount
    aprsIsFirstDisconnectedAt.value = status.aprsIsFirstDisconnectedAt
    aprsIsLastConnectAttemptAt.value = status.aprsIsLastConnectAttemptAt
    aprsIsFailedAttempts.value = status.aprsIsFailedAttempts
    aprsIsLastError.value = status.aprsIsLastError
  } catch {
    apiOffline.value = true
    direwolfDisconnected.value = false
  }
}

function formatTimestamp(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString()
}

async function reconnectAprsIsNow() {
  aprsIsReconnecting.value = true
  try {
    await reconnectAprsIs()
    // Re-poll shortly after so the UI reflects the new attempt.
    setTimeout(pollStatus, 1000)
  } catch {
    // ignore — next poll will reflect the real state
  } finally {
    aprsIsReconnecting.value = false
  }
}

// Global keyboard shortcuts
function onKeydown(e: KeyboardEvent) {
  // Don't fire when user is typing in an input
  const target = e.target as HTMLElement
  if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable) {
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
  // The single realtime connection — every store and view shares it.
  void packetHub.start()

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

  await syncServerClock()
  // Re-sync periodically: the dashboard can stay open for days, over which the
  // client and server clocks drift apart.
  clockSyncTimer = setInterval(syncServerClock, CLOCK_SYNC_INTERVAL_MS)

  await pollStatus()
  statusInterval = setInterval(pollStatus, 10_000)

  window.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  if (statusInterval !== null) clearInterval(statusInterval)
  if (clockSyncTimer !== null) clearInterval(clockSyncTimer)
  window.removeEventListener('keydown', onKeydown)
})

async function syncServerClock() {
  try {
    const requestStart = Date.now()
    const about = await getAbout()
    recordServerSync(about.serverTime, requestStart, Date.now())
    version.value = about.version
  } catch {
    // ignore — non-critical; keep the last good offset and version
  }
}
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
        <v-list-item to="/" prepend-icon="mdi-map" title="Map" @click="mobileDrawerOpen = false" />

        <v-list-subheader>Activity</v-list-subheader>
        <v-list-item
          to="/beacons"
          prepend-icon="mdi-radio-tower"
          title="Beacon Stream"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/radio"
          prepend-icon="mdi-radio-handheld"
          title="Radio"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/logs"
          prepend-icon="mdi-text-box-outline"
          title="Logs"
          @click="mobileDrawerOpen = false"
        />

        <v-list-subheader>Comms</v-list-subheader>
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

        <v-list-subheader>Insights</v-list-subheader>
        <v-list-item
          to="/statistics"
          prepend-icon="mdi-chart-bar"
          title="Statistics"
          @click="mobileDrawerOpen = false"
        />
        <v-list-item
          to="/network"
          prepend-icon="mdi-access-point-network"
          title="Network"
          @click="mobileDrawerOpen = false"
        />

        <v-divider class="my-1" />
        <v-list-item
          to="/settings"
          prepend-icon="mdi-cog"
          title="Settings"
          @click="mobileDrawerOpen = false"
        />
      </v-list>
    </v-navigation-drawer>

    <!-- App bar — hidden in pop-out windows -->
    <v-app-bar v-if="!route.meta.isPopOut" density="compact" color="surface" flat border="b">
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
        <!-- Desktop nav — grouped: Map · Activity · Comms · Insights · Settings -->
        <div class="desktop-nav">
          <v-btn to="/" variant="text" size="small">Map</v-btn>

          <v-menu open-on-hover :close-delay="100">
            <template #activator="{ props }">
              <v-btn
                v-bind="props"
                variant="text"
                size="small"
                :color="activityActive ? 'primary' : undefined"
                append-icon="mdi-chevron-down"
              >
                Activity
              </v-btn>
            </template>
            <v-list density="compact" nav>
              <v-list-item to="/beacons" prepend-icon="mdi-radio-tower" title="Beacon Stream" />
              <v-list-item to="/radio" prepend-icon="mdi-radio-handheld" title="Radio" />
              <v-list-item to="/logs" prepend-icon="mdi-text-box-outline" title="Logs" />
            </v-list>
          </v-menu>

          <v-menu open-on-hover :close-delay="100">
            <template #activator="{ props }">
              <v-btn
                v-bind="props"
                variant="text"
                size="small"
                :color="commsActive ? 'primary' : undefined"
                append-icon="mdi-chevron-down"
                class="position-relative"
              >
                Comms
                <v-badge
                  v-if="commsBadgeCount > 0"
                  :content="commsBadgeCount"
                  color="error"
                  floating
                />
              </v-btn>
            </template>
            <v-list density="compact" nav>
              <v-list-item to="/messages" prepend-icon="mdi-message-text" title="Messages">
                <template #append>
                  <v-badge
                    v-if="messagesStore.unreadCount > 0"
                    :content="messagesStore.unreadCount"
                    color="error"
                    inline
                  />
                </template>
              </v-list-item>
              <v-list-item to="/alerts" prepend-icon="mdi-bell" title="Alerts">
                <template #append>
                  <v-badge
                    v-if="alertsStore.unacknowledgedCount > 0"
                    :content="alertsStore.unacknowledgedCount"
                    color="warning"
                    inline
                  />
                </template>
              </v-list-item>
            </v-list>
          </v-menu>

          <v-menu open-on-hover :close-delay="100">
            <template #activator="{ props }">
              <v-btn
                v-bind="props"
                variant="text"
                size="small"
                :color="insightsActive ? 'primary' : undefined"
                append-icon="mdi-chevron-down"
              >
                Insights
              </v-btn>
            </template>
            <v-list density="compact" nav>
              <v-list-item to="/statistics" prepend-icon="mdi-chart-bar" title="Statistics" />
              <v-list-item to="/network" prepend-icon="mdi-access-point-network" title="Network" />
            </v-list>
          </v-menu>

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

        <!-- Global live-status pill: hub + APRS-IS + modem in one place -->
        <v-menu open-on-hover :close-delay="100">
          <template #activator="{ props }">
            <v-chip
              v-bind="props"
              :color="livePill.color"
              size="small"
              variant="tonal"
              class="ml-1 mr-2"
              aria-label="Connection status"
            >
              <v-icon start size="14">{{ livePill.icon }}</v-icon>
              {{ livePill.label }}
            </v-chip>
          </template>
          <v-card min-width="300" density="compact">
            <v-card-title class="text-subtitle-2 pb-1">Realtime status</v-card-title>
            <v-card-text class="pt-0">
              <div class="d-flex align-center justify-space-between mb-1">
                <span class="text-body-2">Packet feed</span>
                <span class="text-caption" :class="`text-${livePill.color}`">
                  {{ packetHub.state }}
                </span>
              </div>
              <div class="d-flex align-center justify-space-between mb-1">
                <span class="text-body-2">Last packet</span>
                <span class="text-caption text-medium-emphasis">{{ lastPacketLabel }}</span>
              </div>
              <div class="d-flex align-center justify-space-between">
                <span class="text-body-2">Sound modem</span>
                <span class="text-caption" :class="`text-${modemStateColor}`">
                  {{ modemStateLabels[modemState] ?? 'Unknown' }}
                </span>
              </div>

              <template v-if="aprsIsState !== 'Disabled'">
                <v-divider class="my-2" />
                <div class="d-flex align-center mb-1">
                  <v-icon :color="aprsIsStateColor" size="12" class="mr-2">mdi-circle</v-icon>
                  <span class="text-body-2">APRS-IS · {{ aprsIsStateLabel }}</span>
                </div>
                <div v-if="aprsIsServerName" class="text-caption text-medium-emphasis">
                  Server: {{ aprsIsServerName }}
                </div>
                <div v-if="aprsIsFilter" class="text-caption text-medium-emphasis mt-1">
                  Filter: {{ aprsIsFilter }}
                </div>
                <div class="text-caption text-medium-emphasis mt-1">
                  Session packets: {{ aprsIsSessionPacketCount.toLocaleString() }}
                </div>

                <!-- Disconnection diagnostics -->
                <template v-if="aprsIsState !== 'Connected'">
                  <v-divider class="my-2" />
                  <div class="text-caption text-medium-emphasis">
                    First disconnected: {{ formatTimestamp(aprsIsFirstDisconnectedAt) }}
                  </div>
                  <div class="text-caption text-medium-emphasis mt-1">
                    Last attempt: {{ formatTimestamp(aprsIsLastConnectAttemptAt) }}
                  </div>
                  <div class="text-caption text-medium-emphasis mt-1">
                    Failed attempts: {{ aprsIsFailedAttempts.toLocaleString() }}
                  </div>
                  <div v-if="aprsIsLastError" class="text-caption text-error mt-1">
                    Error: {{ aprsIsLastError }}
                  </div>
                </template>
              </template>
            </v-card-text>
            <v-card-actions v-if="aprsIsState !== 'Disabled'" class="pt-0">
              <v-spacer />
              <v-btn
                size="small"
                variant="tonal"
                color="primary"
                prepend-icon="mdi-refresh"
                :loading="aprsIsReconnecting"
                @click="reconnectAprsIsNow"
              >
                Reconnect APRS-IS
              </v-btn>
            </v-card-actions>
          </v-card>
        </v-menu>
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

    <!-- Only warn about the missing TNC when the native sound modem is not
         carrying RF either — with the modem running, RF still flows. -->
    <v-banner
      v-else-if="direwolfDisconnected && modemState !== ModemStates.Running"
      color="warning"
      density="compact"
      icon="mdi-radio-tower"
      lines="one"
      :sticky="true"
    >
      <v-banner-text>No RF backend is connected — no new packets will be received</v-banner-text>
    </v-banner>

    <v-main class="fill-height">
      <router-view v-slot="{ Component }">
        <keep-alive include="RadioView">
          <component :is="Component" />
        </keep-alive>
      </router-view>
    </v-main>

    <!-- Single app-wide toast stack -->
    <div class="toast-stack">
      <v-slide-y-reverse-transition group>
        <v-alert
          v-for="toast in toastStore.toasts"
          :key="toast.id"
          v-show="toast.show"
          :color="toast.color"
          variant="tonal"
          density="compact"
          closable
          class="toast-item"
          @click:close="toastStore.dismiss(toast.id)"
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

/* Mock convention: callsigns render in mono, app-wide. */
.callsign-link {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-weight: 600;
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

<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useDisplay, useTheme } from 'vuetify'
import { useMessagesStore } from '@/stores/messagesStore'
import { useAlertsStore } from '@/stores/alertsStore'
import { useLightningAlertsStore } from '@/stores/lightningAlertsStore'
import { useToastStore } from '@/stores/toastStore'
import { usePacketHubStore } from '@/stores/packetHub'
import { useUiStore } from '@/stores/uiStore'
import { useHealthStore } from '@/stores/health'
import { getAbout } from '@/api/aboutApi'
import { recordServerSync } from '@/utils/serverTime'
import AudioMonitorButton from '@/components/AudioMonitorButton.vue'
import StatusStrip from '@/components/health/StatusStrip.vue'
import HealthBanners from '@/components/health/HealthBanners.vue'
import TxChip from '@/components/tx/TxChip.vue'
import TxQueueSheet from '@/components/tx/TxQueueSheet.vue'
import { useTxStore } from '@/stores/tx'

const THEME_STORAGE_KEY = 'direcontrol-theme'
const CLOCK_SYNC_INTERVAL_MS = 5 * 60 * 1000

let clockSyncTimer: ReturnType<typeof setInterval> | null = null

const router = useRouter()
const route = useRoute()
const theme = useTheme()
const messagesStore = useMessagesStore()
const alertsStore = useAlertsStore()
// Instantiated for its store-level hub subscription (toast + sound on strikes near home).
useLightningAlertsStore()
const toastStore = useToastStore()
const packetHub = usePacketHubStore()
const uiStore = useUiStore()
const healthStore = useHealthStore()
// Status poll, settings lookup and clock for the shared health model.
healthStore.start()
// Frame lifecycle (txFrameStateChanged) from app start, so every page and the
// TX chip see the same queue.
useTxStore()

// Derived from the live theme so Settings › Display changes show here too.
const isDark = computed(() => theme.global.current.value.dark)
const showShortcutsDialog = ref(false)
const version = ref<string | null>(null)
const commit = ref<string | null>(null)

const shortcuts = [
  { key: 'M', description: 'Open compose message' },
  { key: 'F', description: 'Focus station search' },
  { key: 'B', description: 'Go to Stream' },
  { key: 'Esc', description: 'Close panel / deselect station' },
  { key: '?', description: 'Show this shortcuts overlay' },
]

// ─── Navigation ──────────────────────────────────────────────────────────────
// One flat set of destinations in three groups; no hover menus anywhere
// (review 1.20). Desktop: labelled rail. Tablet: icon rail with labels under
// the icons. Phone: bottom nav with the four most-used pages plus More.
interface NavItem {
  to: string
  title: string
  icon: string
  /** Extra path prefixes that highlight this item (Station → Map, Setup → Settings). */
  also?: string[]
}
const NAV: Array<{ group: string; items: NavItem[] }> = [
  {
    group: 'Operate',
    items: [
      { to: '/', title: 'Map', icon: 'mdi-map', also: ['/stations/'] },
      { to: '/stream', title: 'Stream', icon: 'mdi-format-list-text' },
      { to: '/radio', title: 'Radio', icon: 'mdi-radio-handheld' },
      { to: '/messages', title: 'Messages', icon: 'mdi-message-text' },
      { to: '/terminal', title: 'Terminal', icon: 'mdi-console-network' },
    ],
  },
  {
    group: 'Monitor',
    items: [
      { to: '/alerts', title: 'Alerts', icon: 'mdi-bell' },
      { to: '/network', title: 'Network', icon: 'mdi-access-point-network' },
      { to: '/rf-heard', title: 'RF Heard', icon: 'mdi-antenna' },
    ],
  },
  {
    group: 'System',
    items: [
      { to: '/logs', title: 'Logs', icon: 'mdi-text-box-outline' },
      { to: '/radio/diagnostics', title: 'Diagnostics', icon: 'mdi-stethoscope' },
      { to: '/settings', title: 'Settings', icon: 'mdi-cog', also: ['/settings/', '/setup'] },
    ],
  },
]
const PHONE_NAV = ['/', '/stream', '/radio', '/messages']
const allNav = NAV.flatMap((g) => g.items)
const phoneNav = allNav.filter((i) => PHONE_NAV.includes(i.to))
const moreNav = allNav.filter((i) => !PHONE_NAV.includes(i.to))

function isActive(item: NavItem): boolean {
  if (route.path === item.to) return true
  return (item.also ?? []).some((p) => route.path.startsWith(p))
}
const moreActive = computed(() => moreNav.some(isActive) || route.path === '/setup')

const { width } = useDisplay()
/** Container widths from the mock: bottom nav below 700 px, icon rail below 1200 px. */
const wide = computed(() => width.value >= 1200)
/** Desktop can collapse the labelled rail to the icon rail (remembered per browser). */
const navMode = computed<'rail' | 'icons' | 'bottom'>(() =>
  width.value < 700 ? 'bottom' : !wide.value || uiStore.navCollapsed ? 'icons' : 'rail',
)
const moreOpen = ref(false)

const PAGE_TITLES: Record<string, string> = {
  '/setup': 'Setup checklist',
}
const pageTitle = computed(() => {
  if (route.path.startsWith('/stations/')) return 'Station'
  return PAGE_TITLES[route.path] ?? [...allNav].reverse().find(isActive)?.title ?? ''
})

/** Badge text per destination: failed messages win ("2!"), then unread; unacknowledged alerts. */
function badgeFor(item: NavItem): { content: string; color: string } | null {
  if (item.to === '/messages') {
    if (messagesStore.failedCount > 0)
      return { content: `${messagesStore.failedCount}!`, color: 'error' }
    if (messagesStore.unreadCount > 0)
      return { content: String(messagesStore.unreadCount), color: 'primary' }
  }
  if (item.to === '/alerts' && alertsStore.unacknowledgedCount > 0)
    return { content: String(alertsStore.unacknowledgedCount), color: 'warning' }
  return null
}
const moreBadge = computed(() => moreNav.map(badgeFor).find((b) => b) ?? null)

const showSetupChip = computed(
  () => healthStore.settingsLoaded && !healthStore.setupComplete && !uiStore.setupDismissed,
)

// Tell the operator what a reconnect refetched (review 1.7).
watch(
  () => packetHub.lastResync,
  (r) => {
    if (!r) return
    if (r.failed.length)
      toastStore.toast(
        `Reconnected — could not resync ${r.failed.join(', ')}; showing what was last loaded`,
        'warning',
        10_000,
      )
    else if (r.ok.length) toastStore.toast(`Reconnected — resynced ${r.ok.join(', ')}`, 'success')
  },
)

// Persist theme changes

function toggleTheme() {
  const next = isDark.value ? 'light' : 'dark'
  theme.global.name.value = next
  try {
    localStorage.setItem(THEME_STORAGE_KEY, next)
  } catch {
    /* private window: lasts for this page load */
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
      router.push('/stream')
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

  window.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  if (clockSyncTimer !== null) clearInterval(clockSyncTimer)
  window.removeEventListener('keydown', onKeydown)
})

async function syncServerClock() {
  try {
    const requestStart = Date.now()
    const about = await getAbout()
    recordServerSync(about.serverTime, requestStart, Date.now())
    version.value = about.version
    commit.value = about.commit
  } catch {
    // ignore — non-critical; keep the last good offset and version
  }
}
</script>

<template>
  <v-app>
    <!-- Desktop / tablet rail -->
    <v-navigation-drawer
      v-if="!route.meta.isPopOut && navMode !== 'bottom'"
      permanent
      :width="navMode === 'rail' ? 208 : 84"
      class="app-rail"
      :class="`app-rail--${navMode}`"
      aria-label="Main"
    >
      <div class="rail-brand">
        <span class="text-primary">▲</span>
        <template v-if="navMode === 'rail'">
          <span class="font-weight-bold">DireControl</span>
          <span
            v-if="version"
            class="text-caption text-medium-emphasis"
            :title="commit ? `commit ${commit}` : undefined"
            >{{ version }}</span
          >
        </template>
      </div>
      <nav>
        <template v-for="g in NAV" :key="g.group">
          <div v-if="navMode === 'rail'" class="rail-group">{{ g.group }}</div>
          <v-divider v-else class="my-1" />
          <router-link
            v-for="item in g.items"
            :key="item.to"
            :to="item.to"
            class="rail-item"
            :class="{ 'rail-item--active': isActive(item) }"
            :aria-current="isActive(item) ? 'page' : undefined"
            :aria-label="navMode === 'icons' ? item.title : undefined"
          >
            <span class="rail-icon">
              <!-- Icon rail: badge on the icon. Labelled rail: badge at the row's end. -->
              <v-badge
                v-if="badgeFor(item) && navMode === 'icons'"
                :content="badgeFor(item)!.content"
                :color="badgeFor(item)!.color"
              >
                <v-icon :icon="item.icon" size="22" />
              </v-badge>
              <v-icon v-else :icon="item.icon" size="22" />
            </span>
            <span class="rail-label">{{ item.title }}</span>
            <v-badge
              v-if="badgeFor(item) && navMode === 'rail'"
              :content="badgeFor(item)!.content"
              :color="badgeFor(item)!.color"
              inline
              class="ml-auto"
            />
          </router-link>
        </template>
      </nav>
      <template v-if="wide" #append>
        <div v-if="navMode === 'rail'" class="rail-foot text-caption text-medium-emphasis">
          Kiosk:
          <a href="/map-only" target="_blank" rel="noopener">map</a> ·
          <a href="/stream-only" target="_blank" rel="noopener">stream</a> ·
          <a href="/logs-only" target="_blank" rel="noopener">logs</a>
        </div>
        <div class="rail-collapse" :class="{ 'rail-collapse--icons': navMode === 'icons' }">
          <v-btn
            :icon="uiStore.navCollapsed ? 'mdi-chevron-double-right' : 'mdi-chevron-double-left'"
            variant="text"
            size="small"
            :aria-label="uiStore.navCollapsed ? 'Expand menu' : 'Collapse menu'"
            :aria-expanded="!uiStore.navCollapsed"
            :title="uiStore.navCollapsed ? 'Expand menu' : 'Collapse menu'"
            @click="uiStore.navCollapsed = !uiStore.navCollapsed"
          />
        </div>
      </template>
    </v-navigation-drawer>

    <!-- App bar — hidden in pop-out windows -->
    <v-app-bar v-if="!route.meta.isPopOut" density="compact" color="surface" flat border="b">
      <div class="app-title ml-3">
        <span v-if="navMode === 'bottom'" class="text-primary mr-1">▲</span>
        <v-btn
          v-if="route.path.startsWith('/stations/')"
          icon="mdi-arrow-left"
          size="small"
          variant="text"
          aria-label="Back to the map"
          to="/"
        />
        <span class="font-weight-bold">{{ pageTitle }}</span>
      </div>

      <v-spacer />

      <template #append>
        <v-btn
          v-if="showSetupChip"
          to="/setup"
          size="small"
          variant="tonal"
          color="info"
          prepend-icon="mdi-clipboard-check-outline"
          class="mr-1 setup-chip"
          :aria-label="`Setup checklist, ${healthStore.setupDone} of ${healthStore.setupRequired.length} done`"
        >
          Setup {{ healthStore.setupDone }}/{{ healthStore.setupRequired.length }}
        </v-btn>

        <AudioMonitorButton />

        <v-btn
          v-if="wide"
          icon="mdi-keyboard-outline"
          variant="text"
          size="small"
          aria-label="Keyboard shortcuts"
          @click="showShortcutsDialog = true"
        />

        <TxChip class="ml-1" />
        <!-- Worst-of status strip: LIVE / RF / IS / TX lanes, tap for detail -->
        <StatusStrip class="ml-2 mr-2" />

        <v-btn
          v-if="navMode !== 'bottom'"
          :icon="isDark ? 'mdi-weather-sunny' : 'mdi-weather-night'"
          variant="text"
          size="small"
          :aria-label="isDark ? 'Switch to light mode' : 'Switch to dark mode'"
          @click="toggleTheme"
        />
      </template>
    </v-app-bar>

    <!-- Phone: bottom nav + More sheet -->
    <v-bottom-navigation
      v-if="!route.meta.isPopOut && navMode === 'bottom'"
      grow
      density="comfortable"
      aria-label="Main"
    >
      <v-btn
        v-for="item in phoneNav"
        :key="item.to"
        :to="item.to"
        :active="isActive(item)"
        :aria-current="isActive(item) ? 'page' : undefined"
      >
        <v-badge
          v-if="badgeFor(item)"
          :content="badgeFor(item)!.content"
          :color="badgeFor(item)!.color"
        >
          <v-icon :icon="item.icon" />
        </v-badge>
        <v-icon v-else :icon="item.icon" />
        <span>{{ item.title }}</span>
      </v-btn>
      <v-btn :active="moreActive" aria-label="More pages" @click="moreOpen = true">
        <v-badge v-if="moreBadge" :content="moreBadge.content" :color="moreBadge.color">
          <v-icon icon="mdi-dots-horizontal" />
        </v-badge>
        <v-icon v-else icon="mdi-dots-horizontal" />
        <span>More</span>
      </v-btn>
    </v-bottom-navigation>

    <v-bottom-sheet v-model="moreOpen">
      <v-card>
        <v-card-item>
          <v-card-title class="text-subtitle-1">More</v-card-title>
          <template #append>
            <v-btn
              icon="mdi-close"
              variant="text"
              size="small"
              aria-label="Close"
              @click="moreOpen = false"
            />
          </template>
        </v-card-item>
        <v-list nav density="comfortable">
          <v-list-item
            v-for="item in moreNav"
            :key="item.to"
            :to="item.to"
            :prepend-icon="item.icon"
            :title="item.title"
            :active="isActive(item)"
            @click="moreOpen = false"
          >
            <template v-if="badgeFor(item)" #append>
              <v-badge :content="badgeFor(item)!.content" :color="badgeFor(item)!.color" inline />
            </template>
          </v-list-item>
          <v-list-item
            to="/setup"
            prepend-icon="mdi-clipboard-check-outline"
            title="Setup checklist"
            @click="moreOpen = false"
          />
          <v-list-item
            :prepend-icon="isDark ? 'mdi-weather-sunny' : 'mdi-weather-night'"
            :title="isDark ? 'Light theme' : 'Dark theme'"
            @click="toggleTheme"
          />
        </v-list>
      </v-card>
    </v-bottom-sheet>

    <v-main class="fill-height">
      <div class="main-col">
        <!-- One banner per failure, with the action that fixes it -->
        <HealthBanners v-if="!route.meta.isPopOut" />
        <div class="main-view">
          <router-view v-slot="{ Component }">
            <keep-alive include="RadioView">
              <component :is="Component" />
            </keep-alive>
          </router-view>
        </div>
      </div>
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
          <template v-if="toast.action" #append>
            <v-btn
              size="small"
              variant="text"
              :to="toast.action.to"
              @click="(toast.action.run?.(), toastStore.dismiss(toast.id))"
            >
              {{ toast.action.label }}
            </v-btn>
          </template>
        </v-alert>
      </v-slide-y-reverse-transition>
    </div>

    <TxQueueSheet v-if="!route.meta.isPopOut" />

    <!-- Kiosk views stay chrome-less except one worst-of status chip. -->
    <StatusStrip v-if="route.meta.isPopOut" compact class="kiosk-status" />

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

/* Banners sit above the routed view, which keeps the rest of the height. */
.main-col {
  display: flex;
  flex-direction: column;
  height: 100%;
}
.main-view {
  flex: 1 1 0;
  min-height: 0;
  position: relative;
}

/* Convention: callsigns render in mono, app-wide. */
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

/* Button labels never touch their borders — x-small text buttons get real
   horizontal padding app-wide. */
.v-btn--size-x-small:not(.v-btn--icon) {
  padding-inline: 10px;
  min-width: 0;
}

/* Kiosk: the only chrome is a small status chip. Bottom-right, because every
   kiosk view keeps its own toolbar along the top (above the map attribution). */
.kiosk-status {
  position: fixed;
  bottom: 28px;
  right: 10px;
  z-index: 1100;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.25);
}

/* ── Rail ── */
.app-rail .v-navigation-drawer__content {
  display: flex;
  flex-direction: column;
}
.rail-brand {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 14px 16px 8px;
}
.app-rail--icons .rail-brand {
  justify-content: center;
  padding-inline: 0;
}
.rail-group {
  font-size: 0.6875rem;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
  padding: 12px 16px 4px;
}
.rail-item {
  display: flex;
  align-items: center;
  gap: 12px;
  margin: 1px 8px;
  padding: 8px 10px;
  border-radius: 8px;
  color: rgba(var(--v-theme-on-surface), var(--v-high-emphasis-opacity));
  text-decoration: none;
  font-size: 0.875rem;
  min-height: 40px;
}
.rail-item:hover,
.rail-item:focus-visible {
  background: rgba(var(--v-theme-on-surface), 0.06);
}
.rail-item:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: -2px;
}
.rail-item--active {
  background: rgba(var(--v-theme-primary), 0.14);
  color: rgb(var(--v-theme-primary));
  font-weight: 600;
}
.rail-icon {
  position: relative;
  display: inline-flex;
}
.rail-foot a,
.rail-foot a:visited {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
}
.rail-foot a:hover,
.rail-foot a:focus-visible {
  text-decoration: underline;
}

/* Filled coloured buttons/chips: Vuetify's variant rule (on-surface text) wins over
   .bg-<color> here, giving light text on the light dark-theme fills (Compose,
   Connect, Save...). Re-assert the theme's on-<color>. Unlayered, so it wins. */
.v-btn.bg-primary,
.v-chip.bg-primary {
  color: rgb(var(--v-theme-on-primary));
}
.v-btn.bg-secondary,
.v-chip.bg-secondary {
  color: rgb(var(--v-theme-on-secondary));
}
.v-btn.bg-success,
.v-chip.bg-success {
  color: rgb(var(--v-theme-on-success));
}
.v-btn.bg-error,
.v-chip.bg-error {
  color: rgb(var(--v-theme-on-error));
}
.v-btn.bg-warning,
.v-chip.bg-warning {
  color: rgb(var(--v-theme-on-warning));
}
.v-btn.bg-info,
.v-chip.bg-info {
  color: rgb(var(--v-theme-on-info));
}

/* Disabled filled buttons: Vuetify keeps the brand fill and fades only the
   label, which reads as an enabled button with broken text (worst on the
   light-teal dark-theme primary). Use the neutral disabled look instead. */
.v-btn--disabled.v-btn--variant-elevated,
.v-btn--disabled.v-btn--variant-flat {
  background: rgba(var(--v-theme-on-surface), 0.12) !important;
  color: rgba(var(--v-theme-on-surface), 0.5) !important;
  box-shadow: none !important;
}
.v-btn--disabled.v-btn--variant-elevated .v-btn__overlay,
.v-btn--disabled.v-btn--variant-flat .v-btn__overlay {
  opacity: 0 !important;
}
/* Tablet: icon over a small label, centred. */
.app-rail--icons .rail-item {
  flex-direction: column;
  gap: 2px;
  margin: 1px 6px;
  padding: 6px 2px;
  text-align: center;
}
.app-rail--icons .rail-label {
  font-size: 0.6875rem;
  line-height: 1.1;
}
.rail-foot {
  padding: 12px 16px 0;
}
.rail-collapse {
  display: flex;
  justify-content: flex-end;
  padding: 4px 8px 8px;
}
.rail-collapse--icons {
  justify-content: center;
}
.app-title {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
@media (max-width: 480px) {
  .setup-chip {
    display: none;
  }
}
</style>

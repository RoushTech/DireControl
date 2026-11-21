import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { getStatus, reconnectAprsIs as apiReconnectAprsIs, type StatusDto } from '@/api/statusApi'
import { getSettings } from '@/api/stationsApi'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore } from '@/stores/radiosStore'
import { useUiStore } from '@/stores/uiStore'
import { serverNow } from '@/utils/serverTime'
import {
  deriveBanners,
  deriveHealth,
  deriveSetupSteps,
  type HealthInput,
  type RadioHealthInput,
} from '@/stores/healthModel'

export {
  agoShort,
  durationShort,
  isN0Call,
  type Health,
  type HealthBanner,
  type HealthLevel,
  type Lane,
} from '@/stores/healthModel'

const STATUS_POLL_MS = 10_000
const PTT_TESTED_KEY = 'direcontrol.pttTested'

/** Server-side CSMA cap (DireControl:Modem:CsmaMaxWaitSeconds default). */
export const CSMA_MAX_SECONDS = 20

function readPttTested(): boolean {
  try {
    return localStorage.getItem(PTT_TESTED_KEY) === '1'
  } catch {
    return false
  }
}

/**
 * Shared, worst-of system health. Owns the status poll, the callsign/home
 * lookup and the clock, and feeds the pure deriveHealth() model so the
 * status strip, status panel, banners and kiosk chip all say the same thing.
 */
export const useHealthStore = defineStore('health', () => {
  const hub = usePacketHubStore()
  const radiosStore = useRadiosStore()
  const ui = useUiStore()

  const status = ref<StatusDto | null>(null)
  const apiOffline = ref(false)
  const callsign = ref<string | null>(null)
  const homeSet = ref(false)
  const home = ref<{ lat: number; lon: number } | null>(null)
  const settingsLoaded = ref(false)
  const pttTested = ref(readPttTested())
  const now = ref(serverNow())

  let started = false
  let pollTimer: ReturnType<typeof setInterval> | null = null

  async function refreshStatus() {
    try {
      status.value = await getStatus()
      apiOffline.value = false
    } catch {
      apiOffline.value = true
    }
  }

  async function refreshSettings() {
    try {
      const s = await getSettings()
      callsign.value = s.ourCallsign
      homeSet.value = !!s.homePosition
      home.value = s.homePosition ? { lat: s.homePosition.lat, lon: s.homePosition.lon } : null
      settingsLoaded.value = true
    } catch {
      /* offline is reported by the status poll */
    }
  }

  function markPttTested() {
    pttTested.value = true
    try {
      localStorage.setItem(PTT_TESTED_KEY, '1')
    } catch {
      /* session only */
    }
  }

  async function reconnectAprsIs() {
    await apiReconnectAprsIs()
    setTimeout(() => void refreshStatus(), 1000)
  }

  /** Starts polling; called once from App.vue. */
  function start() {
    if (started) return
    started = true
    void refreshStatus()
    void refreshSettings()
    if (!radiosStore.loaded) void radiosStore.fetchRadios().catch(() => {})
    void radiosStore.fetchModemStatus()
    pollTimer = setInterval(() => void refreshStatus(), STATUS_POLL_MS)
    setInterval(() => {
      now.value = serverNow()
    }, 1000)
    hub.onReconnected(async () => {
      await Promise.all([refreshStatus(), refreshSettings()])
    }, 'status')
    // Settings edits change the checklist; re-read on leaving Settings/Setup.
    const route = useRoute()
    watch(
      () => route.path,
      (path, prev) => {
        if (prev && (prev.startsWith('/settings') || prev === '/setup') && path !== prev)
          void refreshSettings()
      },
    )
  }

  function stop() {
    if (pollTimer) clearInterval(pollTimer)
    pollTimer = null
  }

  const liveRadios = computed<RadioHealthInput[]>(() =>
    radiosStore.activeRadios.map((r) => ({
      radioId: r.id,
      name: r.name,
      modemEnabled: r.modem.modemEnabled,
      txConfigured: r.modem.modemEnabled ? r.modem.txEnabled : true,
      modem: radiosStore.modemStatus[r.id] ?? null,
    })),
  )

  // While the feed is down the radios store drops its live modem status; the
  // model greys the lanes, but they should read "as of the drop", not blank.
  const frozenRadios = ref<RadioHealthInput[]>([])
  watch(
    [liveRadios, () => hub.state],
    ([v, state]) => {
      if (state === 'connected' || state === 'resyncing') frozenRadios.value = v
    },
    { immediate: true },
  )

  const input = computed<HealthInput>(() => {
    const live = hub.state === 'connected' || hub.state === 'resyncing'
    const s = status.value
    return {
      now: now.value,
      apiOffline: apiOffline.value,
      hubState: hub.state,
      hubDownSince: hub.downSince,
      hubEverConnected: hub.everConnected,
      radiosLoaded: radiosStore.loaded,
      radios: live ? liveRadios.value : frozenRadios.value,
      callsign: callsign.value,
      externalTncConnected: s?.externalTncConnected ?? false,
      lastRfPacketAt: hub.lastRfAt,
      aprsIs: s
        ? {
            state: s.aprsIsState,
            lastError: s.aprsIsLastError,
            serverName: s.aprsIsServerName,
            lastPacketAt: s.lastIsPacketAt
              ? Math.max(new Date(s.lastIsPacketAt).getTime(), hub.lastIsAt ?? 0)
              : hub.lastIsAt,
          }
        : null,
      rfSilentMs: ui.rfSilentMinutes * 60_000,
    }
  })

  const health = computed(() => deriveHealth(input.value))

  const setupSteps = computed(() =>
    deriveSetupSteps({
      callsign: callsign.value,
      homeSet: homeSet.value,
      radios: radiosStore.activeRadios.map((r) => ({
        name: r.name,
        modemEnabled: r.modem.modemEnabled,
        txEnabled: r.modem.txEnabled || !r.modem.modemEnabled,
        captureDevice: r.modem.modemCaptureDevice,
      })),
      pttTested: pttTested.value,
      aprsIsState: status.value?.aprsIsState ?? 0,
      aprsIsServerName: status.value?.aprsIsServerName ?? null,
    }),
  )
  const setupRequired = computed(() => setupSteps.value.filter((s) => !s.optional))
  const setupDone = computed(() => setupRequired.value.filter((s) => s.done).length)
  const setupComplete = computed(
    () => settingsLoaded.value && setupDone.value === setupRequired.value.length,
  )

  const banners = computed(() =>
    deriveBanners(input.value, health.value, {
      csmaMaxSeconds: CSMA_MAX_SECONDS,
      setupDone: setupDone.value,
      setupTotal: setupRequired.value.length,
      setupDismissed: ui.setupDismissed,
    }),
  )

  return {
    status,
    apiOffline,
    callsign,
    homeSet,
    home,
    settingsLoaded,
    now,
    health,
    banners,
    setupSteps,
    setupDone,
    setupRequired,
    setupComplete,
    pttTested,
    markPttTested,
    start,
    stop,
    refreshStatus,
    refreshSettings,
    reconnectAprsIs,
  }
})

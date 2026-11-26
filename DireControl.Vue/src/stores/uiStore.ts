import { defineStore } from 'pinia'
import { ref, watch } from 'vue'

const RF_SILENT_KEY = 'direcontrol.rfSilentMinutes'
const SETUP_DISMISSED_KEY = 'direcontrol.setupChecklistDismissed'
const NAV_COLLAPSED_KEY = 'direcontrol.navCollapsed'

/** Default "RF silent" threshold (open question default: 15 min, configurable). */
export const DEFAULT_RF_SILENT_MINUTES = 15

function readNumber(key: string, fallback: number): number {
  try {
    const v = Number(localStorage.getItem(key))
    return Number.isFinite(v) && v > 0 ? v : fallback
  } catch {
    return fallback
  }
}

function readFlag(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    // Private window etc. — the choice lasts for this page load.
  }
}

/** Per-browser UI preferences and cross-component UI signals. */
export const useUiStore = defineStore('ui', () => {
  const pendingComposeOpen = ref(false)

  function triggerCompose() {
    pendingComposeOpen.value = true
  }

  function consumeCompose() {
    pendingComposeOpen.value = false
  }

  /** Minutes without an RF decode (modem running) before RF counts as silent. */
  const rfSilentMinutes = ref(readNumber(RF_SILENT_KEY, DEFAULT_RF_SILENT_MINUTES))
  watch(rfSilentMinutes, (v) => write(RF_SILENT_KEY, String(v)))

  /** The first-run banner/chip was hidden; /setup stays reachable. */
  const setupDismissed = ref(readFlag(SETUP_DISMISSED_KEY))
  watch(setupDismissed, (v) => write(SETUP_DISMISSED_KEY, v ? '1' : null))

  /** Desktop rail collapsed to icons only. */
  const navCollapsed = ref(readFlag(NAV_COLLAPSED_KEY))
  watch(navCollapsed, (v) => write(NAV_COLLAPSED_KEY, v ? '1' : null))

  /** The transmit queue sheet (opened from the TX chip and toasts). */
  const txSheetOpen = ref(false)

  return {
    pendingComposeOpen,
    txSheetOpen,
    triggerCompose,
    consumeCompose,
    rfSilentMinutes,
    setupDismissed,
    navCollapsed,
  }
})

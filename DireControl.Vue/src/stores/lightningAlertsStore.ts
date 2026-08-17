import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { LightningAlertDto, TriggeringStrike } from '@/types/alert'
import { usePacketHubStore } from '@/stores/packetHub'
import { useToastStore } from '@/stores/toastStore'
import { useUnits } from '@/composables/useUnits'
import { playAlertSound } from '@/utils/alertSound'

const CARDINALS = [
  'N',
  'NNE',
  'NE',
  'ENE',
  'E',
  'ESE',
  'SE',
  'SSE',
  'S',
  'SSW',
  'SW',
  'WSW',
  'W',
  'WNW',
  'NW',
  'NNW',
]

export function cardinal(bearingDegrees: number): string {
  const normalized = ((bearingDegrees % 360) + 360) % 360
  return CARDINALS[Math.round(normalized / 22.5) % 16]!
}

/** How stale the strike already was when the alert arrived, e.g. "4s ago". */
export function formatStrikeAge(feedLagSeconds: number): string {
  const seconds = Math.max(0, Math.round(feedLagSeconds))
  if (seconds < 1) return 'just now'
  if (seconds < 90) return `${seconds}s ago`
  return `${Math.round(seconds / 60)}m ago`
}

/**
 * How long a triggering strike stays marked on the map. Short on purpose — the marker
 * calls out a strike worth reacting to now, and a storm would otherwise leave a trail of
 * stale markers behind it. The ordinary lightning layer still shows the strike itself.
 */
const TRIGGER_TTL_MS = 10 * 60 * 1000
const TRIGGER_PRUNE_MS = 15_000
const MAX_TRIGGERS = 25

export const useLightningAlertsStore = defineStore('lightningAlerts', () => {
  const toastStore = useToastStore()
  const { formatDistance } = useUnits()

  /**
   * Strikes that actually fired an alert, oldest first. Held here rather than in the
   * map so they survive navigation and stay independent of the lightning layer's own
   * live/playback rendering.
   */
  const triggeringStrikes = ref<TriggeringStrike[]>([])

  function strikeKey(dto: LightningAlertDto): string {
    return `${dto.strikeTimeUtc}|${dto.latitude.toFixed(4)}|${dto.longitude.toFixed(4)}`
  }

  function onLightningAlert(dto: LightningAlertDto) {
    // The same strike reaching us twice (a hub reconnect replay, a second broadcast)
    // must not toast or sound twice.
    if (triggeringStrikes.value.some((s) => s.key === strikeKey(dto))) return

    // Kept terse so it stays on one line in the toast, which is 360 px wide.
    const message =
      `Lightning ${formatDistance(dto.distanceKm)} ${cardinal(dto.bearingDegrees)}` +
      ` · ${formatStrikeAge(dto.feedLagSeconds)}`
    recordTrigger(dto)
    toastStore.toast(message, 'warning', 10000)
    showBrowserNotification(message)
    playAlertSound()
  }

  function recordTrigger(dto: LightningAlertDto) {
    const key = strikeKey(dto)
    const kept = triggeringStrikes.value.filter((s) => s.key !== key)
    kept.push({ ...dto, key, alertedAt: Date.now() })
    triggeringStrikes.value = kept.slice(-MAX_TRIGGERS)
  }

  function dismissTrigger(key: string) {
    triggeringStrikes.value = triggeringStrikes.value.filter((s) => s.key !== key)
  }

  function clearTriggers() {
    triggeringStrikes.value = []
  }

  function pruneTriggers() {
    const cutoff = Date.now() - TRIGGER_TTL_MS
    const kept = triggeringStrikes.value.filter((s) => s.alertedAt >= cutoff)
    if (kept.length !== triggeringStrikes.value.length) triggeringStrikes.value = kept
  }

  function showBrowserNotification(body: string) {
    if (!('Notification' in window)) return
    if (Notification.permission === 'granted') {
      new Notification('DireControl — Lightning', { body, icon: '/favicon.ico' })
    } else if (Notification.permission === 'default') {
      Notification.requestPermission().then((permission) => {
        if (permission === 'granted') {
          new Notification('DireControl — Lightning', { body, icon: '/favicon.ico' })
        }
      })
    }
  }

  // Store-level hub subscription — lightning alerts arrive on any screen.
  const hub = usePacketHubStore()
  hub.on('lightningAlert', (dto: LightningAlertDto) => onLightningAlert(dto))

  setInterval(pruneTriggers, TRIGGER_PRUNE_MS)

  return { onLightningAlert, triggeringStrikes, dismissTrigger, clearTriggers, pruneTriggers }
})

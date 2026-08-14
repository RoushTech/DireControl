import { defineStore } from 'pinia'
import type { LightningAlertDto } from '@/types/alert'
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

function cardinal(bearingDegrees: number): string {
  const normalized = ((bearingDegrees % 360) + 360) % 360
  return CARDINALS[Math.round(normalized / 22.5) % 16]!
}

export const useLightningAlertsStore = defineStore('lightningAlerts', () => {
  const toastStore = useToastStore()
  const { formatDistance } = useUnits()

  function onLightningAlert(dto: LightningAlertDto) {
    const message = `Lightning strike ${formatDistance(dto.distanceKm)} ${cardinal(dto.bearingDegrees)} of station`
    toastStore.toast(message, 'warning', 10000)
    showBrowserNotification(message)
    playAlertSound()
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

  return { onLightningAlert }
})

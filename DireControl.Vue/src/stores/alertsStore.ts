import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { createHubConnection } from '@/composables/useSignalR'
import { getAlerts, acknowledgeAlert } from '@/api/alertsApi'
import type { AlertDto, AlertBroadcastDto } from '@/types/alert'
import { ALERT_TYPE_COLORS } from '@/types/alert'

export interface Toast {
  id: number
  message: string
  color: string
  show: boolean
}

let toastSeq = 0

export const useAlertsStore = defineStore('alerts', () => {
  const alerts = ref<AlertDto[]>([])
  const toasts = ref<Toast[]>([])
  const loading = ref(false)
  let connectionStarted = false

  const unacknowledgedCount = computed(() => alerts.value.filter((a) => !a.isAcknowledged).length)

  async function fetchAlerts() {
    loading.value = true
    try {
      alerts.value = await getAlerts()
    } finally {
      loading.value = false
    }
  }

  async function acknowledge(id: number) {
    await acknowledgeAlert(id)
    const alert = alerts.value.find((a) => a.id === id)
    if (alert) alert.isAcknowledged = true
  }

  function onAlertReceived(dto: AlertBroadcastDto) {
    // Add to alerts list as a provisional entry (will be refreshed on next fetch)
    const provisional: AlertDto = {
      id: dto.id,
      alertType: 0,
      alertTypeName: dto.alertTypeName,
      callsign: dto.callsign,
      triggeredAt: dto.triggeredAt,
      isAcknowledged: false,
      distanceMeters: dto.distanceMeters ?? null,
      geofenceName: dto.geofenceName ?? null,
      direction: dto.direction ?? null,
      ruleName: dto.ruleName ?? null,
    }
    alerts.value.unshift(provisional)

    const message = buildToastMessage(dto)
    showToast(message, ALERT_TYPE_COLORS[dto.alertTypeName] ?? 'info')

    showBrowserNotification(dto.alertTypeName, message)
  }

  function showToast(message: string, color: string) {
    const id = ++toastSeq
    toasts.value.push({ id, message, color, show: true })
    setTimeout(() => dismissToast(id), 6000)
  }

  function dismissToast(id: number) {
    const t = toasts.value.find((t) => t.id === id)
    if (t) t.show = false
    // Clean up after animation
    setTimeout(() => {
      const idx = toasts.value.findIndex((t) => t.id === id)
      if (idx !== -1) toasts.value.splice(idx, 1)
    }, 400)
  }

  function showBrowserNotification(type: string, body: string) {
    if (!('Notification' in window)) return
    if (Notification.permission === 'granted') {
      new Notification(`DireControl — ${type}`, { body, icon: '/favicon.ico' })
    } else if (Notification.permission === 'default') {
      Notification.requestPermission().then((permission) => {
        if (permission === 'granted') {
          new Notification(`DireControl — ${type}`, { body, icon: '/favicon.ico' })
        }
      })
    }
  }

  function startSignalR() {
    if (connectionStarted) return
    connectionStarted = true

    const hub = createHubConnection(
      '/hubs/packets',
      { alertReceived: (dto: AlertBroadcastDto) => onAlertReceived(dto) },
      { retryForever: true },
    )
    hub.start()
  }

  return {
    alerts,
    toasts,
    loading,
    unacknowledgedCount,
    fetchAlerts,
    acknowledge,
    onAlertReceived,
    dismissToast,
    startSignalR,
  }
})

function buildToastMessage(dto: AlertBroadcastDto): string {
  switch (dto.alertTypeName) {
    case 'WatchList':
      return `${dto.callsign} came online`
    case 'Proximity':
      return dto.ruleName
        ? `${dto.callsign} entered proximity zone "${dto.ruleName}"`
        : `${dto.callsign} entered proximity zone`
    case 'Geofence':
      return `${dto.callsign} ${dto.direction} geofence "${dto.geofenceName}"`
    default:
      return `Alert: ${dto.alertTypeName} from ${dto.callsign}`
  }
}

import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { getAlerts, acknowledgeAlert } from '@/api/alertsApi'
import type { AlertDto, AlertBroadcastDto } from '@/types/alert'
import { ALERT_TYPE_COLORS } from '@/types/alert'
import { usePacketHubStore } from '@/stores/packetHub'
import { useToastStore } from '@/stores/toastStore'

export const useAlertsStore = defineStore('alerts', () => {
  const toastStore = useToastStore()

  const alerts = ref<AlertDto[]>([])
  const loading = ref(false)

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
      messageText: null,
    }
    alerts.value.unshift(provisional)

    const message = buildToastMessage(dto)
    toastStore.toast(message, ALERT_TYPE_COLORS[dto.alertTypeName] ?? 'info')
    showBrowserNotification(dto.alertTypeName, message)
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

  // Store-level hub subscription — alerts arrive on any screen.
  const hub = usePacketHubStore()
  hub.on('alertReceived', (dto: AlertBroadcastDto) => onAlertReceived(dto))

  return {
    alerts,
    loading,
    unacknowledgedCount,
    fetchAlerts,
    acknowledge,
    onAlertReceived,
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
    case 'NewMessage':
      return `New message from ${dto.callsign}`
    default:
      return `Alert: ${dto.alertTypeName} from ${dto.callsign}`
  }
}

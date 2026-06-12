import { defineStore } from 'pinia'
import { ref, watch } from 'vue'
import { createHubConnection } from '@/composables/useSignalR'
import { getStatus } from '@/api/statusApi'
import { PacketSource, type PacketBroadcastDto } from '@/types/packet'

interface AprsIsStateDto {
  state: string
  serverName: string | null
  activeFilter: string
  sessionPacketCount: number
}

interface DirewolfStateDto {
  connected: boolean
}

/**
 * Backend connectivity state, pushed over SignalR instead of polled: Direwolf
 * and APRS-IS state changes arrive as events, and the hub connection itself is
 * the API-reachability signal.
 */
export const useStatusStore = defineStore('status', () => {
  const apiOffline = ref(false)
  const direwolfDisconnected = ref(false)
  const aprsIsState = ref('Disabled')
  const aprsIsServerName = ref<string | null>(null)
  const aprsIsFilter = ref('')
  const aprsIsSessionPacketCount = ref(0)
  let started = false

  async function refresh() {
    try {
      const status = await getStatus()
      apiOffline.value = false
      direwolfDisconnected.value = !status.direwolfConnected
      aprsIsState.value = status.aprsIsState
      aprsIsServerName.value = status.aprsIsServerName
      aprsIsFilter.value = status.aprsIsFilter
      aprsIsSessionPacketCount.value = status.aprsIsSessionPacketCount
    } catch {
      apiOffline.value = true
      direwolfDisconnected.value = false
    }
  }

  function start() {
    if (started) return
    started = true

    const hub = createHubConnection(
      '/hubs/packets',
      {
        aprsIsStateChanged: (dto: AprsIsStateDto) => {
          aprsIsState.value = dto.state
          aprsIsServerName.value = dto.serverName
          aprsIsFilter.value = dto.activeFilter
          aprsIsSessionPacketCount.value = dto.sessionPacketCount
        },
        direwolfStateChanged: (dto: DirewolfStateDto) => {
          direwolfDisconnected.value = !dto.connected
        },
        // Keep the session packet counter live between state events.
        packetReceived: (packet: PacketBroadcastDto) => {
          if (packet.source === PacketSource.AprsIs) aprsIsSessionPacketCount.value++
        },
      },
      { retryForever: true },
    )

    // Re-sync once on every (re)connect; losing the hub means the API is gone.
    watch(hub.status, (s) => {
      if (s === 'connected') refresh()
      else if (s === 'disconnected') apiOffline.value = true
    })

    hub.start()
    refresh()
  }

  return {
    apiOffline,
    direwolfDisconnected,
    aprsIsState,
    aprsIsServerName,
    aprsIsFilter,
    aprsIsSessionPacketCount,
    start,
    refresh,
  }
})

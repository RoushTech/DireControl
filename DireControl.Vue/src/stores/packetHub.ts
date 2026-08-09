import { defineStore } from 'pinia'
import { ref } from 'vue'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { serverNow } from '@/utils/serverTime'

export type PacketHubState = 'connecting' | 'connected' | 'disconnected'

// SignalR's own handler signature — a shared wrapper has to be untyped here;
// consumers keep their typed handlers.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type HubHandler = (...args: any[]) => void

/**
 * The app's single connection to /hubs/packets. Every store and view registers
 * handlers here instead of building its own HubConnection — one socket, one
 * reconnect policy, one place the connection state is surfaced (the live pill).
 *
 * Handlers can be registered before start(); SignalR queues them. Views that
 * unmount must call off() with the same handler reference.
 */
export const usePacketHubStore = defineStore('packetHub', () => {
  const state = ref<PacketHubState>('connecting')
  /** serverNow() timestamp of the last packetReceived — "how alive is the band". */
  const lastPacketAt = ref<number | null>(null)

  let connection: HubConnection | null = null
  let retryTimer: ReturnType<typeof setTimeout> | null = null

  function ensureConnection(): HubConnection {
    if (connection) return connection
    connection = new HubConnectionBuilder()
      .withUrl('/hubs/packets')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.onreconnecting(() => {
      state.value = 'connecting'
    })
    connection.onreconnected(() => {
      state.value = 'connected'
    })
    connection.onclose(() => {
      // Automatic reconnect has given up (it stops after its backoff sequence) —
      // keep trying forever; a dashboard can sit open for days.
      state.value = 'disconnected'
      scheduleRetry()
    })

    connection.on('packetReceived', () => {
      lastPacketAt.value = serverNow()
    })

    // ModemStatusBroadcaster pushes these to every client at up to 10 Hz, and
    // SignalR logs a console warning for each message that has no handler.
    // Permanent no-ops keep pages that don't consume the streams quiet; real
    // consumers register their own handlers alongside these.
    const noop = () => {}
    connection.on('modemLevel', noop)
    connection.on('modemSpectrum', noop)
    connection.on('modemStatusChanged', noop)
    return connection
  }

  function scheduleRetry() {
    if (retryTimer) return
    retryTimer = setTimeout(() => {
      retryTimer = null
      void start()
    }, 5000)
  }

  async function start() {
    const conn = ensureConnection()
    if (conn.state !== HubConnectionState.Disconnected) return
    state.value = 'connecting'
    try {
      await conn.start()
      state.value = 'connected'
    } catch {
      state.value = 'disconnected'
      scheduleRetry()
    }
  }

  function on(method: string, handler: HubHandler) {
    ensureConnection().on(method, handler)
  }

  function off(method: string, handler: HubHandler) {
    connection?.off(method, handler)
  }

  return { state, lastPacketAt, start, on, off }
})

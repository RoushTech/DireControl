import { defineStore } from 'pinia'
import { ref } from 'vue'
import { HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { createHubConnection } from '@/api/hubConnection'
import { serverNow } from '@/utils/serverTime'
import { PacketSource, type PacketBroadcastDto } from '@/types/packet'

export type PacketHubState = 'connecting' | 'connected' | 'disconnected' | 'resyncing'

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
  /** serverNow() timestamp of the last packetReceived, any source. */
  const lastPacketAt = ref<number | null>(null)
  /**
   * Last APRS-IS packet, kept apart from RF: internet traffic flowing says
   * nothing about whether the radio is hearing anything.  RF health comes from
   * the modem's own last-decode time instead.
   */
  const lastAprsIsPacketAt = ref<number | null>(null)

  let connection: HubConnection | null = null
  // Events sent while the socket was down are gone for good; stores register
  // here to refetch their state once the connection is back.
  const reconnectHandlers: Array<() => Promise<unknown> | void> = []
  let everConnected = false

  async function runResync() {
    if (reconnectHandlers.length === 0) {
      state.value = 'connected'
      return
    }
    state.value = 'resyncing'
    await Promise.allSettled(reconnectHandlers.map((h) => Promise.resolve().then(h)))
    if (state.value === 'resyncing') state.value = 'connected'
  }

  /** Registers a refetch to run after every reconnect (not the first connect). */
  function onReconnected(handler: () => Promise<unknown> | void) {
    reconnectHandlers.push(handler)
  }
  let retryTimer: ReturnType<typeof setTimeout> | null = null

  function ensureConnection(): HubConnection {
    if (connection) return connection
    connection = createHubConnection('/hubs/packets')

    connection.onreconnecting(() => {
      state.value = 'connecting'
    })
    connection.onreconnected(() => {
      void runResync()
    })
    connection.onclose(() => {
      // Automatic reconnect has given up (it stops after its backoff sequence) —
      // keep trying forever; a dashboard can sit open for days.
      state.value = 'disconnected'
      scheduleRetry()
    })

    connection.on('packetReceived', (p: PacketBroadcastDto) => {
      const at = serverNow()
      lastPacketAt.value = at
      if (p?.source === PacketSource.AprsIs) lastAprsIsPacketAt.value = at
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
      // A fresh start after onclose is a reconnect too — resync the same way.
      if (everConnected) void runResync()
      else state.value = 'connected'
      everConnected = true
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

  return { state, lastPacketAt, lastAprsIsPacketAt, start, on, off, onReconnected }
})

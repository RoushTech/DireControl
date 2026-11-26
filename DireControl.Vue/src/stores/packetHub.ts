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
  /**
   * serverNow() of the last packet per source. RF and APRS-IS are kept apart —
   * internet traffic flowing says nothing about whether the radio is hearing
   * anything — and our own transmissions (OWN) count as neither.
   */
  const lastRfAt = ref<number | null>(null)
  const lastIsAt = ref<number | null>(null)
  /** serverNow() when the feed dropped; null while connected. */
  const downSince = ref<number | null>(null)
  /** The feed has connected at least once this page load. */
  const everConnectedRef = ref(false)
  /** What the last resync refetched, for the "resynced" toast. */
  const lastResync = ref<{ at: number; ok: string[]; failed: string[] } | null>(null)

  let connection: HubConnection | null = null
  // Events sent while the socket was down are gone for good; stores register
  // here to refetch their state once the connection is back.
  const reconnectHandlers: Array<{ name: string; run: () => Promise<unknown> | void }> = []
  let everConnected = false

  async function runResync() {
    downSince.value = null
    if (reconnectHandlers.length === 0) {
      state.value = 'connected'
      return
    }
    state.value = 'resyncing'
    const results = await Promise.allSettled(
      reconnectHandlers.map((h) => Promise.resolve().then(h.run)),
    )
    lastResync.value = {
      at: serverNow(),
      ok: reconnectHandlers.filter((_, i) => results[i]!.status === 'fulfilled').map((h) => h.name),
      failed: reconnectHandlers
        .filter((_, i) => results[i]!.status === 'rejected')
        .map((h) => h.name),
    }
    if (state.value === 'resyncing') state.value = 'connected'
  }

  /**
   * Registers a refetch to run after every reconnect (not the first connect).
   * `name` is what the resync toast lists ("messages", "alerts", …).
   */
  function onReconnected(handler: () => Promise<unknown> | void, name = 'data') {
    reconnectHandlers.push({ name, run: handler })
  }

  function markDown() {
    if (downSince.value === null && everConnected) downSince.value = serverNow()
  }
  let retryTimer: ReturnType<typeof setTimeout> | null = null

  function ensureConnection(): HubConnection {
    if (connection) return connection
    connection = createHubConnection('/hubs/packets')

    connection.onreconnecting(() => {
      state.value = 'connecting'
      markDown()
    })
    connection.onreconnected(() => {
      void runResync()
    })
    connection.onclose(() => {
      // Automatic reconnect has given up (it stops after its backoff sequence) —
      // keep trying forever; a dashboard can sit open for days.
      state.value = 'disconnected'
      markDown()
      scheduleRetry()
    })

    connection.on('packetReceived', (p: PacketBroadcastDto) => {
      const at = serverNow()
      if (p?.source === PacketSource.AprsIs) lastIsAt.value = at
      else if (p?.source === PacketSource.Rf) lastRfAt.value = at
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

  /** Skip the backoff and try the feed now (banner "Reconnect now"). */
  async function reconnectNow() {
    if (retryTimer) {
      clearTimeout(retryTimer)
      retryTimer = null
    }
    if (connection && connection.state !== HubConnectionState.Disconnected) {
      // Mid automatic-reconnect: restart the attempt instead of waiting out the backoff.
      await connection.stop().catch(() => {})
    }
    await start()
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
      everConnectedRef.value = true
    } catch {
      state.value = 'disconnected'
      markDown()
      scheduleRetry()
    }
  }

  function on(method: string, handler: HubHandler) {
    ensureConnection().on(method, handler)
  }

  function off(method: string, handler: HubHandler) {
    connection?.off(method, handler)
  }

  return {
    state,
    lastRfAt,
    lastIsAt,
    downSince,
    everConnected: everConnectedRef,
    lastResync,
    start,
    reconnectNow,
    on,
    off,
    onReconnected,
  }
})

import { ref, type Ref } from 'vue'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'

export type HubStatus = 'connecting' | 'connected' | 'disconnected'

type HubHandler = Parameters<HubConnection['on']>[1]

export interface HubHandle {
  connection: HubConnection
  status: Ref<HubStatus>
  start: () => Promise<void>
  stop: () => Promise<void>
}

/**
 * Shared SignalR connection setup: auto-reconnect, warning-level client logging,
 * and a status ref driven by the connection lifecycle.
 *
 * `retryForever` is for app-lifetime store connections that are never stopped:
 * a failed initial start is retried every 5 s, and the connection is restarted
 * if auto-reconnect gives up.
 */
export function createHubConnection(
  hubUrl: string,
  handlers: Record<string, HubHandler>,
  { retryForever = false } = {},
): HubHandle {
  const status = ref<HubStatus>('connecting')
  let stopped = false

  const connection = new HubConnectionBuilder()
    .withUrl(hubUrl)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  for (const [method, handler] of Object.entries(handlers)) {
    connection.on(method, handler)
  }

  connection.onreconnecting(() => {
    status.value = 'connecting'
  })
  connection.onreconnected(() => {
    status.value = 'connected'
  })
  connection.onclose(() => {
    status.value = 'disconnected'
    if (retryForever && !stopped && connection.state !== HubConnectionState.Reconnecting) {
      setTimeout(start, 5000)
    }
  })

  async function start() {
    if (stopped) return
    status.value = 'connecting'
    try {
      await connection.start()
      status.value = 'connected'
    } catch {
      status.value = 'disconnected'
      if (retryForever) setTimeout(start, 5000)
    }
  }

  async function stop() {
    stopped = true
    await connection.stop()
  }

  return { connection, status, start, stop }
}

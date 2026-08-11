import { defineStore } from 'pinia'
import { ref } from 'vue'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import {
  base64ToBytes,
  bytesToBase64,
  closeSession as apiCloseSession,
  getSessions,
  openLocalPmsSession,
  openSession as apiOpenSession,
  type OpenTerminalSessionRequest,
  type TerminalSessionDto,
  type TerminalSessionStatsDto,
} from '@/api/terminalApi'

export type TerminalHubState = 'connecting' | 'connected' | 'disconnected'

/** Raw-byte output handler — bytes are NOT reactive; xterm's buffer is the store. */
export type TerminalOutputHandler = (seq: number, bytes: Uint8Array) => void

interface TerminalOutputMessage {
  sessionId: string
  seq: number
  dataBase64: string
}

interface TerminalBacklogMessage {
  sessionId: string
  startSeq: number
  dataBase64: string
}

interface TerminalStatsMessage {
  sessionId: string
  stats: TerminalSessionStatsDto
}

/**
 * The app's single connection to /hubs/terminal plus the session list. Terminal
 * byte streams are delivered through a non-reactive pub/sub — subscribers (the
 * xterm panes) get raw Uint8Arrays and keep their own scrollback; no packet
 * data ever lands in reactive state.
 */
export const useTerminalStore = defineStore('terminal', () => {
  const hubState = ref<TerminalHubState>('disconnected')
  const sessions = ref<TerminalSessionDto[]>([])
  const stats = ref<Record<string, TerminalSessionStatsDto>>({})

  let connection: HubConnection | null = null
  let retryTimer: ReturnType<typeof setTimeout> | null = null
  // Deliberately non-reactive: handlers receive raw bytes.
  const outputHandlers = new Map<string, Set<TerminalOutputHandler>>()

  function deliver(sessionId: string, seq: number, dataBase64: string) {
    const handlers = outputHandlers.get(sessionId)
    if (!handlers || handlers.size === 0) return
    const bytes = base64ToBytes(dataBase64)
    for (const handler of handlers) handler(seq, bytes)
  }

  function upsertSession(dto: TerminalSessionDto) {
    const idx = sessions.value.findIndex((s) => s.id === dto.id)
    if (idx !== -1) sessions.value[idx] = dto
    else sessions.value.push(dto)
    stats.value[dto.id] = dto.stats
  }

  function ensureConnection(): HubConnection {
    if (connection) return connection
    connection = new HubConnectionBuilder()
      .withUrl('/hubs/terminal')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.onreconnecting(() => {
      hubState.value = 'connecting'
    })
    connection.onreconnected(() => {
      hubState.value = 'connected'
      // A new socket has no group memberships — re-join every subscribed
      // session. The re-sent backlog gap-fills panes via their seq dedup.
      for (const sessionId of outputHandlers.keys()) {
        connection?.invoke('JoinSession', sessionId).catch(() => {})
      }
      void refreshSessions()
    })
    connection.onclose(() => {
      // Automatic reconnect gave up — keep trying forever, like packetHub.
      hubState.value = 'disconnected'
      scheduleRetry()
    })

    connection.on('terminalOutput', (msg: TerminalOutputMessage) => {
      deliver(msg.sessionId, msg.seq, msg.dataBase64)
    })
    connection.on('terminalBacklog', (msg: TerminalBacklogMessage) => {
      deliver(msg.sessionId, msg.startSeq, msg.dataBase64)
    })
    connection.on('terminalStateChanged', (dto: TerminalSessionDto) => {
      upsertSession(dto)
    })
    connection.on('terminalStats', (msg: TerminalStatsMessage) => {
      stats.value[msg.sessionId] = msg.stats
    })
    connection.on('terminalSessionsChanged', () => {
      void refreshSessions()
    })
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
    hubState.value = 'connecting'
    try {
      await conn.start()
      hubState.value = 'connected'
      // Sessions the panes subscribed to while the socket was down.
      for (const sessionId of outputHandlers.keys()) {
        conn.invoke('JoinSession', sessionId).catch(() => {})
      }
    } catch {
      hubState.value = 'disconnected'
      scheduleRetry()
    }
  }

  /** Starts the hub lazily and resolves once it is connected (or throws). */
  async function ensureStarted(): Promise<HubConnection> {
    const conn = ensureConnection()
    if (conn.state === HubConnectionState.Disconnected) await start()
    if (conn.state !== HubConnectionState.Connected) {
      throw new Error('Terminal hub is not connected.')
    }
    return conn
  }

  // ── Output pub/sub ─────────────────────────────────────────────────────────

  function subscribeOutput(sessionId: string, handler: TerminalOutputHandler) {
    let handlers = outputHandlers.get(sessionId)
    if (!handlers) {
      handlers = new Set()
      outputHandlers.set(sessionId, handlers)
    }
    handlers.add(handler)
  }

  function unsubscribeOutput(sessionId: string, handler: TerminalOutputHandler) {
    const handlers = outputHandlers.get(sessionId)
    if (!handlers) return
    handlers.delete(handler)
    if (handlers.size === 0) outputHandlers.delete(sessionId)
  }

  // ── Hub calls ──────────────────────────────────────────────────────────────

  async function joinSession(sessionId: string) {
    try {
      const conn = await ensureStarted()
      await conn.invoke('JoinSession', sessionId)
    } catch {
      // Not connected — onreconnected/start re-joins from outputHandlers.
    }
  }

  async function leaveSession(sessionId: string) {
    if (connection?.state !== HubConnectionState.Connected) return
    try {
      await connection.invoke('LeaveSession', sessionId)
    } catch {
      /* session may already be gone */
    }
  }

  async function sendInput(sessionId: string, bytes: Uint8Array) {
    const conn = await ensureStarted()
    await conn.invoke('SendInput', sessionId, bytesToBase64(bytes))
  }

  // ── Session lifecycle (REST) ───────────────────────────────────────────────

  async function refreshSessions() {
    sessions.value = await getSessions()
    for (const s of sessions.value) stats.value[s.id] = s.stats
  }

  async function open(request: OpenTerminalSessionRequest): Promise<TerminalSessionDto> {
    const dto = await apiOpenSession(request)
    upsertSession(dto)
    return dto
  }

  async function openLocalPms(): Promise<TerminalSessionDto> {
    const dto = await openLocalPmsSession()
    upsertSession(dto)
    return dto
  }

  async function close(id: string, abort = false) {
    await apiCloseSession(id, abort)
    await refreshSessions().catch(() => {})
  }

  return {
    hubState,
    sessions,
    stats,
    start,
    subscribeOutput,
    unsubscribeOutput,
    joinSession,
    leaveSession,
    sendInput,
    refreshSessions,
    open,
    openLocalPms,
    close,
  }
})

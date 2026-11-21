import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { HubConnection } from '@microsoft/signalr'
import { createHubConnection } from '@/api/hubConnection'
import { LOG_LEVEL_RANK, type LogEntryDto } from '@/types/log'

// Hard cap on rows held in memory so a long-running stream can't grow unbounded.
const MAX_DISPLAYED = 2000

export const useLogStreamStore = defineStore('logStream', () => {
  const displayedLogs = ref<LogEntryDto[]>([])
  const pendingLogs = ref<LogEntryDto[]>([])
  const paused = ref(false)

  const levelFilter = ref<string>('') // '' = all levels
  const textFilter = ref('')

  const pendingCount = computed(() => pendingLogs.value.length)

  // ── /hubs/logs connection (opened while a log view is mounted) ─────────────
  const connectionStatus = ref<'connecting' | 'connected' | 'disconnected'>('disconnected')
  let connection: HubConnection | null = null

  async function connect() {
    if (connection) return
    connectionStatus.value = 'connecting'
    const conn = createHubConnection('/hubs/logs')
    connection = conn
    conn.on('logBacklog', (entries: LogEntryDto[]) => seedBacklog(entries))
    conn.on('logReceived', (entry: LogEntryDto) => addLog(entry))
    conn.onreconnecting(() => {
      connectionStatus.value = 'connecting'
    })
    conn.onreconnected(() => {
      connectionStatus.value = 'connected'
    })
    conn.onclose(() => {
      connectionStatus.value = 'disconnected'
    })
    try {
      await conn.start()
      connectionStatus.value = 'connected'
    } catch {
      connectionStatus.value = 'disconnected'
    }
  }

  async function disconnect() {
    const conn = connection
    connection = null
    if (conn) await conn.stop()
    connectionStatus.value = 'disconnected'
  }

  function addLog(entry: LogEntryDto) {
    const target = paused.value ? pendingLogs : displayedLogs
    target.value.unshift(entry)
    if (target.value.length > MAX_DISPLAYED) target.value.splice(MAX_DISPLAYED)
  }

  /**
   * Seed from the server ring-buffer backlog (delivered oldest-first). Merged by
   * sequence with anything already received live so the race between the backlog
   * message and the first live entries can't produce duplicates.
   */
  function seedBacklog(backlog: LogEntryDto[]) {
    const bySeq = new Map<number, LogEntryDto>()
    for (const e of backlog) bySeq.set(e.sequence, e)
    for (const e of displayedLogs.value) bySeq.set(e.sequence, e)
    displayedLogs.value = [...bySeq.values()]
      .sort((a, b) => b.sequence - a.sequence) // newest first
      .slice(0, MAX_DISPLAYED)
  }

  function pause() {
    paused.value = true
  }

  function unpause() {
    paused.value = false
    if (pendingLogs.value.length > 0) {
      displayedLogs.value = [...pendingLogs.value, ...displayedLogs.value].slice(0, MAX_DISPLAYED)
      pendingLogs.value = []
    }
  }

  function clear() {
    displayedLogs.value = []
    pendingLogs.value = []
  }

  const filteredLogs = computed(() => {
    let list = displayedLogs.value
    const lf = levelFilter.value
    if (lf) {
      const min = LOG_LEVEL_RANK[lf] ?? 0
      list = list.filter((e) => (LOG_LEVEL_RANK[e.level] ?? 0) >= min)
    }
    const tx = textFilter.value.trim().toLowerCase()
    if (tx) {
      list = list.filter(
        (e) => e.message.toLowerCase().includes(tx) || e.category.toLowerCase().includes(tx),
      )
    }
    return list
  })

  return {
    displayedLogs,
    pendingLogs,
    pendingCount,
    paused,
    levelFilter,
    textFilter,
    filteredLogs,
    addLog,
    seedBacklog,
    connectionStatus,
    connect,
    disconnect,
    pause,
    unpause,
    clear,
  }
})

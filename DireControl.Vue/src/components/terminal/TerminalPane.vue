<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useTheme } from 'vuetify'
import { Terminal, type ITheme } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { createCrlfNormalizer } from '@/api/terminalApi'
import { useTerminalStore } from '@/stores/terminalStore'

export type TerminalPhosphorTheme = 'classic' | 'green' | 'amber'

const props = withDefaults(
  defineProps<{
    sessionId: string
    localEcho?: boolean
    phosphorTheme?: TerminalPhosphorTheme
  }>(),
  {
    localEcho: false,
    phosphorTheme: 'classic',
  },
)

const store = useTerminalStore()
const vuetifyTheme = useTheme()
const container = ref<HTMLElement | null>(null)

let term: Terminal | null = null
let fitAddon: FitAddon | null = null
let resizeObserver: ResizeObserver | null = null

// ── Byte-stream seq dedup ────────────────────────────────────────────────────
// The backlog (on join/rejoin) and live output both arrive through the same
// subscription with byte-stream offsets; overlap is dropped so a reconnect
// never double-prints, and a backlog that starts past what we've seen gap-fills.
let nextExpectedSeq: number | null = null

// Bare-CR packet line endings would overwrite lines in xterm — normalize the
// received stream to CRLF (stateful across chunk boundaries).
const normalizeForDisplay = createCrlfNormalizer()

function onOutput(seq: number, bytes: Uint8Array) {
  if (!term) return
  const end = seq + bytes.length
  if (nextExpectedSeq === null) {
    // First delivery (normally the backlog) establishes the stream position.
    term.write(normalizeForDisplay(bytes))
    nextExpectedSeq = end
    return
  }
  if (end <= nextExpectedSeq) return // entirely already written
  if (seq < nextExpectedSeq) {
    term.write(normalizeForDisplay(bytes.subarray(nextExpectedSeq - seq))) // overlap — write only the tail
  } else {
    term.write(normalizeForDisplay(bytes))
  }
  nextExpectedSeq = end
}

// ── Theming ──────────────────────────────────────────────────────────────────

function currentTheme(): ITheme {
  switch (props.phosphorTheme) {
    case 'green':
      return {
        background: '#000000',
        foreground: '#33ff66',
        cursor: '#33ff66',
        cursorAccent: '#000000',
        selectionBackground: 'rgba(51, 255, 102, 0.3)',
      }
    case 'amber':
      return {
        background: '#000000',
        foreground: '#ffb000',
        cursor: '#ffb000',
        cursorAccent: '#000000',
        selectionBackground: 'rgba(255, 176, 0, 0.3)',
      }
    default:
      return vuetifyTheme.global.current.value.dark
        ? {
            background: '#0e1116',
            foreground: '#e6edf3',
            cursor: '#e6edf3',
            cursorAccent: '#0e1116',
            selectionBackground: 'rgba(230, 237, 243, 0.25)',
          }
        : {
            background: '#ffffff',
            foreground: '#1f2328',
            cursor: '#1f2328',
            cursorAccent: '#ffffff',
            selectionBackground: 'rgba(31, 35, 40, 0.2)',
          }
  }
}

function applyTheme() {
  if (term) term.options.theme = currentTheme()
}

watch([() => props.phosphorTheme, () => vuetifyTheme.global.current.value.dark], applyTheme)

// ── Lifecycle ────────────────────────────────────────────────────────────────

onMounted(() => {
  if (!container.value) return
  term = new Terminal({
    convertEol: false,
    cursorBlink: true,
    scrollback: 5000,
    fontSize: 13,
    fontFamily: "'JetBrains Mono', 'Fira Code', monospace",
    theme: currentTheme(),
  })
  fitAddon = new FitAddon()
  term.loadAddon(fitAddon)
  term.open(container.value)
  fitAddon.fit()

  // xterm sends '\r' for Enter — exactly what packet expects. No local echo by
  // default: connected-mode remotes echo; the optional toggle covers those
  // that don't.
  term.onData((data) => {
    if (props.localEcho && term) {
      let echo = ''
      for (const ch of data) {
        if (ch === '\r') echo += '\r\n'
        else if (ch >= ' ' || ch === '\t') echo += ch
      }
      if (echo) term.write(echo)
    }
    store.sendInput(props.sessionId, new TextEncoder().encode(data)).catch(() => {
      /* hub down — the session tab state chip shows the trouble */
    })
  })

  store.subscribeOutput(props.sessionId, onOutput)
  void store.joinSession(props.sessionId)

  resizeObserver = new ResizeObserver(() => {
    // fit() throws if called while the container has zero size (hidden tab).
    if (container.value && container.value.clientHeight > 0) fitAddon?.fit()
  })
  resizeObserver.observe(container.value)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
  store.unsubscribeOutput(props.sessionId, onOutput)
  void store.leaveSession(props.sessionId)
  term?.dispose()
  term = null
  fitAddon = null
})
</script>

<template>
  <div ref="container" class="terminal-pane" />
</template>

<style scoped>
.terminal-pane {
  height: 100%;
  width: 100%;
  min-height: 0;
  overflow: hidden;
  /* xterm paints its own background; pad inside the same color via the host. */
  padding: 4px;
  box-sizing: border-box;
}

.terminal-pane :deep(.xterm) {
  height: 100%;
}
</style>

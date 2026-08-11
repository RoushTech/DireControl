<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useTerminalStore } from '@/stores/terminalStore'
import { TerminalSessionStates, type TerminalSessionDto } from '@/api/terminalApi'
import { apiErrorDetail } from '@/api/axios'
import TerminalPane, { type TerminalPhosphorTheme } from '@/components/terminal/TerminalPane.vue'
import TerminalConnectForm from '@/components/terminal/TerminalConnectForm.vue'
import TerminalMacroBar from '@/components/terminal/TerminalMacroBar.vue'
import TerminalTelemetryPanel from '@/components/terminal/TerminalTelemetryPanel.vue'
import TerminalTranscriptBrowser from '@/components/terminal/TerminalTranscriptBrowser.vue'

defineOptions({ name: 'TerminalView' })

const store = useTerminalStore()

// 'new' is the trailing "+" tab; anything else is a session id.
const activeTab = ref<string>('new')

const activeSession = computed<TerminalSessionDto | null>(
  () => store.sessions.find((s) => s.id === activeTab.value) ?? null,
)

function stateDotColor(session: TerminalSessionDto): string {
  switch (session.state) {
    case TerminalSessionStates.Connecting:
      return 'orange'
    case TerminalSessionStates.Connected:
      return 'green'
    case TerminalSessionStates.Error:
      return 'red'
    default:
      return 'grey'
  }
}

// If the active session disappears (closed elsewhere), fall back sensibly.
watch(
  () => store.sessions.map((s) => s.id),
  (ids) => {
    if (activeTab.value !== 'new' && !ids.includes(activeTab.value)) {
      activeTab.value = ids[ids.length - 1] ?? 'new'
    }
  },
)

// ── Connect / close ──────────────────────────────────────────────────────────
const connecting = ref(false)
const connectError = ref<string | null>(null)
const pmsLoading = ref(false)

async function onOpen(request: Parameters<typeof store.open>[0]) {
  connecting.value = true
  connectError.value = null
  try {
    const dto = await store.open(request)
    activeTab.value = dto.id
  } catch (e: unknown) {
    connectError.value = apiErrorDetail(e)
  } finally {
    connecting.value = false
  }
}

async function onOpenPms() {
  pmsLoading.value = true
  connectError.value = null
  try {
    const dto = await store.openLocalPms()
    activeTab.value = dto.id
  } catch (e: unknown) {
    connectError.value = apiErrorDetail(e)
  } finally {
    pmsLoading.value = false
  }
}

const closing = ref<Record<string, boolean>>({})

async function closeTab(session: TerminalSessionDto) {
  closing.value[session.id] = true
  try {
    await store.close(session.id, false)
  } catch {
    /* refreshSessions on the next sessionsChanged shows the truth */
  } finally {
    delete closing.value[session.id]
  }
}

// ── View preferences ─────────────────────────────────────────────────────────
const THEME_STORAGE_KEY = 'direcontrol-terminal-theme'

const phosphorTheme = ref<TerminalPhosphorTheme>(
  (() => {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    return stored === 'green' || stored === 'amber' ? stored : 'classic'
  })(),
)
watch(phosphorTheme, (t) => localStorage.setItem(THEME_STORAGE_KEY, t))

const themeItems = [
  { title: 'Classic', value: 'classic' },
  { title: 'Green phosphor', value: 'green' },
  { title: 'Amber phosphor', value: 'amber' },
]

const localEcho = ref(false)
const telemetryOpen = ref(false)
const transcriptsOpen = ref(false)

// ── Macros ───────────────────────────────────────────────────────────────────
function onMacro(bytes: Uint8Array) {
  const session = activeSession.value
  if (!session) return
  store.sendInput(session.id, bytes).catch(() => {
    /* hub down — state chip already shows it */
  })
}

onMounted(async () => {
  void store.start()
  try {
    await store.refreshSessions()
  } catch {
    /* the hub's sessionsChanged will retry the fetch */
  }
  if (activeTab.value === 'new' && store.sessions.length > 0) {
    activeTab.value = store.sessions[0]!.id
  }
})
</script>

<template>
  <div class="terminal-view">
    <!-- Tab strip + toolbar -->
    <div class="terminal-tabbar">
      <v-tabs v-model="activeTab" density="compact" class="terminal-tabs" show-arrows>
        <v-tab v-for="session in store.sessions" :key="session.id" :value="session.id">
          <v-icon size="10" :color="stateDotColor(session)" class="mr-1">mdi-circle</v-icon>
          <span class="terminal-tab-label">{{ session.remoteCallsign }}</span>
          <!-- v-tab renders a <button>; a nested v-btn would be invalid HTML -->
          <v-icon
            size="14"
            class="ml-1 terminal-tab-close"
            :class="{ 'terminal-tab-close--busy': closing[session.id] }"
            title="Close session"
            @click.stop.prevent="closeTab(session)"
          >
            {{ closing[session.id] ? 'mdi-loading' : 'mdi-close' }}
          </v-icon>
        </v-tab>
        <v-tab value="new" title="New connection">
          <v-icon size="18">mdi-plus</v-icon>
        </v-tab>
      </v-tabs>

      <v-spacer />

      <div class="d-flex align-center ga-1 flex-shrink-0 pr-2">
        <v-chip
          v-if="store.hubState !== 'connected'"
          :color="store.hubState === 'connecting' ? 'warning' : 'error'"
          size="x-small"
          variant="tonal"
        >
          {{ store.hubState }}
        </v-chip>
        <v-select
          v-model="phosphorTheme"
          :items="themeItems"
          density="compact"
          variant="outlined"
          hide-details
          class="terminal-theme-select"
        />
        <v-btn
          size="small"
          variant="text"
          :color="localEcho ? 'primary' : undefined"
          title="Local echo"
          @click="localEcho = !localEcho"
        >
          Echo
        </v-btn>
        <v-btn
          icon="mdi-gauge"
          size="small"
          variant="text"
          :color="telemetryOpen ? 'primary' : undefined"
          title="Link telemetry"
          :disabled="!activeSession"
          @click="telemetryOpen = !telemetryOpen"
        />
        <v-btn
          icon="mdi-history"
          size="small"
          variant="text"
          title="Transcripts"
          @click="transcriptsOpen = true"
        />
        <v-btn
          icon="mdi-cog-outline"
          size="small"
          variant="text"
          title="Packet settings"
          to="/settings?tab=packet"
        />
      </div>
    </div>

    <v-divider />

    <!-- New-connection tab -->
    <div v-if="activeTab === 'new'" class="terminal-connect-wrap">
      <TerminalConnectForm
        :loading="connecting"
        :pms-loading="pmsLoading"
        :error="connectError"
        @open="onOpen"
        @open-pms="onOpenPms"
      />
    </div>

    <!-- Session panes — all kept alive (v-show) so scrollback survives tab switches -->
    <template v-for="session in store.sessions" :key="session.id">
      <div v-show="activeTab === session.id" class="terminal-session">
        <div class="terminal-session-main">
          <div class="terminal-pane-wrap">
            <TerminalPane
              :session-id="session.id"
              :local-echo="localEcho"
              :phosphor-theme="phosphorTheme"
            />
          </div>
          <TerminalTelemetryPanel
            v-if="telemetryOpen"
            :session="session"
            :stats="store.stats[session.id] ?? null"
          />
        </div>
        <v-divider />
        <TerminalMacroBar @macro="onMacro" />
      </div>
    </template>

    <TerminalTranscriptBrowser v-model="transcriptsOpen" />
  </div>
</template>

<style scoped>
.terminal-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.terminal-tabbar {
  display: flex;
  align-items: center;
  flex-shrink: 0;
}

.terminal-tabs {
  min-width: 0;
}

.terminal-tab-label {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-weight: 600;
  text-transform: none;
}

.terminal-tab-close {
  opacity: 0.6;
  border-radius: 50%;
}

.terminal-tab-close:hover {
  opacity: 1;
  background: rgba(var(--v-theme-on-surface), 0.12);
}

.terminal-tab-close--busy {
  animation: terminal-close-spin 0.8s linear infinite;
}

@keyframes terminal-close-spin {
  to {
    transform: rotate(360deg);
  }
}

.terminal-theme-select {
  width: 160px;
  flex-shrink: 0;
}

.terminal-connect-wrap {
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding: 32px 16px;
  overflow-y: auto;
}

.connect-form {
  width: 100%;
}

.terminal-session {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.terminal-session-main {
  flex: 1;
  min-height: 0;
  display: flex;
}

.terminal-pane-wrap {
  flex: 1;
  min-width: 0;
  min-height: 0;
}
</style>

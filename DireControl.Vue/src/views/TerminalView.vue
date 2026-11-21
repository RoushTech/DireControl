<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useDisplay } from 'vuetify'
import { useTerminalStore } from '@/stores/terminalStore'
import {
  TerminalSessionStates,
  terminalSessionStateLabels,
  type TerminalSessionDto,
} from '@/api/terminalApi'
import { apiErrorDetail } from '@/api/axios'
import TerminalPane, { type TerminalPhosphorTheme } from '@/components/terminal/TerminalPane.vue'
import TerminalConnectForm from '@/components/terminal/TerminalConnectForm.vue'
import TerminalMacroBar from '@/components/terminal/TerminalMacroBar.vue'
import TerminalTelemetryPanel from '@/components/terminal/TerminalTelemetryPanel.vue'
import TerminalTranscriptBrowser from '@/components/terminal/TerminalTranscriptBrowser.vue'

defineOptions({ name: 'TerminalView' })

const store = useTerminalStore()
// Below sm the toolbar folds into an overflow menu and telemetry becomes a
// bottom sheet, so the terminal itself keeps the width.
const { smAndDown } = useDisplay()

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

function isEnded(session: TerminalSessionDto): boolean {
  return (
    session.state === TerminalSessionStates.Disconnected ||
    session.state === TerminalSessionStates.Error
  )
}

function stateBannerType(session: TerminalSessionDto): 'success' | 'info' | 'warning' | 'error' {
  switch (session.state) {
    case TerminalSessionStates.Connected:
      return 'success'
    case TerminalSessionStates.Error:
      return 'error'
    case TerminalSessionStates.Disconnected:
      return 'warning'
    default:
      return 'info'
  }
}

async function doClose(session: TerminalSessionDto, abort: boolean) {
  closing.value[session.id] = true
  try {
    await store.close(session.id, abort)
  } catch {
    /* refreshSessions on the next sessionsChanged shows the truth */
  } finally {
    delete closing.value[session.id]
  }
}

// Disconnecting a live link is confirmed — the tab "x" is easy to hit.
const confirmCloseFor = ref<TerminalSessionDto | null>(null)

function closeTab(session: TerminalSessionDto) {
  if (session.state === TerminalSessionStates.Connected) confirmCloseFor.value = session
  else void doClose(session, false)
}

async function confirmClose(abort: boolean) {
  const session = confirmCloseFor.value
  confirmCloseFor.value = null
  if (session) await doClose(session, abort)
}

async function reconnect(session: TerminalSessionDto) {
  await onOpen({
    channel: session.channel,
    remoteCallsign: session.remoteCallsign,
    localCallsign: session.localCallsign,
    digiPath: session.digiPath ?? undefined,
  })
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
          <!-- v-tab renders a <button>, so a nested v-btn would be invalid HTML;
               a focusable role="button" icon keeps it keyboard-reachable. -->
          <v-icon
            size="14"
            class="ml-1 terminal-tab-close"
            :class="{ 'terminal-tab-close--busy': closing[session.id] }"
            role="button"
            tabindex="0"
            :aria-label="`Close session with ${session.remoteCallsign}`"
            title="Close session"
            @click.stop.prevent="closeTab(session)"
            @keydown.enter.stop.prevent="closeTab(session)"
            @keydown.space.stop.prevent="closeTab(session)"
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
        <v-menu v-if="smAndDown" location="bottom end">
          <template #activator="{ props: menuProps }">
            <v-btn
              v-bind="menuProps"
              icon="mdi-dots-vertical"
              size="small"
              variant="text"
              aria-label="Terminal options"
            />
          </template>
          <v-list density="compact">
            <v-list-subheader>Theme</v-list-subheader>
            <v-list-item
              v-for="t in themeItems"
              :key="t.value"
              :title="t.title"
              :active="phosphorTheme === t.value"
              @click="phosphorTheme = t.value as TerminalPhosphorTheme"
            />
            <v-divider />
            <v-list-item
              :title="localEcho ? 'Local echo: on' : 'Local echo: off'"
              prepend-icon="mdi-keyboard-outline"
              @click="localEcho = !localEcho"
            />
            <v-list-item
              title="Link telemetry"
              prepend-icon="mdi-gauge"
              :disabled="!activeSession"
              @click="telemetryOpen = !telemetryOpen"
            />
            <v-list-item
              title="Transcripts"
              prepend-icon="mdi-history"
              @click="transcriptsOpen = true"
            />
            <v-list-item
              title="Packet settings"
              prepend-icon="mdi-cog-outline"
              to="/settings/packet"
            />
          </v-list>
        </v-menu>
        <template v-else>
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
            aria-label="Link telemetry"
            :disabled="!activeSession"
            @click="telemetryOpen = !telemetryOpen"
          />
          <v-btn
            icon="mdi-history"
            size="small"
            variant="text"
            title="Transcripts"
            aria-label="Transcripts"
            @click="transcriptsOpen = true"
          />
          <v-btn
            icon="mdi-cog-outline"
            size="small"
            variant="text"
            title="Packet settings"
            aria-label="Packet settings"
            to="/settings/packet"
          />
        </template>
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
        <v-banner
          :color="stateBannerType(session)"
          density="compact"
          lines="one"
          class="terminal-state-banner"
        >
          <v-banner-text>
            <strong>{{ terminalSessionStateLabels[session.state] ?? 'Unknown' }}</strong>
            · {{ session.localCallsign }} ↔ {{ session.remoteCallsign }}
            <template v-if="session.digiPath"> via {{ session.digiPath }}</template>
            <template v-if="isEnded(session) && session.endReason">
              — {{ session.endReason }}
            </template>
          </v-banner-text>
          <template #actions>
            <template v-if="!isEnded(session)">
              <v-btn
                size="small"
                variant="text"
                :loading="closing[session.id]"
                @click="closeTab(session)"
              >
                Disconnect
              </v-btn>
              <v-btn
                size="small"
                variant="text"
                color="error"
                title="Drop the link immediately without a DISC handshake"
                @click="doClose(session, true)"
              >
                Abort
              </v-btn>
            </template>
            <template v-else>
              <v-btn size="small" variant="text" :loading="connecting" @click="reconnect(session)">
                Reconnect
              </v-btn>
              <v-btn size="small" variant="text" @click="doClose(session, false)">Close tab</v-btn>
            </template>
          </template>
        </v-banner>
        <div class="terminal-session-main">
          <div class="terminal-pane-wrap">
            <TerminalPane
              :session-id="session.id"
              :local-echo="localEcho"
              :phosphor-theme="phosphorTheme"
              :read-only="isEnded(session)"
            />
          </div>
          <TerminalTelemetryPanel
            v-if="telemetryOpen && !smAndDown"
            :session="session"
            :stats="store.stats[session.id] ?? null"
          />
        </div>
        <v-divider />
        <TerminalMacroBar @macro="onMacro" />
      </div>
    </template>

    <TerminalTranscriptBrowser v-model="transcriptsOpen" />

    <v-bottom-sheet
      v-if="smAndDown && activeSession"
      :model-value="telemetryOpen"
      @update:model-value="(v) => (telemetryOpen = v)"
    >
      <v-card>
        <TerminalTelemetryPanel
          :session="activeSession"
          :stats="store.stats[activeSession.id] ?? null"
          class="telemetry-sheet"
        />
      </v-card>
    </v-bottom-sheet>

    <v-dialog
      :model-value="confirmCloseFor !== null"
      max-width="420"
      @update:model-value="confirmCloseFor = null"
    >
      <v-card>
        <v-card-title class="text-subtitle-1">
          Disconnect from {{ confirmCloseFor?.remoteCallsign }}?
        </v-card-title>
        <v-card-text>
          The link is up. Disconnect sends DISC and waits for the remote to confirm; Abort drops it
          at once.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="confirmCloseFor = null">Keep connected</v-btn>
          <v-btn variant="text" color="error" @click="confirmClose(true)">Abort</v-btn>
          <v-btn variant="tonal" color="primary" @click="confirmClose(false)">Disconnect</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
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
  min-width: 0;
}

.telemetry-sheet {
  width: 100% !important;
  border-left: none !important;
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

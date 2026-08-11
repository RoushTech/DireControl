<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useTheme } from 'vuetify'
import { Terminal, type ITheme } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import {
  base64ToBytes,
  createCrlfNormalizer,
  deleteTranscript,
  getTranscript,
  getTranscripts,
  terminalSessionOriginLabels,
  TranscriptDirections,
  type TerminalTranscriptSummaryDto,
} from '@/api/terminalApi'
import { apiErrorDetail } from '@/api/axios'

const open = defineModel<boolean>({ default: false })

const vuetifyTheme = useTheme()

// ── Transcript list ──────────────────────────────────────────────────────────
const PAGE_SIZE = 25
const transcripts = ref<TerminalTranscriptSummaryDto[]>([])
const page = ref(1)
const callsignFilter = ref('')
const loading = ref(false)
const listError = ref('')

async function loadList() {
  loading.value = true
  listError.value = ''
  try {
    transcripts.value = await getTranscripts({
      page: page.value,
      pageSize: PAGE_SIZE,
      callsign: callsignFilter.value.trim() || undefined,
    })
  } catch (e: unknown) {
    listError.value = apiErrorDetail(e)
  } finally {
    loading.value = false
  }
}

let filterDebounce: ReturnType<typeof setTimeout> | null = null
watch(callsignFilter, () => {
  if (filterDebounce) clearTimeout(filterDebounce)
  filterDebounce = setTimeout(() => {
    page.value = 1
    void loadList()
  }, 400)
})

watch(open, (isOpen) => {
  if (isOpen) {
    page.value = 1
    void loadList()
  } else {
    closeReplay()
  }
})

function changePage(delta: number) {
  page.value = Math.max(1, page.value + delta)
  void loadList()
}

// ── Replay view ──────────────────────────────────────────────────────────────
const replaying = ref<TerminalTranscriptSummaryDto | null>(null)
const replayContainer = ref<HTMLElement | null>(null)
const replayLoading = ref(false)
let replayTerm: Terminal | null = null

function replayTheme(): ITheme {
  return vuetifyTheme.global.current.value.dark
    ? { background: '#0e1116', foreground: '#e6edf3', cursor: '#0e1116' }
    : { background: '#ffffff', foreground: '#1f2328', cursor: '#ffffff' }
}

const SGR_SENT = new TextEncoder().encode('\x1b[2;36m') // dim cyan — our keystrokes
const SGR_RESET = new TextEncoder().encode('\x1b[0m')

async function openReplay(summary: TerminalTranscriptSummaryDto) {
  replaying.value = summary
  replayLoading.value = true
  await nextTick()
  if (!replayContainer.value) return
  replayTerm?.dispose()
  replayTerm = new Terminal({
    convertEol: false,
    disableStdin: true,
    cursorBlink: false,
    scrollback: 20000,
    fontSize: 13,
    fontFamily: "'JetBrains Mono', 'Fira Code', monospace",
    theme: replayTheme(),
  })
  const fit = new FitAddon()
  replayTerm.loadAddon(fit)
  replayTerm.open(replayContainer.value)
  fit.fit()
  try {
    const detail = await getTranscript(summary.id)
    // Bare-CR packet line endings would overwrite lines in xterm.
    const normalize = createCrlfNormalizer()
    for (const chunk of detail.chunks) {
      const bytes = normalize(base64ToBytes(chunk.dataBase64))
      if (chunk.direction === TranscriptDirections.Sent) {
        // Dim/colored so sent bytes are distinguishable from the remote's.
        replayTerm.write(SGR_SENT)
        replayTerm.write(bytes)
        replayTerm.write(SGR_RESET)
      } else {
        replayTerm.write(bytes)
      }
    }
  } catch (e: unknown) {
    listError.value = apiErrorDetail(e)
  } finally {
    replayLoading.value = false
  }
}

function closeReplay() {
  replayTerm?.dispose()
  replayTerm = null
  replaying.value = null
}

// ── Delete ───────────────────────────────────────────────────────────────────
const deleteTarget = ref<TerminalTranscriptSummaryDto | null>(null)

async function confirmDelete() {
  const target = deleteTarget.value
  if (!target) return
  deleteTarget.value = null
  try {
    await deleteTranscript(target.id)
    transcripts.value = transcripts.value.filter((t) => t.id !== target.id)
  } catch (e: unknown) {
    listError.value = apiErrorDetail(e)
  }
}

// ── Formatting ───────────────────────────────────────────────────────────────

function formatDate(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString() : '—'
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  return `${(bytes / 1024).toFixed(1)} KB`
}
</script>

<template>
  <v-dialog v-model="open" max-width="900" scrollable>
    <v-card class="transcript-card">
      <v-card-title class="d-flex align-center ga-2">
        <v-btn
          v-if="replaying"
          icon="mdi-arrow-left"
          size="small"
          variant="text"
          @click="closeReplay"
        />
        <v-icon v-else>mdi-history</v-icon>
        <template v-if="replaying">
          {{ replaying.localCallsign }} ⇄ {{ replaying.remoteCallsign }}
          <span class="text-caption text-medium-emphasis ml-1">
            {{ formatDate(replaying.startedAt) }}
          </span>
        </template>
        <template v-else>Session Transcripts</template>
        <v-spacer />
        <v-btn icon="mdi-close" size="small" variant="text" @click="open = false" />
      </v-card-title>
      <v-divider />

      <!-- Replay -->
      <v-card-text v-if="replaying" class="pa-0 replay-body">
        <div ref="replayContainer" class="replay-terminal" />
        <v-progress-linear v-if="replayLoading" indeterminate color="primary" />
        <div class="text-caption text-medium-emphasis px-3 py-1">
          Dim cyan text was sent by your station; normal text was received.
        </div>
      </v-card-text>

      <!-- List -->
      <v-card-text v-else class="pa-0">
        <div class="d-flex align-center ga-2 px-3 py-2">
          <v-text-field
            v-model="callsignFilter"
            placeholder="Filter by callsign…"
            density="compact"
            variant="outlined"
            hide-details
            clearable
            style="max-width: 260px"
          />
          <v-spacer />
          <v-btn
            icon="mdi-refresh"
            size="small"
            variant="text"
            :loading="loading"
            @click="loadList"
          />
        </div>

        <v-alert v-if="listError" type="error" variant="tonal" density="compact" class="mx-3 mb-2">
          {{ listError }}
        </v-alert>

        <v-table density="compact" hover>
          <thead>
            <tr>
              <th>Remote</th>
              <th>Local</th>
              <th>Origin</th>
              <th>Started</th>
              <th>Ended</th>
              <th>In / Out</th>
              <th>End reason</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="t in transcripts" :key="t.id" style="cursor: pointer" @click="openReplay(t)">
              <td class="callsign-plain">{{ t.remoteCallsign }}</td>
              <td class="callsign-plain">{{ t.localCallsign }}</td>
              <td>
                <v-chip size="x-small" variant="tonal">
                  {{ terminalSessionOriginLabels[t.origin] }}
                </v-chip>
              </td>
              <td class="text-no-wrap">{{ formatDate(t.startedAt) }}</td>
              <td class="text-no-wrap">{{ formatDate(t.endedAt) }}</td>
              <td class="text-no-wrap">
                {{ formatBytes(t.bytesIn) }} / {{ formatBytes(t.bytesOut) }}
              </td>
              <td class="text-truncate" style="max-width: 140px" :title="t.endReason ?? undefined">
                {{ t.endReason ?? '—' }}
              </td>
              <td class="text-right">
                <v-btn
                  icon="mdi-delete-outline"
                  size="x-small"
                  variant="text"
                  color="error"
                  @click.stop="deleteTarget = t"
                />
              </td>
            </tr>
            <tr v-if="transcripts.length === 0 && !loading">
              <td colspan="8" class="text-center text-medium-emphasis py-6">
                No transcripts recorded.
              </td>
            </tr>
          </tbody>
        </v-table>

        <div class="d-flex align-center justify-end ga-2 px-3 py-2">
          <v-btn
            icon="mdi-chevron-left"
            size="x-small"
            variant="tonal"
            :disabled="page <= 1"
            @click="changePage(-1)"
          />
          <span class="text-caption">Page {{ page }}</span>
          <v-btn
            icon="mdi-chevron-right"
            size="x-small"
            variant="tonal"
            :disabled="transcripts.length < PAGE_SIZE"
            @click="changePage(1)"
          />
        </div>
      </v-card-text>
    </v-card>

    <!-- Delete confirm -->
    <v-dialog
      :model-value="deleteTarget !== null"
      max-width="420"
      @update:model-value="deleteTarget = null"
    >
      <v-card v-if="deleteTarget">
        <v-card-title>Delete transcript?</v-card-title>
        <v-card-text>
          Delete the transcript of the session with
          <strong>{{ deleteTarget.remoteCallsign }}</strong> from
          {{ formatDate(deleteTarget.startedAt) }}? This cannot be undone.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="deleteTarget = null">Cancel</v-btn>
          <v-btn color="error" variant="tonal" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-dialog>
</template>

<style scoped>
.transcript-card {
  min-height: 420px;
}

.replay-body {
  display: flex;
  flex-direction: column;
}

.replay-terminal {
  height: 440px;
  padding: 4px;
  box-sizing: border-box;
}

.callsign-plain {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-weight: 600;
}
</style>

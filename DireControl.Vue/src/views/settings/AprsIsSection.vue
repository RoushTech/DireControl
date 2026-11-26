<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { getSettings, updateAprsIsSettings } from '@/api/stationsApi'
import { apiErrorDetail } from '@/api/axios'
import { getStatus, reconnectAprsIs, AprsIsStates } from '@/api/statusApi'
import { serverNow } from '@/utils/serverTime'
import { useHealthStore } from '@/stores/health'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('aprsis')
const toastStore = useToastStore()
const healthStore = useHealthStore()

// ─── APRS-IS settings ─────────────────────────────────────────────────────────
const aprsIsEnabled = ref(false)
const aprsIsHost = ref('rotate.aprs2.net')
const aprsIsPort = ref(14580)
const aprsIsPasscodeOverride = ref<number | null>(null)
const aprsIsPasscodeComputed = ref(0)
const aprsIsFilter = ref('r/39.0/-98.0/500')
const deduplicationWindowSeconds = ref(60)
const aprsIsSaving = ref(false)
const aprsIsSaveError = ref('')

const form = useFormSnapshot(() => ({
  enabled: aprsIsEnabled.value,
  host: (aprsIsHost.value ?? '').trim(),
  port: Number(aprsIsPort.value),
  passcode: aprsIsPasscodeOverride.value ?? null,
  filter: (aprsIsFilter.value ?? '').trim(),
  dedupe: Number(deduplicationWindowSeconds.value),
}))
const loadError = ref('')

async function saveAprsIsSettings() {
  aprsIsSaving.value = true
  aprsIsSaveError.value = ''
  try {
    await updateAprsIsSettings({
      aprsIsEnabled: aprsIsEnabled.value,
      aprsIsHost: aprsIsHost.value.trim(),
      aprsIsPort: aprsIsPort.value,
      // A cleared number field comes back as '' or null — both mean "auto".
      aprsIsPasscodeOverride:
        aprsIsPasscodeOverride.value === null || String(aprsIsPasscodeOverride.value) === ''
          ? null
          : Number(aprsIsPasscodeOverride.value),
      aprsIsFilter: aprsIsFilter.value.trim(),
      deduplicationWindowSeconds: deduplicationWindowSeconds.value,
    })
    form.markClean()
    if (aprsIsEnabled.value) {
      toastStore.toast('APRS-IS settings saved. Reconnecting…', 'info', 4000)
      void watchAprsIsOutcome()
    } else {
      toastStore.toast('APRS-IS settings saved. Connection closed.', 'info', 4000)
      aprsIsOutcome.value = null
    }
    void healthStore.refreshStatus()
  } catch (e) {
    aprsIsSaveError.value = apiErrorDetail(e)
  } finally {
    aprsIsSaving.value = false
  }
}

// What happened after the save: a save restarts the connection, and whether
// the new settings actually log in is the part the operator cares about.
const aprsIsOutcome = ref<{ type: 'info' | 'success' | 'error' | 'warning'; text: string } | null>(
  null,
)
const aprsIsReconnecting = ref(false)
let aprsIsOutcomeRun = 0

async function watchAprsIsOutcome() {
  const run = ++aprsIsOutcomeRun
  // Only a connect attempt made after this point counts — until the service
  // restarts, status still describes the previous connection.
  const since = serverNow() - 1000
  aprsIsOutcome.value = { type: 'info', text: 'Connecting to APRS-IS…' }
  const deadline = Date.now() + 15_000
  while (run === aprsIsOutcomeRun && Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 1500))
    if (run !== aprsIsOutcomeRun) return
    try {
      const s = await getStatus()
      const fresh =
        !!s.aprsIsLastConnectAttemptAt && new Date(s.aprsIsLastConnectAttemptAt).getTime() >= since
      if (!fresh) continue
      if (s.aprsIsState === AprsIsStates.Connected) {
        aprsIsOutcome.value = {
          type: 'success',
          text: `Connected to ${s.aprsIsServerName ?? aprsIsHost.value}.`,
        }
        return
      }
      if (s.aprsIsState === AprsIsStates.AuthFailed) {
        aprsIsOutcome.value = {
          type: 'error',
          text: `Auth failed: ${s.aprsIsLastError ?? 'the server rejected the passcode'}.`,
        }
        return
      }
    } catch (e) {
      aprsIsOutcome.value = { type: 'error', text: apiErrorDetail(e) }
      return
    }
  }
  if (run === aprsIsOutcomeRun) {
    const s = await getStatus().catch(() => null)
    aprsIsOutcome.value = {
      type: 'warning',
      text: `Still not connected after 15 s${s?.aprsIsLastError ? ` (${s.aprsIsLastError})` : ''}. Check the host and port.`,
    }
  }
}

async function reconnectAprsIsNow() {
  aprsIsReconnecting.value = true
  try {
    await reconnectAprsIs()
    void watchAprsIsOutcome()
  } catch (e) {
    aprsIsOutcome.value = { type: 'error', text: apiErrorDetail(e) }
  } finally {
    aprsIsReconnecting.value = false
  }
}
onMounted(async () => {
  try {
    const s = await getSettings()
    aprsIsEnabled.value = s.aprsIsEnabled
    aprsIsHost.value = s.aprsIsHost
    aprsIsPort.value = s.aprsIsPort
    aprsIsPasscodeOverride.value = s.aprsIsPasscodeOverride
    aprsIsPasscodeComputed.value = s.aprsIsPasscodeComputed
    aprsIsFilter.value = s.aprsIsFilter
    deduplicationWindowSeconds.value = s.deduplicationWindowSeconds
    form.markClean()
    // Arriving from a "Fix passcode" banner: say why up front.
    const st = healthStore.status
    if (st?.aprsIsState === AprsIsStates.AuthFailed && !aprsIsOutcome.value)
      aprsIsOutcome.value = {
        type: 'error',
        text: `Auth failed: ${st.aprsIsLastError ?? 'the server rejected the passcode'}.`,
      }
  } catch (e) {
    loadError.value = `Couldn't load APRS-IS settings: ${apiErrorDetail(e)}`
  }
})

onUnmounted(() => {
  // Abandon any outcome poll still running.
  aprsIsOutcomeRun++
})
</script>

<template>
  <div class="settings-single">
    <!-- ================================================================ -->
    <!-- APRS-IS -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">APRS-IS</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <v-switch
        v-model="aprsIsEnabled"
        label="Enable APRS-IS connection"
        hide-details
        density="compact"
        class="mb-4"
      />

      <div class="d-flex ga-2 mb-2">
        <v-text-field v-model="aprsIsHost" label="Server" density="compact" style="flex: 3" />
        <v-text-field
          v-model.number="aprsIsPort"
          label="Port"
          density="compact"
          type="number"
          style="flex: 1"
        />
      </div>

      <div class="text-body-2 text-medium-emphasis mb-1">
        Passcode (auto-computed: <strong>{{ aprsIsPasscodeComputed }}</strong>)
      </div>
      <v-text-field
        v-model.number="aprsIsPasscodeOverride"
        label="Passcode override (leave blank to use auto-computed)"
        density="compact"
        type="number"
        clearable
        class="mb-2"
        style="max-width: 360px"
      />

      <v-text-field
        v-model="aprsIsFilter"
        label="Server-side filter"
        density="compact"
        class="mb-2"
        hint="e.g. r/39.0/-98.0/500 for stations within 500 km. Terms are OR'd: an unscoped t/m pulls messages from the whole network"
        persistent-hint
      />

      <v-text-field
        v-model.number="deduplicationWindowSeconds"
        label="Deduplication window (seconds)"
        density="compact"
        type="number"
        class="mb-3 mt-2"
        style="max-width: 240px"
        hint="Packets with the same callsign and info field within this window are counted as duplicates"
        persistent-hint
      />

      <v-alert v-if="aprsIsSaveError" type="error" density="compact" class="mt-3">
        {{ aprsIsSaveError }}
      </v-alert>
      <v-alert
        v-if="aprsIsOutcome"
        :type="aprsIsOutcome.type"
        density="compact"
        variant="tonal"
        class="mt-3"
      >
        {{ aprsIsOutcome.text }}
        <template #append>
          <v-btn
            v-if="aprsIsOutcome.type !== 'info' && aprsIsEnabled"
            size="small"
            variant="text"
            prepend-icon="mdi-refresh"
            :loading="aprsIsReconnecting"
            @click="reconnectAprsIsNow"
          >
            Reconnect
          </v-btn>
        </template>
      </v-alert>
    </v-card>

    <v-alert v-if="loadError" type="error" variant="tonal" density="compact" class="mb-3">
      {{ loadError }}
    </v-alert>
    <SettingsSaveBar
      :restart="meta.restart"
      :dirty="form.dirty.value"
      :saving="aprsIsSaving"
      :disabled="!form.dirty.value"
      save-label="Save APRS-IS"
      @save="saveAprsIsSettings"
    />
  </div>
</template>

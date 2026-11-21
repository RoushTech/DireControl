<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { baseCallsign } from '@/types/packet'
import { getSettings } from '@/api/stationsApi'
import { updatePacketSettings } from '@/api/packetApi'
import { apiErrorDetail } from '@/api/axios'
import type { SettingsDto } from '@/types/station'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('packet')
const toastStore = useToastStore()

/** Station callsign, for the PMS "answers as" preview (edited on Station). */
const stationCallsign = ref('')

// ─── Packet (connected mode / PMS / AGWPE) ───────────────────────────────────
const connectedModeInboundEnabled = ref(false)
const connectedModeMaxSessions = ref(4)
const connectedModeDefaultPaclen = ref(128)
const connectedModeWindowSize = ref(4)
const connectedModeT1Seconds = ref(10)
const connectedModeRetries = ref(10)
const connectedModePreferMod128 = ref(false)
const pmsEnabled = ref(false)
const pmsSsid = ref(1)
const pmsBannerText = ref('')
const pmsRetentionDays = ref(30)
const agwpeServerEnabled = ref(false)
const agwpeServerPort = ref(8000)
const agwpeServerBindAddress = ref('127.0.0.1')
const terminalTranscriptRetentionDays = ref(30)
const packetSaving = ref(false)
const packetSaveError = ref('')

/** What the PMS answers as on the air: the station base callsign + PMS SSID. */
const pmsFullCallsign = computed(() => {
  const base = baseCallsign(stationCallsign.value)
  return `${base || 'N0CALL'}-${pmsSsid.value}`
})

const agwpeBindWarning = computed(
  () =>
    agwpeServerEnabled.value &&
    agwpeServerBindAddress.value.trim() !== '127.0.0.1' &&
    agwpeServerBindAddress.value.trim() !== '',
)

function loadPacketSettings(s: SettingsDto) {
  connectedModeInboundEnabled.value = s.connectedModeInboundEnabled
  connectedModeMaxSessions.value = s.connectedModeMaxSessions
  connectedModeDefaultPaclen.value = s.connectedModeDefaultPaclen
  connectedModeWindowSize.value = s.connectedModeWindowSize
  connectedModeT1Seconds.value = s.connectedModeT1Seconds
  connectedModeRetries.value = s.connectedModeRetries
  connectedModePreferMod128.value = s.connectedModePreferMod128
  pmsEnabled.value = s.pmsEnabled
  pmsSsid.value = s.pmsSsid
  pmsBannerText.value = s.pmsBannerText
  pmsRetentionDays.value = s.pmsRetentionDays
  agwpeServerEnabled.value = s.agwpeServerEnabled
  agwpeServerPort.value = s.agwpeServerPort
  agwpeServerBindAddress.value = s.agwpeServerBindAddress
  terminalTranscriptRetentionDays.value = s.terminalTranscriptRetentionDays
}

const form = useFormSnapshot(() => ({
  connectedModeInboundEnabled: connectedModeInboundEnabled.value,
  connectedModeMaxSessions: connectedModeMaxSessions.value,
  connectedModeDefaultPaclen: connectedModeDefaultPaclen.value,
  connectedModeWindowSize: connectedModeWindowSize.value,
  connectedModeT1Seconds: connectedModeT1Seconds.value,
  connectedModeRetries: connectedModeRetries.value,
  connectedModePreferMod128: connectedModePreferMod128.value,
  pmsEnabled: pmsEnabled.value,
  pmsSsid: pmsSsid.value,
  pmsBannerText: pmsBannerText.value,
  pmsRetentionDays: pmsRetentionDays.value,
  agwpeServerEnabled: agwpeServerEnabled.value,
  agwpeServerPort: agwpeServerPort.value,
  agwpeServerBindAddress: agwpeServerBindAddress.value,
  terminalTranscriptRetentionDays: terminalTranscriptRetentionDays.value,
}))

const loadError = ref('')

async function savePacketSettings() {
  packetSaving.value = true
  packetSaveError.value = ''
  try {
    await updatePacketSettings({
      connectedModeInboundEnabled: connectedModeInboundEnabled.value,
      connectedModeMaxSessions: connectedModeMaxSessions.value,
      connectedModeDefaultPaclen: connectedModeDefaultPaclen.value,
      connectedModeWindowSize: connectedModeWindowSize.value,
      connectedModeT1Seconds: connectedModeT1Seconds.value,
      connectedModeRetries: connectedModeRetries.value,
      connectedModePreferMod128: connectedModePreferMod128.value,
      pmsEnabled: pmsEnabled.value,
      pmsSsid: pmsSsid.value,
      pmsBannerText: pmsBannerText.value,
      pmsRetentionDays: pmsRetentionDays.value,
      agwpeServerEnabled: agwpeServerEnabled.value,
      agwpeServerPort: agwpeServerPort.value,
      agwpeServerBindAddress: agwpeServerBindAddress.value.trim(),
      terminalTranscriptRetentionDays: terminalTranscriptRetentionDays.value,
    })
    form.markClean()
    toastStore.toast('Packet settings saved — restarting PMS, terminal listener and AGWPE…', 'info')
  } catch (e: unknown) {
    packetSaveError.value = apiErrorDetail(e)
  } finally {
    packetSaving.value = false
  }
}

onMounted(async () => {
  try {
    const s = await getSettings()
    stationCallsign.value = s.ourCallsign
    loadPacketSettings(s)
    form.markClean()
  } catch (e) {
    loadError.value = `Couldn't load packet settings: ${apiErrorDetail(e)}`
  }
})
</script>

<template>
  <div class="settings-single">
    <!-- ================================================================ -->
    <!-- Packet: connected mode, PMS mailbox, AGWPE server -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">Packet</span>
    </div>

    <!-- Connected mode -->
    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-subtitle-2 font-weight-medium mb-1">Connected mode</div>
      <div class="text-caption text-medium-emphasis mb-2">
        AX.25 connected-mode sessions — the Terminal view, inbound connects, and the link parameters
        used when a session doesn't override them.
      </div>
      <v-switch
        v-model="connectedModeInboundEnabled"
        label="Accept inbound connections"
        color="primary"
        hide-details
        density="compact"
        class="mb-2"
      />
      <div class="d-flex flex-wrap ga-4 mb-2">
        <v-text-field
          v-model.number="connectedModeMaxSessions"
          label="Max sessions"
          type="number"
          min="1"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
        <v-text-field
          v-model.number="connectedModeDefaultPaclen"
          label="Default PACLEN"
          type="number"
          min="1"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
        <v-text-field
          v-model.number="connectedModeWindowSize"
          label="Window size"
          type="number"
          min="1"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
        <v-text-field
          v-model.number="connectedModeT1Seconds"
          label="T1 (seconds)"
          type="number"
          min="1"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
        <v-text-field
          v-model.number="connectedModeRetries"
          label="Retries (N2)"
          type="number"
          min="1"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
      </div>
      <v-switch
        v-model="connectedModePreferMod128"
        label="Prefer modulo-128 (EAX.25) when the remote supports it"
        color="primary"
        hide-details
        density="compact"
      />
      <v-divider class="my-3" />
      <v-text-field
        v-model.number="terminalTranscriptRetentionDays"
        label="Transcript retention (days)"
        type="number"
        min="0"
        density="compact"
        variant="outlined"
        hint="How long session transcripts are kept. 0 = keep forever."
        persistent-hint
        style="max-width: 240px"
      />
    </v-card>

    <!-- PMS mailbox -->
    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-subtitle-2 font-weight-medium mb-1">PMS mailbox</div>
      <div class="text-caption text-medium-emphasis mb-2">
        A small personal message system other stations can connect to and leave mail in.
      </div>
      <v-switch
        v-model="pmsEnabled"
        label="Enable PMS mailbox"
        color="primary"
        hide-details
        density="compact"
        class="mb-2"
      />
      <div class="d-flex flex-wrap ga-4 align-center mb-2">
        <v-text-field
          v-model.number="pmsSsid"
          label="SSID"
          type="number"
          min="1"
          max="15"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 120px"
        />
        <span class="text-body-2 text-medium-emphasis">
          Answers as <code>{{ pmsFullCallsign }}</code>
        </span>
      </div>
      <v-text-field
        v-model="pmsBannerText"
        label="Banner text"
        density="compact"
        variant="outlined"
        hint="Shown to stations when they connect."
        persistent-hint
        class="mb-3"
        style="max-width: 480px"
      />
      <v-text-field
        v-model.number="pmsRetentionDays"
        label="Message retention (days)"
        type="number"
        min="0"
        density="compact"
        variant="outlined"
        hint="Killed messages are purged after this many days. 0 = keep forever."
        persistent-hint
        style="max-width: 240px"
      />
    </v-card>

    <!-- AGWPE server -->
    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-subtitle-2 font-weight-medium mb-1">AGWPE server</div>
      <div class="text-caption text-medium-emphasis mb-2">
        Lets AGWPE-protocol applications (e.g. Winlink Express, EasyTerm) use DireControl as their
        packet engine.
      </div>
      <v-switch
        v-model="agwpeServerEnabled"
        label="Enable AGWPE server"
        color="primary"
        hide-details
        density="compact"
        class="mb-2"
      />
      <div class="d-flex flex-wrap ga-4 mb-2">
        <v-text-field
          v-model.number="agwpeServerPort"
          label="Port"
          type="number"
          min="1"
          max="65535"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 140px"
        />
        <v-text-field
          v-model="agwpeServerBindAddress"
          label="Bind address"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 220px"
        />
      </div>
      <v-alert
        v-if="agwpeBindWarning"
        type="warning"
        variant="tonal"
        density="compact"
        class="mb-2"
      >
        The AGWPE protocol has no authentication — binding to anything other than
        <code>127.0.0.1</code> lets any host that can reach this machine key your radios.
      </v-alert>
    </v-card>

    <v-alert v-if="packetSaveError" type="error" variant="tonal" density="compact" class="mb-3">
      {{ packetSaveError }}
    </v-alert>

    <v-alert v-if="loadError" type="error" variant="tonal" density="compact" class="mb-3">
      {{ loadError }}
    </v-alert>
    <SettingsSaveBar
      :restart="meta.restart"
      :dirty="form.dirty.value"
      :saving="packetSaving"
      :disabled="!form.dirty.value"
      save-label="Save packet settings"
      @save="savePacketSettings"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import {
  AUDIO_LOG_PACK_URL,
  getAudioCaptures,
  getAudioCaptureSettings,
  getRecordingRadios,
  setAudioCaptureEnabled,
  startAudioRecording,
  stopAudioRecording,
  type AudioCaptureDto,
} from '@/api/modemApi'

/**
 * Off-air audio capture for diagnosing decode failures: manual recording per
 * radio, automatic capture of heard-but-undecoded transmissions, and the log
 * pack that bundles them.  Lives on the Radio page's Diagnostics panel.
 */
const radiosStore = useRadiosStore()

const recordingRadios = ref<string[]>([])
const captures = ref<AudioCaptureDto[]>([])
const recordError = ref<string | null>(null)

onMounted(() => {
  if (radiosStore.radios.length === 0) void radiosStore.fetchRadios()
  void refreshCaptureState()
})

defineExpose({ refreshCaptureState })

/**
 * Recording state lives on the server, so a page reload recovers it rather
 * than showing a stopped button while audio is still being recorded.
 */
async function refreshCaptureState() {
  try {
    const [recording, files, settings] = await Promise.all([
      getRecordingRadios(),
      getAudioCaptures(),
      getAudioCaptureSettings(),
    ])
    recordingRadios.value = recording
    captures.value = files
    // Assigned without going through the watcher that would write it back.
    applyingCaptureSetting = true
    autoCaptureEnabled.value = settings.enabled
    applyingCaptureSetting = false
  } catch {
    /* diagnostics are best-effort; never block the player on them */
  }
}

/**
 * Persisted server-side, so the switch reflects what the modem is actually
 * doing rather than this tab's opinion of it.
 */
const autoCaptureEnabled = ref(true)
let applyingCaptureSetting = false

watch(autoCaptureEnabled, async (enabled) => {
  if (applyingCaptureSetting) return
  recordError.value = null
  try {
    await setAudioCaptureEnabled(enabled)
  } catch {
    recordError.value = 'Could not change the capture setting.'
    await refreshCaptureState()
  }
})

const isRecording = (id: string) => recordingRadios.value.includes(id)

const missedCaptureCount = computed(
  () => captures.value.filter((c) => c.reason === 'missed-decode').length,
)

async function toggleRecording(id: string) {
  recordError.value = null
  try {
    if (isRecording(id)) await stopAudioRecording(id)
    else await startAudioRecording(id)
  } catch {
    recordError.value = isRecording(id)
      ? 'Could not stop the recording.'
      : 'Could not start recording. The radio needs a running modem.'
  }
  await refreshCaptureState()
}

const recordableRadios = computed(() =>
  radiosStore.activeRadios.filter((radio) => radio.modem.modemEnabled),
)
</script>

<template>
  <div>
    <div v-if="recordableRadios.length === 0" class="text-caption text-medium-emphasis mb-2">
      No radio has the sound modem enabled, so there is no audio to record.
    </div>
    <div v-for="radio in recordableRadios" :key="radio.id" class="d-flex align-center ga-2 mb-1">
      <span class="text-body-2">{{ radio.name }}</span>
      <span class="text-caption text-medium-emphasis">{{ radio.fullCallsign }}</span>
      <v-spacer />
      <v-btn
        size="small"
        variant="tonal"
        :color="isRecording(radio.id) ? 'error' : undefined"
        :prepend-icon="isRecording(radio.id) ? 'mdi-stop-circle' : 'mdi-record-circle-outline'"
        @click="toggleRecording(radio.id)"
      >
        {{ isRecording(radio.id) ? 'Stop recording' : 'Record raw audio' }}
      </v-btn>
    </div>
    <v-divider class="my-2" />
    <!-- Recordings are raw 48 kHz audio for replaying through the demodulator offline. -->
    <v-alert
      v-if="recordError"
      type="warning"
      variant="tonal"
      density="compact"
      class="mb-2 text-caption"
    >
      {{ recordError }}
    </v-alert>

    <v-switch
      v-model="autoCaptureEnabled"
      label="Auto-capture failed decodes"
      color="primary"
      density="compact"
      hide-details
      class="ml-1"
    />
    <div class="text-caption text-medium-emphasis mb-2 ml-1">
      Save the audio of transmissions that are heard but never decode. Manual recording works either
      way.
    </div>

    <div class="text-caption text-medium-emphasis ml-1">
      <template v-if="captures.length > 0">
        {{ captures.length }} capture{{ captures.length === 1 ? '' : 's' }} saved<template
          v-if="missedCaptureCount > 0"
        >
          · {{ missedCaptureCount }} from failed decodes</template
        >.
      </template>
      <template v-else-if="autoCaptureEnabled"> No captures yet. </template>
      <template v-else> No captures, and auto-capture is off. </template>
    </div>

    <!-- Navigated to rather than fetched: the pack can be tens of megabytes,
             and the browser handles the download and its progress. -->
    <v-btn
      :href="AUDIO_LOG_PACK_URL"
      :disabled="captures.length === 0"
      prepend-icon="mdi-folder-zip-outline"
      variant="tonal"
      size="small"
      block
      class="mt-2"
    >
      Download log pack
    </v-btn>
    <div class="text-caption text-medium-emphasis mt-1 ml-1">
      Audio, modem state, and the recent log tail in one zip.
    </div>
  </div>
</template>

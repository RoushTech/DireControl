<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useAudioMonitorStore } from '@/stores/audioMonitorStore'
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

const audio = useAudioMonitorStore()
const radiosStore = useRadiosStore()

const recordingRadios = ref<string[]>([])
const captures = ref<AudioCaptureDto[]>([])
const recordError = ref<string | null>(null)

// This button lives in the app bar, so it may be the first thing to need the
// radio list — views that load it themselves share the same store.
onMounted(() => {
  if (radiosStore.radios.length === 0) void radiosStore.fetchRadios()
  void refreshCaptureState()
})

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
      : 'Could not start recording — the radio needs a running modem.'
  }
  await refreshCaptureState()
}

/**
 * Only radios with the native sound modem configured have audio to stream —
 * a KISS or AGWPE radio gives us frames, never samples.
 */
const listenableRadios = computed(() =>
  radiosStore.activeRadios.filter((radio) => radio.modem.modemEnabled),
)

const playing = computed(() => audio.state === 'playing')

const activeRadioName = computed(
  () => listenableRadios.value.find((radio) => radio.id === audio.radioId)?.name ?? null,
)

const buttonColor = computed(() => {
  if (audio.state === 'error') return 'error'
  if (playing.value) return audio.muted ? 'medium-emphasis' : 'success'
  return undefined
})

const buttonIcon = computed(() => {
  if (!playing.value) return 'mdi-headphones'
  return audio.muted ? 'mdi-headphones-off' : 'mdi-headphones'
})

/** Live modem state for a radio, matching the TX/RX convention in OwnStationPanel. */
function activityFor(id: string): { label: string; color: string } | null {
  const activity = radiosStore.getActivityForRadio(id)
  if (activity?.transmitting) return { label: 'TX', color: 'error' }
  if (activity?.carrierDetected) return { label: 'RX', color: 'success' }
  return null
}

function volumeIcon() {
  if (audio.muted || audio.volume === 0) return 'mdi-volume-off'
  if (audio.volume < 0.5) return 'mdi-volume-medium'
  return 'mdi-volume-high'
}
</script>

<template>
  <!-- Captures accumulate in the background, so the counts are refreshed each
       time the menu opens rather than only on mount. -->
  <v-menu
    :close-on-content-click="false"
    location="bottom end"
    @update:model-value="(open) => open && refreshCaptureState()"
  >
    <template #activator="{ props }">
      <v-btn
        v-bind="props"
        :icon="buttonIcon"
        :color="buttonColor"
        variant="text"
        size="small"
        aria-label="Listen to radio audio"
      />
    </template>

    <v-card min-width="320" density="compact">
      <v-card-title class="text-subtitle-2 pb-1">
        Listen
        <span v-if="activeRadioName" class="text-caption text-medium-emphasis">
          · {{ activeRadioName }}
        </span>
      </v-card-title>

      <v-card-text class="pt-0">
        <v-alert
          v-if="audio.errorMessage"
          type="error"
          variant="tonal"
          density="compact"
          class="mb-2 text-caption"
        >
          {{ audio.errorMessage }}
        </v-alert>

        <div v-if="listenableRadios.length === 0" class="text-caption text-medium-emphasis">
          No radio has the sound modem enabled, so there is no audio to stream.
        </div>

        <v-list v-else density="compact" class="py-0">
          <v-list-item
            v-for="radio in listenableRadios"
            :key="radio.id"
            :active="audio.radioId === radio.id"
            class="px-1"
            @click="audio.toggle(radio.id)"
          >
            <template #prepend>
              <v-icon
                size="18"
                :color="audio.radioId === radio.id && playing ? 'success' : undefined"
              >
                {{ audio.radioId === radio.id && playing ? 'mdi-stop' : 'mdi-play' }}
              </v-icon>
            </template>
            <v-list-item-title class="text-body-2">{{ radio.name }}</v-list-item-title>
            <v-list-item-subtitle class="text-caption">
              {{ radio.fullCallsign }}
              <template v-if="radio.frequencyMhz"> · {{ radio.frequencyMhz }} MHz</template>
            </v-list-item-subtitle>
            <template #append>
              <div class="d-flex align-center ga-1">
                <v-progress-circular
                  v-if="audio.radioId === radio.id && audio.state === 'connecting'"
                  indeterminate
                  size="16"
                  width="2"
                />
                <!-- Live RX/TX from the modemLevel stream, so you can tell at a
                     glance which radio is worth listening to. -->
                <v-chip
                  v-else-if="activityFor(radio.id)"
                  :color="activityFor(radio.id)!.color"
                  size="x-small"
                  variant="tonal"
                  label
                >
                  {{ activityFor(radio.id)!.label }}
                </v-chip>
                <!-- Recording is independent of listening: you can record a
                     radio you are not monitoring. -->
                <v-btn
                  :icon="isRecording(radio.id) ? 'mdi-stop-circle' : 'mdi-record-circle-outline'"
                  :color="isRecording(radio.id) ? 'error' : undefined"
                  variant="text"
                  size="x-small"
                  :aria-label="
                    isRecording(radio.id) ? 'Stop recording audio' : 'Record raw audio for analysis'
                  "
                  @click.stop="toggleRecording(radio.id)"
                />
              </div>
            </template>
          </v-list-item>
        </v-list>

        <template v-if="playing">
          <v-divider class="my-2" />

          <!-- Listening-side level meter: confirms audio is actually arriving,
               and makes a clipped or dead audio path obvious at a glance. -->
          <div class="d-flex align-center mb-2">
            <v-icon size="16" class="mr-2">mdi-sine-wave</v-icon>
            <v-progress-linear
              :model-value="audio.level * 100"
              :color="audio.level > 0.95 ? 'error' : 'success'"
              height="6"
              rounded
            />
          </div>

          <div class="d-flex align-center">
            <v-btn
              :icon="volumeIcon()"
              variant="text"
              size="x-small"
              :aria-label="audio.muted ? 'Unmute' : 'Mute'"
              @click="audio.muted = !audio.muted"
            />
            <v-slider
              v-model="audio.volume"
              :min="0"
              :max="1"
              :step="0.01"
              hide-details
              density="compact"
              class="mx-2"
              aria-label="Volume"
            />
          </div>
        </template>

        <v-divider class="my-2" />

        <v-switch
          v-model="audio.squelchGated"
          label="Squelch gate"
          color="primary"
          density="compact"
          hide-details
          class="ml-1"
        />
        <div class="text-caption text-medium-emphasis mb-2 ml-1">
          Only stream while carrier is detected. Leave off to hear raw audio — noise, hum, and
          signals too weak to decode.
        </div>

        <v-switch
          v-model="audio.includeTx"
          label="Hear my transmissions"
          color="primary"
          density="compact"
          hide-details
          class="ml-1"
        />

        <!-- Only shown while audio is actually breaking up. Isolated dropped
             frames are routine and saying so just looks like a fault. -->
        <div
          v-if="audio.audioSkipping"
          class="d-flex align-center text-caption text-warning mt-2 ml-1"
        >
          <v-icon size="14" class="mr-1">mdi-wifi-alert</v-icon>
          Audio is breaking up — the connection can't keep up.
        </div>

        <v-divider class="my-2" />

        <!-- Capture is the diagnostic half of this menu: recordings are raw
             48 kHz audio for replaying through the demodulator offline. -->
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
          Save the audio of transmissions that are heard but never decode. Manual recording works
          either way.
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
      </v-card-text>
    </v-card>
  </v-menu>
</template>

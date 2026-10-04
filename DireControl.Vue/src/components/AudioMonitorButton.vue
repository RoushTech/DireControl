<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useAudioMonitorStore } from '@/stores/audioMonitorStore'
import { useRadiosStore } from '@/stores/radiosStore'

const audio = useAudioMonitorStore()
const radiosStore = useRadiosStore()

// This button lives in the app bar, so it may be the first thing to need the
// radio list — views that load it themselves share the same store.
onMounted(() => {
  if (radiosStore.radios.length === 0) void radiosStore.fetchRadios()
})

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

function volumeIcon() {
  if (audio.muted || audio.volume === 0) return 'mdi-volume-off'
  if (audio.volume < 0.5) return 'mdi-volume-medium'
  return 'mdi-volume-high'
}
</script>

<template>
  <v-menu :close-on-content-click="false" location="bottom end">
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
              <v-progress-circular
                v-if="audio.radioId === radio.id && audio.state === 'connecting'"
                indeterminate
                size="16"
                width="2"
              />
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

        <div v-if="audio.droppedFrames > 0" class="text-caption text-medium-emphasis mt-2 ml-1">
          {{ audio.droppedFrames }} frame(s) dropped to hold latency down.
        </div>
      </v-card-text>
    </v-card>
  </v-menu>
</template>

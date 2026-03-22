<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getStationFrequencies } from '@/api/statisticsApi'
import { StationType, type StationFrequencyDto } from '@/types/station'
import { timeAgo } from '@/utils/time'

const frequencies = ref<StationFrequencyDto[]>([])
const loading = ref(false)
const error = ref(false)

const typeLabel: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Unknown]: 'Unknown',
  [StationType.Gateway]: 'Gateway',
}
const typeColor: Record<StationType, string> = {
  [StationType.Fixed]: 'blue',
  [StationType.Mobile]: 'green',
  [StationType.Weather]: 'teal',
  [StationType.Digipeater]: 'orange',
  [StationType.IGate]: 'purple',
  [StationType.Unknown]: 'grey',
  [StationType.Gateway]: 'deep-purple',
}

async function load() {
  loading.value = true
  error.value = false
  try {
    frequencies.value = await getStationFrequencies()
  } catch {
    error.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <v-container fluid class="pa-4">
    <div class="d-flex align-center mb-4">
      <h1 class="text-h5">Frequencies</h1>
      <v-chip size="small" color="deep-purple" class="ml-3">
        {{ frequencies.length }}
      </v-chip>
      <v-spacer />
      <v-btn
        icon="mdi-refresh"
        variant="text"
        size="small"
        :loading="loading"
        @click="load"
      />
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">
      Failed to load frequency data.
    </v-alert>

    <v-progress-linear v-if="loading && frequencies.length === 0" indeterminate class="mb-4" />

    <v-card v-if="frequencies.length > 0" variant="outlined">
      <v-table density="compact">
        <thead>
          <tr>
            <th class="text-caption">Frequency</th>
            <th class="text-caption">Callsign</th>
            <th class="text-caption">Mode</th>
            <th class="text-caption">Type</th>
            <th class="text-caption text-right">Last Seen</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in frequencies" :key="f.callsign">
            <td class="text-body-2 font-weight-medium">{{ f.frequencyMhz }} MHz</td>
            <td class="text-body-2">{{ f.callsign }}</td>
            <td class="text-body-2">
              <v-chip v-if="f.mode" size="x-small" color="deep-purple" variant="tonal">{{ f.mode }}</v-chip>
              <span v-else class="text-medium-emphasis">—</span>
            </td>
            <td>
              <v-chip :color="typeColor[f.stationType]" size="x-small" label>
                {{ typeLabel[f.stationType] }}
              </v-chip>
            </td>
            <td class="text-body-2 text-right text-medium-emphasis">{{ timeAgo(f.lastSeen) }}</td>
          </tr>
        </tbody>
      </v-table>
    </v-card>

    <v-card v-else-if="!loading" variant="outlined" class="pa-8 text-center text-medium-emphasis">
      No frequency data available yet.
    </v-card>
  </v-container>
</template>

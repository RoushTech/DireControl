<script setup lang="ts">
import type { StationFrequencyDto } from '@/types/station'
import { formatDateTime, timeAgo } from '@/utils/time'
import { stationTypeColor, stationTypeLabel } from './stationTypes'

defineProps<{
  frequencies: StationFrequencyDto[]
  loading: boolean
  now: number
}>()

const stationTo = (callsign: string) => `/stations/${encodeURIComponent(callsign)}`
</script>

<template>
  <v-card variant="outlined">
    <v-card-title class="card-title d-flex align-center ga-2">
      Last-seen frequencies
      <v-chip size="small" variant="tonal">{{ frequencies.length }}</v-chip>
    </v-card-title>
    <v-card-subtitle class="pb-2">
      Voice frequencies stations advertise in their beacons.
    </v-card-subtitle>
    <v-progress-linear v-if="loading && frequencies.length === 0" indeterminate />
    <div class="table-scroll">
      <v-table density="compact">
        <thead>
          <tr>
            <th>Frequency</th>
            <th>Callsign</th>
            <th>Mode</th>
            <th>Type</th>
            <th class="text-right">Last seen</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in frequencies" :key="f.callsign">
            <td class="tabular">{{ f.frequencyMhz }} MHz</td>
            <td>
              <router-link class="callsign-link" :to="stationTo(f.callsign)">
                {{ f.callsign }}
              </router-link>
            </td>
            <td>
              <span v-if="f.mode">{{ f.mode }}</span>
              <span v-else class="text-medium-emphasis">—</span>
            </td>
            <td>
              <v-chip :color="stationTypeColor[f.stationType]" size="small" variant="tonal" label>
                {{ stationTypeLabel[f.stationType] }}
              </v-chip>
            </td>
            <td class="text-right text-medium-emphasis nowrap" :title="formatDateTime(f.lastSeen)">
              {{ timeAgo(f.lastSeen, now) }}
            </td>
          </tr>
          <tr v-if="!loading && frequencies.length === 0">
            <td colspan="5" class="text-center text-medium-emphasis py-6">
              No station has advertised a frequency yet.
            </td>
          </tr>
        </tbody>
      </v-table>
    </div>
  </v-card>
</template>

<style scoped>
.card-title {
  font-size: 14px;
  font-weight: 650;
}

.table-scroll {
  overflow-x: auto;
}

.tabular {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.nowrap {
  white-space: nowrap;
}

.callsign-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  font-weight: 500;
}
</style>

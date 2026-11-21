<script setup lang="ts">
import { computed } from 'vue'
import type { StatisticsDto } from '@/api/statisticsApi'
import { formatDateTime, timeAgo } from '@/utils/time'
import PacketsPerHourChart from './PacketsPerHourChart.vue'
import GridSquareMap from './GridSquareMap.vue'
import { stationTypeColor, stationTypeLabel } from './stationTypes'

const props = defineProps<{
  stats: StatisticsDto | null
  loading: boolean
  /** Epoch ms the stats were fetched at. */
  asOf: number
  now: number
  /** True while our receiver isn't decoding (RF lane warn/err). */
  rfDeaf: boolean
}>()

const zeros = () => Array.from<number>({ length: 24 }).fill(0)
const rf = computed(() => props.stats?.packetsPerHourRf ?? zeros())
const is = computed(() => props.stats?.packetsPerHourIs ?? zeros())
const rfTotal = computed(() => rf.value.reduce((a, b) => a + b, 0))
const isTotal = computed(() => is.value.reduce((a, b) => a + b, 0))

const tiles = computed(() => [
  { label: 'Packets today', value: props.stats?.packetsToday },
  { label: 'Stations today', value: props.stats?.uniqueStationsToday },
  { label: 'Stations this week', value: props.stats?.uniqueStationsThisWeek },
  { label: 'Stations all time', value: props.stats?.uniqueStationsAllTime },
])

const stationTo = (callsign: string) => `/stations/${encodeURIComponent(callsign)}`
</script>

<template>
  <div class="d-flex flex-column ga-4">
    <div class="tile-grid">
      <v-card v-for="t in tiles" :key="t.label" variant="outlined" class="pa-3">
        <div class="text-caption text-medium-emphasis">{{ t.label }}</div>
        <div class="tile-value tabular">{{ t.value?.toLocaleString() ?? '—' }}</div>
      </v-card>
    </div>

    <v-card variant="outlined">
      <v-card-title class="card-title d-flex align-center flex-wrap ga-2">
        Packets per hour
        <span class="text-caption text-medium-emphasis font-weight-regular">
          last 24 h · RF {{ rfTotal.toLocaleString() }} · APRS-IS {{ isTotal.toLocaleString() }}
        </span>
      </v-card-title>
      <v-card-text>
        <v-progress-linear v-if="loading && !stats" indeterminate class="mb-2" />
        <PacketsPerHourChart :rf="rf" :is="is" :as-of="asOf" />
        <div v-if="rfDeaf" class="text-caption text-warning mt-2">
          The receiver isn't decoding right now, so RF bars stay low while APRS-IS carries on.
        </div>
      </v-card-text>
    </v-card>

    <div class="two-col">
      <v-card variant="outlined">
        <v-card-title class="card-title">First heard recently</v-card-title>
        <v-list v-if="stats && stats.recentlyFirstHeard.length > 0" density="compact">
          <v-list-item v-for="s in stats.recentlyFirstHeard" :key="s.callsign">
            <v-list-item-title>
              <router-link class="callsign-link" :to="stationTo(s.callsign)">
                {{ s.callsign }}
              </router-link>
            </v-list-item-title>
            <template #append>
              <v-chip
                :color="stationTypeColor[s.stationType]"
                size="small"
                variant="tonal"
                label
                class="mr-2"
              >
                {{ stationTypeLabel[s.stationType] }}
              </v-chip>
              <span class="text-caption text-medium-emphasis" :title="formatDateTime(s.firstSeen)">
                {{ timeAgo(s.firstSeen, now) }}
              </span>
            </template>
          </v-list-item>
        </v-list>
        <v-card-text v-else class="text-medium-emphasis">No stations heard yet.</v-card-text>
      </v-card>

      <v-card variant="outlined">
        <v-card-title class="card-title">Busiest stations (beacon rate)</v-card-title>
        <div class="table-scroll">
          <v-table density="compact">
            <thead>
              <tr>
                <th>#</th>
                <th>Callsign</th>
                <th class="text-right">Avg/hr</th>
                <th class="text-right">Today</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(s, i) in stats?.busiestStations ?? []" :key="s.callsign">
                <td class="text-medium-emphasis">{{ i + 1 }}</td>
                <td>
                  <router-link class="callsign-link" :to="stationTo(s.callsign)">
                    {{ s.callsign }}
                  </router-link>
                </td>
                <td class="text-right tabular">{{ s.averagePerHour.toFixed(2) }}</td>
                <td class="text-right tabular">{{ s.count.toLocaleString() }}</td>
              </tr>
              <tr v-if="!stats || stats.busiestStations.length === 0">
                <td colspan="4" class="text-center text-medium-emphasis py-4">No data yet.</td>
              </tr>
            </tbody>
          </v-table>
        </div>
      </v-card>
    </div>

    <v-card variant="outlined">
      <v-card-title class="card-title d-flex align-center ga-2">
        Heard grid squares
        <v-chip v-if="stats" size="small" variant="tonal" color="success">
          {{ stats.gridSquares.length }}
        </v-chip>
      </v-card-title>
      <v-card-text class="pa-2">
        <GridSquareMap :grid-squares="stats?.gridSquares ?? []" />
      </v-card-text>
    </v-card>
  </div>
</template>

<style scoped>
.tile-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 700px) {
  .tile-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.two-col {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  align-items: start;
}

@media (max-width: 860px) {
  .two-col {
    grid-template-columns: minmax(0, 1fr);
  }
}

.tile-value {
  font-size: 22px;
  font-weight: 650;
  line-height: 1.3;
}

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
}

.callsign-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  font-weight: 500;
}
</style>

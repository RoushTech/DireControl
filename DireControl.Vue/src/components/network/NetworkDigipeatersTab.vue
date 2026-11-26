<script setup lang="ts">
import type { DigipeaterAnalysisEntry, HomePositionDto } from '@/types/station'
import { useUnits } from '@/composables/useUnits'
import { distanceKm, type RepeaterRow } from './repeaters'

const props = defineProps<{
  digipeaters: DigipeaterAnalysisEntry[]
  /** Digipeaters that repeated our own beacons, merged across radios. */
  repeaters: Map<string, RepeaterRow>
  home: HomePositionDto | null
  loading: boolean
}>()

const { formatDistance } = useUnits()

function distance(callsign: string): string {
  const r = props.repeaters.get(callsign)
  if (!props.home || !r || r.lat === null || r.lon === null) return '—'
  return formatDistance(distanceKm(props.home.lat, props.home.lon, r.lat, r.lon))
}

const stationTo = (callsign: string) => `/stations/${encodeURIComponent(callsign)}`
</script>

<template>
  <v-card variant="outlined">
    <v-card-title class="card-title d-flex align-center ga-2">
      Digipeaters near you
      <v-chip size="small" variant="tonal">{{ digipeaters.length }}</v-chip>
    </v-card-title>
    <v-progress-linear v-if="loading && digipeaters.length === 0" indeterminate />
    <div class="table-scroll">
      <v-table density="compact">
        <thead>
          <tr>
            <th>Digipeater</th>
            <th class="text-right">Forwarded</th>
            <th class="text-right">24 h</th>
            <th class="text-right">Avg hops from us</th>
            <th class="text-right">Distance</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in digipeaters" :key="d.callsign">
            <td>
              <router-link class="callsign-link" :to="stationTo(d.callsign)">
                {{ d.callsign }}
              </router-link>
            </td>
            <td class="text-right tabular">{{ d.totalPacketsForwarded.toLocaleString() }}</td>
            <td class="text-right tabular">{{ d.last24h.toLocaleString() }}</td>
            <td class="text-right tabular">{{ d.averageHopsFromUs.toFixed(2) }}</td>
            <td class="text-right tabular">{{ distance(d.callsign) }}</td>
            <td>
              <v-chip v-if="repeaters.has(d.callsign)" color="success" size="small" variant="tonal">
                repeats you
              </v-chip>
            </td>
          </tr>
          <tr v-if="!loading && digipeaters.length === 0">
            <td colspan="6" class="text-center text-medium-emphasis py-6">
              No digipeater activity recorded yet.
            </td>
          </tr>
        </tbody>
      </v-table>
    </div>
    <v-card-text class="text-caption text-medium-emphasis pt-2">
      Distance is shown for digipeaters that have repeated our beacons, from where they were heard.
    </v-card-text>
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

.callsign-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  font-weight: 500;
}
</style>

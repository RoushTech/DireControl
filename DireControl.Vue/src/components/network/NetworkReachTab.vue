<script setup lang="ts">
import type { RadioDto } from '@/types/radio'
import type { HomePositionDto } from '@/types/station'
import { useUnits } from '@/composables/useUnits'
import { distanceKm, type RepeaterRow } from './repeaters'

const props = defineProps<{
  /** Radios that have beaconed at least once. */
  radios: RadioDto[]
  repeatersByRadio: Map<string, RepeaterRow[]>
  home: HomePositionDto | null
  loading: boolean
  /** Our receiver can't currently hear digipeats, so new beacons can't be confirmed. */
  unverifiable: boolean
  /** Why, in a short phrase ("the receiver isn't decoding"). */
  unverifiableReason: string
}>()

const { formatDistance } = useUnits()

function confirmedPercent(radio: RadioDto): number {
  if (radio.beaconCount === 0) return 0
  return Math.min(100, (radio.confirmationCount / radio.beaconCount) * 100)
}

function repeaterDistance(r: RepeaterRow): string {
  if (!props.home || r.lat === null || r.lon === null) return '—'
  return formatDistance(distanceKm(props.home.lat, props.home.lon, r.lat, r.lon))
}

const stationTo = (callsign: string) => `/stations/${encodeURIComponent(callsign)}`
</script>

<template>
  <div class="d-flex flex-column ga-4">
    <v-alert v-if="unverifiable && radios.length > 0" type="warning" variant="tonal">
      <div class="font-weight-medium">Beacon reach is unverifiable right now</div>
      <div class="text-body-2">
        A beacon only counts as confirmed when our own receiver hears a digipeater repeat it.
        {{ unverifiableReason }}, so new beacons show as unconfirmed even if they were repeated. The
        figures below are as of the last time the receiver was working.
      </div>
    </v-alert>

    <v-card v-if="!loading && radios.length === 0" variant="outlined">
      <v-card-text class="text-medium-emphasis">
        No radio has beaconed yet. Enable auto-beacon on a radio to see who repeats you.
      </v-card-text>
      <v-card-actions>
        <v-btn variant="tonal" to="/settings/radios">Open radio settings</v-btn>
      </v-card-actions>
    </v-card>

    <div v-for="radio in radios" :key="radio.id" class="reach-grid">
      <v-card variant="outlined">
        <v-card-title class="card-title d-flex align-center flex-wrap ga-2">
          {{ radio.name }} beacon reach
          <v-chip v-if="unverifiable" color="warning" size="small" variant="tonal">
            Unverifiable now
          </v-chip>
        </v-card-title>
        <v-card-text class="d-flex align-center ga-5 flex-wrap">
          <v-progress-circular
            :model-value="confirmedPercent(radio)"
            :size="104"
            :width="9"
            :color="unverifiable ? 'warning' : 'success'"
          >
            <span class="text-body-2 font-weight-bold">
              {{ confirmedPercent(radio).toFixed(1) }}%
            </span>
          </v-progress-circular>
          <div class="reach-text">
            <div class="text-body-1 font-weight-medium">
              <span class="tabular">{{ radio.confirmationCount.toLocaleString() }}</span>
              of
              <span class="tabular">{{ radio.beaconCount.toLocaleString() }}</span>
              beacons heard back
            </div>
            <div class="text-caption text-medium-emphasis mt-1">
              Confirmation needs our own receiver to hear the digipeat.
              <template v-if="(repeatersByRadio.get(radio.id) ?? []).length > 0">
                Median repeat delay
                {{ repeatersByRadio.get(radio.id)![0]!.medianSeconds.toFixed(1) }} s via
                {{ repeatersByRadio.get(radio.id)![0]!.callsign }}.
              </template>
            </div>
          </div>
        </v-card-text>
      </v-card>

      <v-card variant="outlined">
        <v-card-title class="card-title">
          Who repeats you · last {{ Math.min(200, radio.beaconCount) }}
        </v-card-title>
        <div class="table-scroll">
          <v-table density="compact">
            <thead>
              <tr>
                <th>Digipeater</th>
                <th class="text-right">Repeats</th>
                <th class="text-right">Median</th>
                <th class="text-right">Distance</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="r in repeatersByRadio.get(radio.id) ?? []" :key="r.callsign">
                <td>
                  <router-link class="callsign-link" :to="stationTo(r.callsign)">
                    {{ r.callsign }}
                  </router-link>
                </td>
                <td class="text-right tabular">{{ r.count.toLocaleString() }}</td>
                <td class="text-right tabular">{{ r.medianSeconds.toFixed(1) }} s</td>
                <td class="text-right tabular">{{ repeaterDistance(r) }}</td>
              </tr>
              <tr v-if="(repeatersByRadio.get(radio.id) ?? []).length === 0">
                <td colspan="4" class="text-center text-medium-emphasis py-4">
                  No digipeater has been heard repeating these beacons.
                </td>
              </tr>
            </tbody>
          </v-table>
        </div>
      </v-card>
    </div>
  </div>
</template>

<style scoped>
.reach-grid {
  display: grid;
  grid-template-columns: minmax(0, 7fr) minmax(0, 5fr);
  gap: 16px;
  align-items: start;
}

@media (max-width: 860px) {
  .reach-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.reach-text {
  flex: 1 1 200px;
  min-width: 0;
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

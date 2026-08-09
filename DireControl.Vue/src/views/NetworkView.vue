<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { getDigipeaterAnalysis } from '@/api/statisticsApi'
import { getWatchList, toggleWatch } from '@/api/stationsApi'
import { getRadios, getBeaconHistory } from '@/api/radiosApi'
import type { DigipeaterAnalysisEntry, StationDto } from '@/types/station'
import type { RadioDto } from '@/types/radio'
import { timeAgo, formatUtc } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'
import { useToastStore } from '@/stores/toastStore'

const router = useRouter()
const toastStore = useToastStore()
const { now } = useTick(5000)

const loading = ref(false)
const loadFailed = ref(false)
const loadedAt = ref<number | null>(null)

const digipeaters = ref<DigipeaterAnalysisEntry[]>([])
const watchlist = ref<StationDto[]>([])
const radios = ref<RadioDto[]>([])

/** Which digipeaters actually repeat our beacons, from recent beacon history. */
interface RepeaterStat {
  count: number
  seconds: number[]
}
const repeaterStats = ref<Map<string, RepeaterStat>>(new Map())

const beaconRadios = computed(() => radios.value.filter((r) => r.beaconCount > 0))

const topRepeaters = computed(() =>
  [...repeaterStats.value.entries()]
    .map(([callsign, s]) => {
      const sorted = [...s.seconds].sort((a, b) => a - b)
      return {
        callsign,
        count: s.count,
        medianSeconds: sorted[Math.floor(sorted.length / 2)] ?? 0,
      }
    })
    .sort((a, b) => b.count - a.count)
    .slice(0, 5),
)

const repeatsUs = computed(() => new Set(repeaterStats.value.keys()))

function confirmedPercent(radio: RadioDto): number {
  if (radio.beaconCount === 0) return 0
  return Math.min(100, (radio.confirmationCount / radio.beaconCount) * 100)
}

const asOfLabel = computed(() => {
  if (loadedAt.value === null) return ''
  return `as of ${timeAgo(new Date(loadedAt.value).toISOString(), now.value)}`
})

async function load() {
  loading.value = true
  try {
    const [digis, watched, radioList] = await Promise.all([
      getDigipeaterAnalysis(),
      getWatchList(),
      getRadios(),
    ])
    digipeaters.value = digis
    watchlist.value = watched
    radios.value = radioList

    // Aggregate which digis confirmed our recent beacons (last 200 per radio).
    const stats = new Map<string, RepeaterStat>()
    for (const radio of radioList.filter((r) => r.beaconCount > 0)) {
      try {
        const history = await getBeaconHistory(radio.id, 200)
        for (const beacon of history) {
          for (const c of beacon.confirmations) {
            const entry = stats.get(c.digipeater) ?? { count: 0, seconds: [] }
            entry.count++
            entry.seconds.push(c.secondsAfterBeacon)
            stats.set(c.digipeater, entry)
          }
        }
      } catch {
        // history is supplementary — the page still works without it
      }
    }
    repeaterStats.value = stats

    loadedAt.value = serverNow()
    loadFailed.value = false
  } catch {
    loadFailed.value = true
  } finally {
    loading.value = false
  }
}

function goToStation(callsign: string) {
  router.push(`/stations/${encodeURIComponent(callsign)}`)
}

const unwatching = ref<Record<string, boolean>>({})

async function unwatch(station: StationDto) {
  unwatching.value[station.callsign] = true
  try {
    await toggleWatch(station.callsign)
    watchlist.value = watchlist.value.filter((s) => s.callsign !== station.callsign)
    toastStore.toast(`${station.callsign} removed from watchlist`, 'info')
  } catch {
    toastStore.toast(`Couldn't update the watchlist — the backend may be unreachable.`, 'error')
  } finally {
    unwatching.value[station.callsign] = false
  }
}

onMounted(load)
</script>

<template>
  <div class="network-view pa-4">
    <div class="d-flex align-center ga-3 mb-4 flex-wrap">
      <span class="text-h5 font-weight-bold">Network</span>
      <span v-if="asOfLabel" class="text-caption text-medium-emphasis">{{ asOfLabel }}</span>
      <v-spacer />
      <v-btn
        size="small"
        variant="tonal"
        prepend-icon="mdi-refresh"
        :loading="loading"
        @click="load"
      >
        Refresh
      </v-btn>
    </div>

    <v-alert v-if="loadFailed" type="error" density="compact" class="mb-4">
      Couldn't load network data — the backend may be unreachable.
      <template #append>
        <v-btn size="small" variant="text" @click="load">Retry</v-btn>
      </template>
    </v-alert>

    <div class="network-grid">
      <!-- Digipeater leaderboard -->
      <v-card variant="outlined">
        <v-card-title class="text-subtitle-1 d-flex align-center ga-2">
          Digipeaters near you
          <v-chip size="x-small" variant="tonal">{{ digipeaters.length }}</v-chip>
        </v-card-title>
        <v-progress-linear v-if="loading && digipeaters.length === 0" indeterminate />
        <div class="table-scroll">
          <v-table density="compact">
            <thead>
              <tr>
                <th>Callsign</th>
                <th class="text-right">Forwarded</th>
                <th class="text-right">24h</th>
                <th class="text-right">Avg hops from us</th>
                <th />
              </tr>
            </thead>
            <tbody>
              <tr v-for="d in digipeaters" :key="d.callsign">
                <td>
                  <a class="callsign-link" @click.prevent="goToStation(d.callsign)">
                    {{ d.callsign }}
                  </a>
                </td>
                <td class="text-right tabular">{{ d.totalPacketsForwarded.toLocaleString() }}</td>
                <td class="text-right tabular">{{ d.last24h.toLocaleString() }}</td>
                <td class="text-right tabular">{{ d.averageHopsFromUs.toFixed(2) }}</td>
                <td>
                  <v-chip
                    v-if="repeatsUs.has(d.callsign)"
                    color="success"
                    size="x-small"
                    variant="tonal"
                  >
                    repeats you
                  </v-chip>
                </td>
              </tr>
              <tr v-if="!loading && digipeaters.length === 0">
                <td colspan="5" class="text-center text-medium-emphasis py-6">
                  No digipeater activity recorded yet.
                </td>
              </tr>
            </tbody>
          </v-table>
        </div>
      </v-card>

      <div class="d-flex flex-column ga-4">
        <!-- Beacon reach per beaconing radio -->
        <v-card v-for="radio in beaconRadios" :key="radio.id" variant="outlined" class="pa-4">
          <div class="d-flex align-center ga-2 mb-3">
            <span class="text-subtitle-1 font-weight-medium">Beacon reach</span>
            <v-chip size="x-small" variant="tonal">{{ radio.name }}</v-chip>
          </div>
          <div class="d-flex align-center ga-4 flex-wrap">
            <v-progress-circular
              :model-value="confirmedPercent(radio)"
              :size="86"
              :width="8"
              color="success"
            >
              <span class="text-caption font-weight-bold">
                {{ confirmedPercent(radio).toFixed(1) }}%
              </span>
            </v-progress-circular>
            <div>
              <div class="text-body-2">
                <strong class="tabular">{{ radio.confirmationCount.toLocaleString() }}</strong>
                of
                <strong class="tabular">{{ radio.beaconCount.toLocaleString() }}</strong>
                beacons confirmed heard
              </div>
              <div v-if="topRepeaters.length > 0" class="text-caption text-medium-emphasis mt-1">
                Most often first-repeated by
                <a class="callsign-link" @click.prevent="goToStation(topRepeaters[0]!.callsign)">
                  {{ topRepeaters[0]!.callsign }}
                </a>
                (median {{ topRepeaters[0]!.medianSeconds.toFixed(1) }}s)
              </div>
            </div>
          </div>
          <div v-if="topRepeaters.length > 1" class="mt-3">
            <div class="text-caption text-medium-emphasis mb-1">
              Confirming digipeaters (last {{ Math.min(200, radio.beaconCount) }} beacons)
            </div>
            <div class="d-flex flex-wrap ga-1">
              <v-chip
                v-for="r in topRepeaters"
                :key="r.callsign"
                size="x-small"
                variant="tonal"
                color="rf"
                @click="goToStation(r.callsign)"
              >
                {{ r.callsign }} · {{ r.count }}
              </v-chip>
            </div>
          </div>
        </v-card>

        <v-card v-if="!loading && beaconRadios.length === 0" variant="outlined" class="pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-1">Beacon reach</div>
          <div class="text-caption text-medium-emphasis">
            No radio has beaconed yet — enable auto-beacon in Settings → Radios.
          </div>
        </v-card>

        <!-- Watchlist -->
        <v-card variant="outlined">
          <v-card-title class="text-subtitle-1 d-flex align-center ga-2">
            Watchlist
            <v-chip size="x-small" variant="tonal" color="primary">{{ watchlist.length }}</v-chip>
          </v-card-title>
          <v-card-text v-if="watchlist.length === 0" class="text-medium-emphasis">
            No watched stations. Star a station from its detail panel to get an alert when it comes
            back on the air.
          </v-card-text>
          <v-list v-else density="compact">
            <v-list-item v-for="s in watchlist" :key="s.callsign">
              <template #prepend>
                <v-icon color="amber" size="18">mdi-star</v-icon>
              </template>
              <v-list-item-title>
                <a class="callsign-link" @click.prevent="goToStation(s.callsign)">
                  {{ s.callsign }}
                </a>
              </v-list-item-title>
              <v-list-item-subtitle :title="formatUtc(s.lastSeen)">
                heard {{ timeAgo(s.lastSeen, now) }}
              </v-list-item-subtitle>
              <template #append>
                <v-chip
                  color="success"
                  size="x-small"
                  variant="tonal"
                  class="mr-1"
                  title="Alerts when this station comes back on the air"
                >
                  alerting
                </v-chip>
                <v-btn
                  icon="mdi-star-off"
                  size="x-small"
                  variant="text"
                  title="Remove from watchlist"
                  :loading="unwatching[s.callsign]"
                  @click="unwatch(s)"
                />
              </template>
            </v-list-item>
          </v-list>
        </v-card>
      </div>
    </div>
  </div>
</template>

<style scoped>
.network-view {
  height: 100%;
  overflow-y: auto;
}

.network-grid {
  display: grid;
  grid-template-columns: minmax(380px, 3fr) minmax(300px, 2fr);
  gap: 16px;
  align-items: start;
}

@media (max-width: 860px) {
  .network-grid {
    grid-template-columns: 1fr;
  }
}

.table-scroll {
  overflow-x: auto;
}

.tabular {
  font-variant-numeric: tabular-nums;
}

.callsign-link {
  color: rgba(var(--v-theme-primary), 1);
  cursor: pointer;
  text-decoration: none;
  font-weight: 500;
}

.callsign-link:hover {
  text-decoration: underline;
}
</style>

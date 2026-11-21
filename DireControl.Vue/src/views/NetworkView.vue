<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  getDigipeaterAnalysis,
  getStationFrequencies,
  getStatistics,
  type StatisticsDto,
} from '@/api/statisticsApi'
import { getSettings, getWatchList } from '@/api/stationsApi'
import { getRadios, getBeaconHistory } from '@/api/radiosApi'
import type {
  DigipeaterAnalysisEntry,
  HomePositionDto,
  StationDto,
  StationFrequencyDto,
} from '@/types/station'
import type { RadioDto } from '@/types/radio'
import { formatTimeOfDay } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'
import { useHealthStore } from '@/stores/health'
import NetworkOverviewTab from '@/components/network/NetworkOverviewTab.vue'
import NetworkReachTab from '@/components/network/NetworkReachTab.vue'
import NetworkDigipeatersTab from '@/components/network/NetworkDigipeatersTab.vue'
import NetworkFrequenciesTab from '@/components/network/NetworkFrequenciesTab.vue'
import NetworkWatchListTab from '@/components/network/NetworkWatchListTab.vue'
import type { RepeaterRow } from '@/components/network/repeaters'

const route = useRoute()
const router = useRouter()
const healthStore = useHealthStore()
const { now } = useTick(5000)

// ---- Tabs, driven by ?tab= so deep links work ----

const TABS = [
  { value: 'overview', label: 'Overview' },
  { value: 'reach', label: 'Beacon reach' },
  { value: 'digipeaters', label: 'Digipeaters' },
  { value: 'frequencies', label: 'Frequencies' },
  { value: 'watch', label: 'Watch list' },
] as const
type TabKey = (typeof TABS)[number]['value']

const tab = computed<TabKey>({
  get() {
    const q = route.query.tab
    return TABS.find((t) => t.value === q)?.value ?? 'overview'
  },
  set(value) {
    if (route.query.tab === value) return
    void router.replace({ query: { ...route.query, tab: value } })
  },
})

// ---- Health: reach can't be confirmed while we can't hear digipeats ----

const rfLane = computed(() => healthStore.health.lanes.rf)
const rfDeaf = computed(() => rfLane.value.level === 'warn' || rfLane.value.level === 'err')
const reachUnverifiable = computed(() => rfDeaf.value || !healthStore.health.live)
const unverifiableReason = computed(() =>
  !healthStore.health.live
    ? 'The live feed is down'
    : `The receiver isn't decoding (${rfLane.value.value})`,
)

// ---- Data ----

const loading = ref(false)
const loadFailed = ref(false)
const loadedAt = ref<number | null>(null)

const stats = ref<StatisticsDto | null>(null)
const frequencies = ref<StationFrequencyDto[]>([])
const digipeaters = ref<DigipeaterAnalysisEntry[]>([])
const watchlist = ref<StationDto[]>([])
const radios = ref<RadioDto[]>([])
const home = ref<HomePositionDto | null>(null)
const repeatersByRadio = ref<Map<string, RepeaterRow[]>>(new Map())

const beaconRadios = computed(() => radios.value.filter((r) => r.beaconCount > 0))

/** Every digipeater that repeated any of our radios' beacons. */
const allRepeaters = computed(() => {
  const merged = new Map<string, RepeaterRow>()
  for (const rows of repeatersByRadio.value.values()) {
    for (const r of rows) {
      const existing = merged.get(r.callsign)
      if (!existing) merged.set(r.callsign, { ...r })
      else {
        existing.count += r.count
        existing.lat ??= r.lat
        existing.lon ??= r.lon
      }
    }
  }
  return merged
})

// Caption style: the data timestamp, not a ticking relative time.
const asOfLabel = computed(() =>
  loadedAt.value === null ? '' : `as of ${formatTimeOfDay(loadedAt.value)}`,
)

/** Which digipeaters confirmed this radio's recent beacons (last 200). */
async function loadRepeaters(radio: RadioDto): Promise<RepeaterRow[]> {
  const byCall = new Map<string, { seconds: number[]; lat: number | null; lon: number | null }>()
  const history = await getBeaconHistory(radio.id, 200)
  for (const beacon of history) {
    for (const c of beacon.confirmations) {
      const entry = byCall.get(c.digipeater) ?? { seconds: [], lat: null, lon: null }
      entry.seconds.push(c.secondsAfterBeacon)
      if (c.lat !== null && c.lon !== null) {
        entry.lat = c.lat
        entry.lon = c.lon
      }
      byCall.set(c.digipeater, entry)
    }
  }
  return [...byCall.entries()]
    .map(([callsign, e]) => {
      const sorted = [...e.seconds].sort((a, b) => a - b)
      return {
        callsign,
        count: e.seconds.length,
        medianSeconds: sorted[Math.floor(sorted.length / 2)] ?? 0,
        lat: e.lat,
        lon: e.lon,
      }
    })
    .sort((a, b) => b.count - a.count)
}

async function load() {
  loading.value = true
  try {
    const [statsResult, freqs, digis, watched, radioList] = await Promise.all([
      getStatistics(),
      getStationFrequencies(),
      getDigipeaterAnalysis(),
      getWatchList(),
      getRadios(),
    ])
    stats.value = statsResult
    frequencies.value = freqs
    digipeaters.value = digis
    watchlist.value = watched
    radios.value = radioList
    loadedAt.value = serverNow()
    loadFailed.value = false
  } catch {
    loadFailed.value = true
  } finally {
    loading.value = false
  }

  // Supplementary — the page still works without these.
  try {
    home.value = (await getSettings()).homePosition
  } catch {
    /* distances show as — */
  }
  const byRadio = new Map<string, RepeaterRow[]>()
  await Promise.all(
    beaconRadios.value.map(async (radio) => {
      try {
        byRadio.set(radio.id, await loadRepeaters(radio))
      } catch {
        /* history unavailable for this radio */
      }
    }),
  )
  repeatersByRadio.value = byRadio
}

function onUnwatched(callsign: string) {
  watchlist.value = watchlist.value.filter((s) => s.callsign !== callsign)
}

onMounted(load)
</script>

<template>
  <div class="network-view pa-4">
    <div class="d-flex align-center ga-3 mb-3 flex-wrap">
      <div>
        <div class="page-title">Network</div>
        <div v-if="asOfLabel" class="text-caption text-medium-emphasis">{{ asOfLabel }}</div>
      </div>
      <v-spacer />
      <v-btn variant="tonal" prepend-icon="mdi-refresh" :loading="loading" @click="load">
        Refresh
      </v-btn>
    </div>

    <v-alert v-if="loadFailed" type="error" variant="tonal" density="compact" class="mb-4">
      Couldn't load network data. The backend may be unreachable.
      <template #append>
        <v-btn variant="text" @click="load">Retry</v-btn>
      </template>
    </v-alert>

    <v-tabs v-model="tab" show-arrows color="primary" class="mb-4">
      <v-tab v-for="t in TABS" :key="t.value" :value="t.value">{{ t.label }}</v-tab>
    </v-tabs>

    <v-window v-model="tab" :touch="false">
      <v-window-item value="overview">
        <NetworkOverviewTab
          :stats="stats"
          :loading="loading"
          :as-of="loadedAt ?? now"
          :now="now"
          :rf-deaf="rfDeaf"
        />
      </v-window-item>
      <v-window-item value="reach">
        <NetworkReachTab
          :radios="beaconRadios"
          :repeaters-by-radio="repeatersByRadio"
          :home="home"
          :loading="loading"
          :unverifiable="reachUnverifiable"
          :unverifiable-reason="unverifiableReason"
        />
      </v-window-item>
      <v-window-item value="digipeaters">
        <NetworkDigipeatersTab
          :digipeaters="digipeaters"
          :repeaters="allRepeaters"
          :home="home"
          :loading="loading"
        />
      </v-window-item>
      <v-window-item value="frequencies">
        <NetworkFrequenciesTab :frequencies="frequencies" :loading="loading" :now="now" />
      </v-window-item>
      <v-window-item value="watch">
        <NetworkWatchListTab :watchlist="watchlist" :now="now" @removed="onUnwatched" />
      </v-window-item>
    </v-window>
  </div>
</template>

<style scoped>
.network-view {
  height: 100%;
  overflow-y: auto;
}

/* Type scale: page h2 17px/650. */
.page-title {
  font-size: 17px;
  font-weight: 650;
  line-height: 1.3;
}
</style>

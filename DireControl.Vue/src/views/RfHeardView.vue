<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  LineElement,
  PointElement,
  LineController,
  Tooltip as ChartTooltip,
  Legend,
} from 'chart.js'
import { useTheme } from 'vuetify'
import {
  getRfHeardDaily,
  getRfHeardStations,
  getRfHeardStatus,
  getRfHeardSummary,
} from '@/api/rfHeardApi'
import type {
  RfHeardDailyDto,
  RfHeardStationDto,
  RfHeardStatusDto,
  RfHeardSummaryDto,
} from '@/types/rfHeard'
import { StationType } from '@/types/station'
import { timeAgo } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'

ChartJS.register(
  CategoryScale,
  LinearScale,
  LineElement,
  PointElement,
  LineController,
  ChartTooltip,
  Legend,
)

const theme = useTheme()
const { now } = useTick(5000)

const summary = ref<RfHeardSummaryDto[]>([])
const daily = ref<RfHeardDailyDto[]>([])
const stations = ref<RfHeardStationDto[]>([])
const loading = ref(false)
const error = ref(false)
const loadedAt = ref<number | null>(null)
const status = ref<RfHeardStatusDto | null>(null)

// ---- Controls ----

const RANGES = [
  { title: '7 days', value: 7 },
  { title: '30 days', value: 30 },
  { title: '90 days', value: 90 },
  { title: '1 year', value: 365 },
]

/** Every metric the daily rollup records, plotted the same way. */
const METRICS = [
  { title: 'Unique stations', value: 'uniqueDirectStations', unit: '' },
  { title: 'New stations', value: 'newDirectStations', unit: '' },
  { title: 'Farthest heard', value: 'maxDirectDistanceKm', unit: ' km' },
  { title: 'Median distance', value: 'medianDirectDistanceKm', unit: ' km' },
] as const

type MetricKey = (typeof METRICS)[number]['value']

const range = ref(30)
const metric = ref<MetricKey>('uniqueDirectStations')
/** null = all radios. */
const channel = ref<number | null>(null)

const metricUnit = computed(() => METRICS.find((m) => m.value === metric.value)?.unit ?? '')

const radioOptions = computed(() => [
  { title: 'All radios', value: null },
  ...summary.value.map((s) => ({ title: s.radioName, value: s.channelNumber })),
])

const asOfLabel = computed(() =>
  loadedAt.value === null ? '' : `as of ${timeAgo(new Date(loadedAt.value).toISOString(), now.value)}`,
)

// ---- Fetch ----

async function load() {
  loading.value = true
  error.value = false
  try {
    const [summaryResult, dailyResult, stationResult, statusResult] = await Promise.all([
      getRfHeardSummary(),
      getRfHeardDaily(range.value, channel.value ?? undefined),
      getRfHeardStations(channel.value ?? undefined),
      getRfHeardStatus(),
    ])
    summary.value = summaryResult
    daily.value = dailyResult
    stations.value = stationResult
    status.value = statusResult
    loadedAt.value = serverNow()
  } catch {
    error.value = true
  } finally {
    loading.value = false
  }
}

// ---- Chart ----

// Follow the active Vuetify theme so the lines, grid, and ticks stay legible in dark mode
// instead of falling back to Chart.js's light defaults.
const chartInk = computed(() =>
  theme.global.current.value.dark ? 'rgba(230, 237, 243, 0.75)' : 'rgba(27, 39, 51, 0.75)',
)
const chartGrid = computed(() =>
  theme.global.current.value.dark ? 'rgba(230, 237, 243, 0.10)' : 'rgba(27, 39, 51, 0.10)',
)

/** One stable colour per radio, so a series keeps its colour as the range changes. */
const SERIES_COLORS = ['#42A5F5', '#66BB6A', '#FFA726', '#AB47BC', '#EF5350', '#26C6DA']

const chartData = computed(() => {
  const days = [...new Set(daily.value.map((d) => d.day))].sort()
  const channels = [...new Set(daily.value.map((d) => d.channelNumber))].sort((a, b) => a - b)

  const byKey = new Map(daily.value.map((d) => [`${d.channelNumber}|${d.day}`, d]))

  return {
    labels: days.map((d) => d.slice(5)), // MM-DD — the year is implied by the range
    datasets: channels.map((ch, i) => {
      const color = SERIES_COLORS[i % SERIES_COLORS.length]!
      return {
        label: daily.value.find((d) => d.channelNumber === ch)?.radioName ?? `Channel ${ch}`,
        data: days.map((day) => byKey.get(`${ch}|${day}`)?.[metric.value] ?? null),
        borderColor: color,
        backgroundColor: `${color}33`,
        borderWidth: 2,
        pointRadius: days.length > 60 ? 0 : 2,
        pointHoverRadius: 4,
        tension: 0.2,
        // Distances are genuinely absent on days nothing positioned was heard; bridging
        // the gap would draw a line through data that does not exist.
        spanGaps: false,
      }
    }),
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: false as const,
  interaction: { mode: 'index' as const, intersect: false },
  plugins: {
    legend: {
      display: true,
      position: 'bottom' as const,
      labels: { color: chartInk.value, boxWidth: 12, font: { size: 11 } },
    },
    tooltip: { enabled: true as const },
  },
  scales: {
    x: {
      ticks: {
        color: chartInk.value,
        font: { size: 10 },
        maxRotation: 0,
        autoSkip: true,
        maxTicksLimit: 12,
      },
      grid: { color: chartGrid.value },
    },
    y: {
      beginAtZero: true,
      ticks: {
        color: chartInk.value,
        font: { size: 10 },
        callback: (value: number | string) => `${value}${metricUnit.value}`,
      },
      grid: { color: chartGrid.value },
    },
  },
}))

const hasChartData = computed(() =>
  daily.value.some((d) => d.uniqueDirectStations > 0 || d.directPackets > 0),
)

// ---- Station table ----

const stationSearch = ref('')

const stationHeaders = [
  { title: 'Callsign', key: 'callsign' },
  { title: 'Radio', key: 'radioName' },
  { title: 'First heard', key: 'firstHeardDirect' },
  { title: 'Last heard', key: 'lastHeardDirect' },
  { title: 'Packets', key: 'directPacketCount', align: 'end' as const },
  { title: 'Distance', key: 'distanceKm', align: 'end' as const },
]

const typeLabel: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Unknown]: 'Unknown',
  [StationType.Gateway]: 'Gateway',
}

const backfillPercent = computed(() => {
  const st = status.value
  if (!st) return 0
  const total = st.packetsClassified + st.packetsRemaining
  return total === 0 ? 100 : Math.floor((st.packetsClassified / total) * 100)
})

function formatKm(km: number | null): string {
  return km === null ? '—' : `${km.toFixed(1)} km`
}

onMounted(load)
</script>

<template>
  <v-container fluid class="rf-heard-view pa-4">
    <div class="d-flex align-center ga-3 mb-4 flex-wrap">
      <div class="text-h5 font-weight-bold">RF Heard</div>
      <span class="text-caption text-medium-emphasis">
        Stations heard directly on RF — no digipeater in between
      </span>
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

    <v-alert v-if="error" type="warning" variant="tonal" density="compact" class="mb-4">
      Could not load RF reception data. Is the API running?
    </v-alert>

    <!-- Until the one-time sweep finishes, everything below is drawn from a partial view of
         the stored packets. Saying so is the difference between "still working" and "wrong". -->
    <v-alert
      v-if="status?.backfillInProgress"
      type="info"
      variant="tonal"
      density="compact"
      class="mb-4"
    >
      <div class="d-flex align-center ga-3 flex-wrap">
        <span>
          Classifying stored packets — {{ backfillPercent }}% done,
          {{ status.packetsRemaining.toLocaleString() }} to go. Counts below cover only what
          has been classified so far and will keep growing; the daily trend appears once this
          finishes.
        </span>
      </div>
      <v-progress-linear
        :model-value="backfillPercent"
        height="4"
        rounded
        class="mt-2"
      />
    </v-alert>

    <!-- Per-radio summary -->
    <v-row class="mb-2">
      <v-col v-for="s in summary" :key="s.channelNumber" cols="12" sm="6" md="4">
        <v-card variant="outlined" class="summary-card">
          <v-card-text class="pa-3">
            <div class="d-flex align-center ga-2 mb-2">
              <v-icon size="small" icon="mdi-antenna" />
              <span class="text-body-2 font-weight-medium">{{ s.radioName }}</span>
              <v-chip size="x-small" variant="tonal" class="ml-auto">
                ch {{ s.channelNumber }}
              </v-chip>
            </div>
            <div class="d-flex text-center">
              <div class="flex-1-1">
                <div class="text-h6 font-weight-bold">{{ s.uniqueDirectToday }}</div>
                <div class="text-caption text-medium-emphasis">today</div>
              </div>
              <div class="flex-1-1">
                <div class="text-h6 font-weight-bold">{{ s.uniqueDirect7d }}</div>
                <div class="text-caption text-medium-emphasis">7 days</div>
              </div>
              <div class="flex-1-1">
                <div class="text-h6 font-weight-bold">{{ s.uniqueDirect30d }}</div>
                <div class="text-caption text-medium-emphasis">30 days</div>
              </div>
              <div class="flex-1-1">
                <div class="text-h6 font-weight-bold">{{ s.uniqueDirectAllTime }}</div>
                <div class="text-caption text-medium-emphasis">all time</div>
              </div>
            </div>
            <v-divider class="my-2" />
            <div class="d-flex justify-space-between text-caption text-medium-emphasis">
              <span>Farthest ever: {{ formatKm(s.bestDistanceKm) }}</span>
              <span v-if="s.lastHeardDirect">Last: {{ timeAgo(s.lastHeardDirect, now) }}</span>
            </div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col v-if="summary.length === 0 && !loading" cols="12">
        <v-alert type="info" variant="tonal" density="compact">
          Nothing heard directly on RF yet. Reception is rolled up every few minutes — if this
          is a fresh install, give it a moment.
        </v-alert>
      </v-col>
    </v-row>

    <!-- Trend -->
    <v-card variant="outlined" class="mb-4">
      <v-card-text class="pa-3">
        <div class="d-flex align-center ga-3 mb-3 flex-wrap">
          <v-select
            v-model="metric"
            :items="METRICS"
            label="Metric"
            density="compact"
            variant="outlined"
            hide-details
            style="max-width: 200px"
          />
          <v-select
            v-model="range"
            :items="RANGES"
            label="Range"
            density="compact"
            variant="outlined"
            hide-details
            style="max-width: 160px"
            @update:model-value="load"
          />
          <v-select
            v-model="channel"
            :items="radioOptions"
            label="Radio"
            density="compact"
            variant="outlined"
            hide-details
            style="max-width: 220px"
            @update:model-value="load"
          />
        </div>

        <div v-if="hasChartData" class="chart-wrap">
          <Line :data="chartData" :options="chartOptions" />
        </div>
        <div v-else class="text-center text-caption text-medium-emphasis py-8">
          No direct receptions recorded in this range.
        </div>
      </v-card-text>
    </v-card>

    <!-- Stations heard direct -->
    <v-card variant="outlined">
      <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1 d-flex align-center ga-2">
        Stations heard direct
        <v-chip size="x-small" color="green" variant="tonal">{{ stations.length }}</v-chip>
        <v-spacer />
        <v-text-field
          v-model="stationSearch"
          density="compact"
          variant="outlined"
          hide-details
          placeholder="Filter callsign"
          prepend-inner-icon="mdi-magnify"
          style="max-width: 220px"
        />
      </v-card-title>
      <v-card-text class="pa-0">
        <v-data-table
          :headers="stationHeaders"
          :items="stations"
          :search="stationSearch"
          density="compact"
          :items-per-page="25"
        >
          <template #[`item.callsign`]="{ item }">
            <router-link
              :to="`/stations/${encodeURIComponent(item.callsign)}`"
              class="callsign-link"
            >
              {{ item.callsign }}
            </router-link>
            <v-chip
              v-if="item.stationType !== StationType.Unknown"
              size="x-small"
              variant="tonal"
              class="ml-2"
            >
              {{ typeLabel[item.stationType] }}
            </v-chip>
          </template>
          <template #[`item.firstHeardDirect`]="{ item }">
            <span class="text-caption text-medium-emphasis">
              {{ timeAgo(item.firstHeardDirect, now) }}
            </span>
          </template>
          <template #[`item.lastHeardDirect`]="{ item }">
            <span class="text-caption text-medium-emphasis">
              {{ timeAgo(item.lastHeardDirect, now) }}
            </span>
          </template>
          <template #[`item.distanceKm`]="{ item }">
            {{ formatKm(item.distanceKm) }}
          </template>
        </v-data-table>
      </v-card-text>
    </v-card>
  </v-container>
</template>

<style scoped>
.rf-heard-view {
  height: 100%;
  overflow-y: auto;
}

.summary-card {
  height: 100%;
}

.chart-wrap {
  height: 260px;
}

.callsign-link {
  color: rgb(var(--v-theme-primary));
  font-weight: 500;
  text-decoration: none;
}

.callsign-link:hover {
  text-decoration: underline;
}
</style>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { Bar } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Tooltip as ChartTooltip,
  Legend,
} from 'chart.js'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { getStatistics, getDigipeaterAnalysis } from '@/api/statisticsApi'
import { StationType, type StatisticsDto, type DigipeaterAnalysisEntry } from '@/types/station'
import { timeAgo } from '@/utils/time'

ChartJS.register(CategoryScale, LinearScale, BarElement, ChartTooltip, Legend)

const stats = ref<StatisticsDto | null>(null)
const digiAnalysis = ref<DigipeaterAnalysisEntry[]>([])
const loading = ref(false)
const error = ref(false)

const mapEl = ref<HTMLElement | null>(null)
let leafletMap: L.Map | null = null
let gridLayers: L.Rectangle[] = []

// ---- Fetch ----

async function load() {
  loading.value = true
  error.value = false
  try {
    const [statsResult, digiResult] = await Promise.all([
      getStatistics(),
      getDigipeaterAnalysis(),
    ])
    stats.value = statsResult
    digiAnalysis.value = digiResult
  } catch {
    error.value = true
  } finally {
    loading.value = false
  }
}

// ---- Bar chart ----

const barOptions = {
  responsive: true,
  maintainAspectRatio: false,
  animation: false as const,
  plugins: {
    legend: { display: false },
    tooltip: { enabled: true as const },
  },
  scales: {
    x: {
      display: true,
      ticks: { font: { size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 12 },
    },
    y: {
      beginAtZero: true,
      ticks: { font: { size: 10 } },
    },
  },
}

const barData = computed(() => {
  const counts = stats.value?.packetsPerHour ?? Array.from<number>({ length: 24 }).fill(0)
  const now = new Date()
  const labels = Array.from({ length: 24 }, (_, i) => {
    const h = (now.getHours() - 23 + i + 24) % 24
    return `${h}:00`
  })
  return {
    labels,
    datasets: [
      {
        label: 'Packets',
        data: counts,
        backgroundColor: 'rgba(66, 165, 245, 0.6)',
        borderColor: '#42A5F5',
        borderWidth: 1,
      },
    ],
  }
})

// ---- Station type labels ----

const typeLabel: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Unknown]: 'Unknown',
}
const typeColor: Record<StationType, string> = {
  [StationType.Fixed]: 'blue',
  [StationType.Mobile]: 'green',
  [StationType.Weather]: 'teal',
  [StationType.Digipeater]: 'orange',
  [StationType.IGate]: 'purple',
  [StationType.Unknown]: 'grey',
}

// ---- Maidenhead grid → lat/lon bounds ----

function gridSquareToBounds(grid: string): [[number, number], [number, number]] | null {
  if (grid.length < 4) return null

  const c0 = grid.charCodeAt(0) - 65
  const c1 = grid.charCodeAt(1) - 65
  if (c0 < 0 || c0 > 17 || c1 < 0 || c1 > 17) return null

  const lonField = c0 * 20 - 180
  const latField = c1 * 10 - 90

  const d2 = parseInt(grid.charAt(2), 10)
  const d3 = parseInt(grid.charAt(3), 10)
  if (isNaN(d2) || isNaN(d3)) return null

  const lon = lonField + d2 * 2
  const lat = latField + d3

  return [
    [lat, lon],
    [lat + 1, lon + 2],
  ]
}

// ---- Leaflet grid map ----

function initMap() {
  if (!mapEl.value) return

  leafletMap = L.map(mapEl.value, {
    center: [20, 0],
    zoom: 1,
    scrollWheelZoom: false,
    zoomControl: true,
  })

  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap contributors',
    maxZoom: 6,
  }).addTo(leafletMap)
}

function updateGridLayers() {
  if (!leafletMap) return

  // Remove old rectangles
  gridLayers.forEach(r => r.remove())
  gridLayers = []

  const squares = stats.value?.gridSquares ?? []
  for (const sq of squares) {
    const bounds = gridSquareToBounds(sq)
    if (!bounds) continue
    const rect = L.rectangle(bounds, {
      color: '#4CAF50',
      fillColor: '#4CAF50',
      fillOpacity: 0.45,
      weight: 1,
      opacity: 0.8,
    }).addTo(leafletMap)
    rect.bindTooltip(sq, { permanent: false, direction: 'top' })
    gridLayers.push(rect)
  }
}

watch(stats, () => {
  updateGridLayers()
})

onMounted(async () => {
  await load()
  initMap()
  updateGridLayers()
})

onUnmounted(() => {
  leafletMap?.remove()
  leafletMap = null
})
</script>

<template>
  <v-container fluid class="statistics-view pa-4">
    <div class="d-flex align-center justify-space-between mb-4">
      <div class="text-h5 font-weight-bold">Statistics</div>
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
      Could not load statistics. Is the API running?
    </v-alert>

    <!-- Summary cards -->
    <v-row class="mb-4">
      <v-col cols="6" sm="3">
        <v-card variant="tonal" color="primary" class="stat-card">
          <v-card-text class="text-center pa-3">
            <div class="text-h4 font-weight-bold">
              {{ stats?.packetsToday.toLocaleString() ?? '—' }}
            </div>
            <div class="text-caption text-medium-emphasis mt-1">Packets today</div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="6" sm="3">
        <v-card variant="tonal" color="green" class="stat-card">
          <v-card-text class="text-center pa-3">
            <div class="text-h4 font-weight-bold">
              {{ stats?.uniqueStationsToday.toLocaleString() ?? '—' }}
            </div>
            <div class="text-caption text-medium-emphasis mt-1">Unique stations today</div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="6" sm="3">
        <v-card variant="tonal" color="orange" class="stat-card">
          <v-card-text class="text-center pa-3">
            <div class="text-h4 font-weight-bold">
              {{ stats?.uniqueStationsThisWeek.toLocaleString() ?? '—' }}
            </div>
            <div class="text-caption text-medium-emphasis mt-1">Unique stations this week</div>
          </v-card-text>
        </v-card>
      </v-col>
      <v-col cols="6" sm="3">
        <v-card variant="tonal" color="purple" class="stat-card">
          <v-card-text class="text-center pa-3">
            <div class="text-h4 font-weight-bold">
              {{ stats?.uniqueStationsAllTime.toLocaleString() ?? '—' }}
            </div>
            <div class="text-caption text-medium-emphasis mt-1">Unique stations all time</div>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <!-- Packets per hour chart -->
    <v-card class="mb-4" variant="outlined">
      <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1">
        Packets per hour — last 24h
      </v-card-title>
      <v-card-text class="pa-3 pt-1">
        <div class="chart-wrap">
          <Bar :data="barData" :options="barOptions" />
        </div>
      </v-card-text>
    </v-card>

    <v-row class="mb-4">
      <!-- Busiest digipeaters -->
      <v-col cols="12" md="6">
        <v-card variant="outlined" height="100%">
          <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1">
            Busiest digipeaters
          </v-card-title>
          <v-card-text class="pa-0">
            <v-table density="compact">
              <thead>
                <tr>
                  <th class="text-caption">#</th>
                  <th class="text-caption">Callsign</th>
                  <th class="text-caption text-right">Total</th>
                  <th class="text-caption text-right">24h</th>
                  <th class="text-caption text-right">Avg hops</th>
                </tr>
              </thead>
              <tbody>
                <tr v-if="digiAnalysis.length === 0">
                  <td colspan="5" class="text-center text-caption text-medium-emphasis py-3">
                    No data
                  </td>
                </tr>
                <tr v-for="(d, i) in digiAnalysis" :key="d.callsign">
                  <td class="text-caption text-medium-emphasis">{{ i + 1 }}</td>
                  <td class="text-body-2 font-weight-medium">{{ d.callsign }}</td>
                  <td class="text-body-2 text-right">{{ d.totalPacketsForwarded.toLocaleString() }}</td>
                  <td class="text-body-2 text-right">{{ d.last24h.toLocaleString() }}</td>
                  <td class="text-body-2 text-right">{{ d.averageHopsFromUs.toFixed(1) }}</td>
                </tr>
              </tbody>
            </v-table>
          </v-card-text>
        </v-card>
      </v-col>

      <!-- Busiest stations by beacon rate -->
      <v-col cols="12" md="6">
        <v-card variant="outlined" height="100%">
          <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1">
            Busiest stations (beacon rate)
          </v-card-title>
          <v-card-text class="pa-0">
            <v-table density="compact">
              <thead>
                <tr>
                  <th class="text-caption">#</th>
                  <th class="text-caption">Callsign</th>
                  <th class="text-caption text-right">Avg/hr</th>
                  <th class="text-caption text-right">Today</th>
                </tr>
              </thead>
              <tbody>
                <tr v-if="!stats || stats.busiestStations.length === 0">
                  <td colspan="4" class="text-center text-caption text-medium-emphasis py-3">
                    No data
                  </td>
                </tr>
                <tr v-for="(s, i) in stats?.busiestStations ?? []" :key="s.callsign">
                  <td class="text-caption text-medium-emphasis">{{ i + 1 }}</td>
                  <td class="text-body-2 font-weight-medium">{{ s.callsign }}</td>
                  <td class="text-body-2 text-right">{{ s.averagePerHour.toFixed(2) }}</td>
                  <td class="text-body-2 text-right">{{ s.count.toLocaleString() }}</td>
                </tr>
              </tbody>
            </v-table>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <v-row class="mb-4">
      <!-- Recently first-heard stations -->
      <v-col cols="12" md="5">
        <v-card variant="outlined">
          <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1">
            Recently first-heard stations
          </v-card-title>
          <v-card-text class="pa-0">
            <div
              v-for="s in stats?.recentlyFirstHeard ?? []"
              :key="s.callsign"
              class="recent-row"
            >
              <span class="text-body-2 font-weight-medium">{{ s.callsign }}</span>
              <div class="d-flex align-center ga-2 mt-1">
                <v-chip :color="typeColor[s.stationType]" size="x-small" label>
                  {{ typeLabel[s.stationType] }}
                </v-chip>
                <span class="text-caption text-medium-emphasis">{{ timeAgo(s.firstSeen) }}</span>
              </div>
            </div>
            <div
              v-if="!stats || stats.recentlyFirstHeard.length === 0"
              class="text-center text-caption text-medium-emphasis py-3"
            >
              No data
            </div>
          </v-card-text>
        </v-card>
      </v-col>

      <!-- Grid square count -->
      <v-col cols="12" md="7">
        <v-card variant="outlined">
          <v-card-title class="text-body-2 font-weight-medium pa-3 pb-1">
            Heard grid squares
            <v-chip v-if="stats" size="x-small" color="green" class="ml-2">
              {{ stats.gridSquares.length }}
            </v-chip>
          </v-card-title>
          <v-card-text class="pa-2">
            <div ref="mapEl" class="grid-map" />
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>
  </v-container>
</template>

<style scoped>
.statistics-view {
  height: 100%;
  overflow-y: auto;
}

.stat-card {
  min-height: 84px;
}

.chart-wrap {
  height: 160px;
}

.grid-map {
  height: 280px;
  border-radius: 4px;
  overflow: hidden;
}

.recent-row {
  padding: 6px 12px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
}

.recent-row:last-child {
  border-bottom: none;
}
</style>

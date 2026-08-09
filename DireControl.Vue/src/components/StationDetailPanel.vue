<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { Line, Bar } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  BarElement,
  Tooltip as ChartTooltip,
  Filler,
  Legend,
} from 'chart.js'
import {
  getStation,
  getStationPackets,
  getStationWeather,
  toggleWatch,
  lookupCallsign,
  getStationStats,
  getStationSignal,
} from '@/api/stationsApi'
import {
  StationType,
  HeardVia,
  type StationDto,
  type CallsignLookupDto,
  type StationStatisticDto,
} from '@/types/station'
import {
  PacketType,
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  type PacketDto,
  type WeatherReadingDto,
  type SignalPointDto,
} from '@/types/packet'
import { timeAgo, formatUtc, compassDir } from '@/utils/time'
import { getSymbolStyle, parseAprsSymbol } from '@/utils/aprsIcon'
import { useStationSelectionStore } from '@/stores/stationSelection'
import PacketInspectionDialog from '@/components/PacketInspectionDialog.vue'
import { useTick } from '@/composables/useTick'

ChartJS.register(
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  BarElement,
  ChartTooltip,
  Filler,
  Legend,
)

const props = defineProps<{
  callsign: string | null
  refreshKey: number
  /** Show an "open full page" link — set by the map, off on /stations/:callsign itself. */
  showPageLink?: boolean
  /**
   * Page mode (/stations/:callsign): the page renders its own header, so the
   * panel's is hidden and the icon rail becomes a horizontal tab row.
   */
  pageVariant?: boolean
}>()

const emit = defineEmits<{
  close: []
  highlightPosition: [lat: number, lon: number]
}>()

const selectionStore = useStationSelectionStore()

const { now } = useTick(1000)

const inspectedPacketId = ref<number | null>(null)

type TabValue = 'info' | 'packets' | 'weather' | 'stats' | 'signal'

// Page mode binds this from the route page's own tab row (v-model:tab).
const tab = defineModel<TabValue>('tab', { default: 'info' })
const packetsNewData = ref(false)

interface TabDef {
  value: TabValue
  label: string
  /** Short label used by the compact in-map tab row (mock: Info · Pkts · Wx · Stats · Sig). */
  short: string
  badge: boolean
}

const visibleTabs = computed<TabDef[]>(() => [
  { value: 'info', label: 'Info', short: 'Info', badge: false },
  { value: 'packets', label: 'Packets', short: 'Pkts', badge: packetsNewData.value },
  ...(station.value?.isWeatherStation
    ? [{ value: 'weather' as const, label: 'Weather', short: 'Wx', badge: false }]
    : []),
  { value: 'stats', label: 'Stats', short: 'Stats', badge: false },
  { value: 'signal', label: 'Signal', short: 'Sig', badge: false },
])

function onTabKeydown(e: KeyboardEvent) {
  const tabs = visibleTabs.value
  const idx = tabs.findIndex((t) => t.value === tab.value)
  if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
    const next = tabs[(idx + 1) % tabs.length]
    if (next) tab.value = next.value
    e.preventDefault()
  } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
    const prev = tabs[(idx - 1 + tabs.length) % tabs.length]
    if (prev) tab.value = prev.value
    e.preventDefault()
  }
}
const station = ref<StationDto | null>(null)
const loading = ref(false)
const watchLoading = ref(false)

const packets = ref<PacketDto[]>([])
const packetPage = ref(1)
const packetTotal = ref(0)
const packetPageSize = 20
const packetsLoading = ref(false)

const weatherReadings = ref<WeatherReadingDto[]>([])
const weatherLoading = ref(false)
const weatherRange = ref<'24h' | '7d'>('24h')

// Inline crosshair plugin — draws a vertical rule at the hovered index
const crosshairPlugin = {
  id: 'weatherCrosshair',
  afterDraw(chart: ChartJS) {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const active = (chart.tooltip as any)?._active as { element: { x: number } }[] | undefined
    const firstActive = active?.[0]
    if (!firstActive) return
    const x = firstActive.element.x
    const { top, bottom } = chart.chartArea
    const ctx = chart.ctx
    ctx.save()
    ctx.beginPath()
    ctx.moveTo(x, top)
    ctx.lineTo(x, bottom)
    ctx.lineWidth = 1
    ctx.strokeStyle = 'rgba(160, 160, 160, 0.55)'
    ctx.stroke()
    ctx.restore()
  },
}

// Lookup state
const lookupLoading = ref(false)
const lookupFailed = ref(false)
const lookupData = ref<CallsignLookupDto | null>(null)

// Stats state
const stats = ref<StationStatisticDto | null>(null)
const statsLoading = ref(false)

// Signal state
const signalPoints = ref<SignalPointDto[]>([])
const signalLoading = ref(false)

const totalPages = computed(() => Math.max(1, Math.ceil(packetTotal.value / packetPageSize)))

// Current conditions — latest reading (readings are newest-first)
const currentWeather = computed(() => weatherReadings.value[0] ?? null)

const tempF = computed(() => currentWeather.value?.temperature ?? null)
const tempC = computed(() =>
  tempF.value != null ? Math.round((((tempF.value - 32) * 5) / 9) * 10) / 10 : null,
)

// ---- Combined weather chart ----

const bucketMs = computed(() => (weatherRange.value === '24h' ? 5 * 60 * 1000 : 60 * 60 * 1000))
const totalBuckets = computed(() => (weatherRange.value === '24h' ? 288 : 168))

function formatBucketLabel(date: Date): string {
  if (weatherRange.value === '24h') {
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
  }
  // 7d: show weekday name at midnight, blank otherwise
  return date.getHours() === 0 ? date.toLocaleDateString([], { weekday: 'short' }) : ''
}

const weatherChartData = computed(() => {
  const now = Date.now()
  const bMs = bucketMs.value
  const total = totalBuckets.value
  const fromMs = now - total * bMs

  const readingMap = new Map<number, WeatherReadingDto>()
  for (const r of weatherReadings.value) {
    const idx = Math.round((new Date(r.receivedAt).getTime() - fromMs) / bMs)
    if (idx >= 0 && idx < total) readingMap.set(idx, r)
  }

  const labels: string[] = []
  const tempData: (number | null)[] = []
  const windData: (number | null)[] = []

  for (let i = 0; i < total; i++) {
    const d = new Date(fromMs + i * bMs)
    labels.push(formatBucketLabel(d))
    const r = readingMap.get(i)
    tempData.push(r?.temperature ?? null)
    windData.push(r?.windSpeed ?? null)
  }

  return {
    labels,
    datasets: [
      {
        label: 'Temp (°F)',
        data: tempData,
        borderColor: '#FF7043',
        backgroundColor: 'transparent',
        borderWidth: 2,
        tension: 0.2,
        fill: false as const,
        spanGaps: false,
        yAxisID: 'yTemp',
        pointRadius: 0,
        pointHoverRadius: 4,
      },
      {
        label: 'Wind (mph)',
        data: windData,
        borderColor: '#42A5F5',
        backgroundColor: 'transparent',
        borderWidth: 2,
        tension: 0.2,
        fill: false as const,
        spanGaps: false,
        yAxisID: 'yWind',
        pointRadius: 0,
        pointHoverRadius: 4,
      },
    ],
  }
})

const weatherChartOptions = computed(() => {
  const is24h = weatherRange.value === '24h'
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false as const,
    interaction: { mode: 'index' as const, intersect: false },
    plugins: {
      legend: {
        display: true,
        position: 'top' as const,
        labels: { font: { size: 10 }, boxWidth: 12 },
      },
      tooltip: {
        enabled: true,
        callbacks: {
          title(items: { dataIndex: number }[]) {
            const idx = items[0]?.dataIndex
            if (idx == null) return ''
            const bMs = is24h ? 5 * 60 * 1000 : 60 * 60 * 1000
            const total = is24h ? 288 : 168
            const d = new Date(Date.now() - total * bMs + idx * bMs)
            return is24h
              ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
              : d.toLocaleDateString([], {
                  weekday: 'short',
                  month: 'short',
                  day: 'numeric',
                  hour: '2-digit',
                  minute: '2-digit',
                })
          },
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          label(item: any) {
            if (item.raw == null) return ''
            const unit = item.datasetIndex === 0 ? '°F' : ' mph'
            return `${item.dataset.label}: ${(item.raw as number).toFixed(1)}${unit}`
          },
        },
      },
    },
    scales: {
      x: {
        display: true,
        ticks: {
          font: { size: 9 },
          maxRotation: 0,
          ...(is24h
            ? { autoSkip: true, maxTicksLimit: 12 }
            : {
                autoSkip: false,
                // eslint-disable-next-line @typescript-eslint/no-explicit-any
                callback(_v: unknown, index: number, ticks: any[]) {
                  // Only render the label when it has content (weekday names)
                  const label = ticks[index]?.label as string | undefined
                  return label || null
                },
              }),
        },
        grid: { display: true, color: 'rgba(128,128,128,0.15)' },
      },
      yTemp: {
        display: true,
        position: 'left' as const,
        ticks: { font: { size: 9 } },
        grid: { display: true, color: 'rgba(128,128,128,0.15)' },
        title: { display: true, text: '°F', font: { size: 9 } },
      },
      yWind: {
        display: true,
        position: 'right' as const,
        beginAtZero: true,
        ticks: { font: { size: 9 } },
        grid: { display: false },
        title: { display: true, text: 'mph', font: { size: 9 } },
      },
    },
  }
})

const hasWeatherChartData = computed(() =>
  weatherReadings.value.some((r) => r.temperature != null || r.windSpeed != null),
)

// ---- Packets-per-hour bar chart ----

const hourBarOptions = {
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
      ticks: { font: { size: 9 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 },
    },
    y: {
      beginAtZero: true,
      ticks: { stepSize: 1, font: { size: 9 } },
    },
  },
}

const hourBarData = computed(() => {
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
        data: counts,
        backgroundColor: 'rgba(66, 165, 245, 0.55)',
        borderColor: '#42A5F5',
        borderWidth: 1,
      },
    ],
  }
})

// ---- Signal analytics ----

const signalRange = ref<'24h' | '7d'>('24h')

const hasAudioData = computed(() => signalPoints.value.some((p) => p.audioLevel != null))
const hasOffsetData = computed(() => signalPoints.value.some((p) => p.frequencyOffsetHz != null))

/** Aggregates for the summary tiles. */
const signalSummary = computed(() => {
  const points = signalPoints.value
  const audio = points.filter((p) => p.audioLevel != null).map((p) => p.audioLevel!)
  const direct = points.filter((p) => p.hopCount === 0).length

  const digiCounts = new Map<string, number>()
  for (const p of points) {
    if (p.firstDigi) digiCounts.set(p.firstDigi, (digiCounts.get(p.firstDigi) ?? 0) + 1)
  }
  const viaDigis = [...digiCounts.entries()].sort((a, b) => b[1] - a[1])

  const profiles = new Map<string, number>()
  for (const p of points) {
    if (p.demodProfile) profiles.set(p.demodProfile, (profiles.get(p.demodProfile) ?? 0) + 1)
  }

  return {
    count: points.length,
    avgAudio: audio.length > 0 ? audio.reduce((a, b) => a + b, 0) / audio.length : null,
    peakAudio: audio.length > 0 ? Math.max(...audio) : null,
    directPct: points.length > 0 ? (direct / points.length) * 100 : 0,
    directCount: direct,
    viaDigis,
    profiles: [...profiles.entries()].sort((a, b) => b[1] - a[1]),
  }
})

/** Direct + per-digipeater share of how this station reaches us. */
const heardViaBars = computed(() => {
  const total = signalPoints.value.length
  if (total === 0) return []
  const bars: { label: string; count: number; pct: number; direct: boolean }[] = []
  if (signalSummary.value.directCount > 0) {
    bars.push({
      label: 'Direct',
      count: signalSummary.value.directCount,
      pct: (signalSummary.value.directCount / total) * 100,
      direct: true,
    })
  }
  for (const [digi, count] of signalSummary.value.viaDigis.slice(0, 5)) {
    bars.push({ label: digi, count, pct: (count / total) * 100, direct: false })
  }
  return bars
})

function signalTimeLabel(iso: string): string {
  const d = new Date(iso)
  if (signalRange.value === '7d') {
    return d.toLocaleDateString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' })
  }
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

const audioChartData = computed(() => ({
  labels: signalPoints.value.map((p) => signalTimeLabel(p.receivedAt)),
  datasets: [
    {
      label: 'Audio level',
      data: signalPoints.value.map((p) => p.audioLevel),
      borderColor: '#66BB6A',
      backgroundColor: 'rgba(102, 187, 106, 0.08)',
      borderWidth: 2,
      tension: 0.2,
      fill: true,
      spanGaps: true,
      pointRadius: 2,
    },
  ],
}))

const audioChartOptions = computed(() => ({
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
      ticks: { font: { size: 9 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 },
    },
    y: {
      beginAtZero: true,
      suggestedMax: Math.max(0.5, signalSummary.value.peakAudio ?? 0),
      ticks: { font: { size: 9 } },
    },
  },
  elements: { point: { radius: 2 } },
}))

const offsetChartData = computed(() => ({
  labels: signalPoints.value.map((p) => signalTimeLabel(p.receivedAt)),
  datasets: [
    {
      label: 'Frequency offset (Hz)',
      data: signalPoints.value.map((p) => p.frequencyOffsetHz),
      borderColor: '#42A5F5',
      backgroundColor: 'transparent',
      borderWidth: 2,
      tension: 0.2,
      fill: false,
      spanGaps: true,
      pointRadius: 2,
    },
  ],
}))

const signalChartOptions = {
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
      ticks: { font: { size: 9 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 },
    },
    y: {
      beginAtZero: true,
      max: 100,
      ticks: { font: { size: 9 } },
    },
  },
  elements: {
    point: { radius: 2 },
  },
}

const signalChartData = computed(() => ({
  labels: signalPoints.value.map((p) => signalTimeLabel(p.receivedAt)),
  datasets: [
    {
      label: 'Decode Quality',
      data: signalPoints.value.map((p) => p.decodeQuality),
      borderColor: '#66BB6A',
      backgroundColor: 'rgba(102, 187, 106, 0.08)',
      borderWidth: 2,
      tension: 0.2,
      fill: true,
    },
  ],
}))

const hasDecodeQualityData = computed(() => signalPoints.value.some((p) => p.decodeQuality != null))

// ---- Station type label / color ----
const stationTypeLabel = computed(() => {
  if (!station.value) return ''
  const labels: Record<StationType, string> = {
    [StationType.Fixed]: 'Fixed',
    [StationType.Mobile]: 'Mobile',
    [StationType.Weather]: 'Weather',
    [StationType.Digipeater]: 'Digipeater',
    [StationType.IGate]: 'IGate',
    [StationType.Unknown]: 'Unknown',
    [StationType.Gateway]: 'Gateway',
  }
  return labels[station.value.stationType] ?? 'Unknown'
})

const stationTypeColor = computed(() => {
  if (!station.value) return 'grey'
  const colors: Record<StationType, string> = {
    [StationType.Fixed]: 'blue',
    [StationType.Mobile]: 'green',
    [StationType.Weather]: 'teal',
    [StationType.Digipeater]: 'orange',
    [StationType.IGate]: 'purple',
    [StationType.Unknown]: 'grey',
    [StationType.Gateway]: 'deep-purple',
  }
  return colors[station.value.stationType] ?? 'grey'
})

const heardViaLabel = computed((): string | null => {
  if (!station.value) return null
  const labels: Partial<Record<HeardVia, string>> = {
    [HeardVia.Direct]: 'Direct',
    [HeardVia.Digi]: 'Via Digipeater',
    [HeardVia.DirectAndDigi]: 'Direct & Digi',
    [HeardVia.IgateRf]: 'iGate',
    [HeardVia.IgateRfDigi]: 'iGate+Digi',
    [HeardVia.Internet]: 'Internet',
  }
  return labels[station.value.heardVia] ?? null
})

const heardViaColor = computed((): string => {
  if (!station.value) return 'grey'
  const colors: Partial<Record<HeardVia, string>> = {
    [HeardVia.Direct]: 'green',
    [HeardVia.Digi]: 'amber-darken-2',
    [HeardVia.DirectAndDigi]: 'teal',
    [HeardVia.IgateRf]: 'blue',
    [HeardVia.IgateRfDigi]: 'purple',
    [HeardVia.Internet]: 'grey',
  }
  return colors[station.value.heardVia] ?? 'grey'
})

const symbolStyle = computed(() => {
  if (!station.value?.symbol) return {}
  const { table, code } = parseAprsSymbol(station.value.symbol)
  return getSymbolStyle(table, code)
})

// ---- Data fetching ----

async function fetchStation() {
  if (!props.callsign) return
  loading.value = true
  try {
    station.value = await getStation(props.callsign)
    if (station.value?.qrzLookupData) {
      lookupData.value = station.value.qrzLookupData
    }
  } catch {
    station.value = null
  } finally {
    loading.value = false
  }
  void fetchLatestRaw()
}

// Latest raw packet line shown at the bottom of the Info tab (mock's mono box).
const latestRaw = ref<string | null>(null)

async function fetchLatestRaw() {
  if (!props.callsign) {
    latestRaw.value = null
    return
  }
  try {
    const { items } = await getStationPackets(props.callsign, 1, 1)
    latestRaw.value = items[0]?.rawPacket ?? null
  } catch {
    latestRaw.value = null
  }
}

async function fetchPackets() {
  if (!props.callsign) return
  packetsLoading.value = true
  try {
    const result = await getStationPackets(props.callsign, packetPage.value, packetPageSize)
    packets.value = result.items
    packetTotal.value = result.totalCount
  } catch {
    packets.value = []
    packetTotal.value = 0
  } finally {
    packetsLoading.value = false
  }
}

async function fetchWeather() {
  if (!props.callsign) return
  weatherLoading.value = true
  try {
    const now = new Date()
    const fromDate =
      weatherRange.value === '24h'
        ? new Date(now.getTime() - 24 * 60 * 60 * 1000)
        : new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000)
    weatherReadings.value = await getStationWeather(
      props.callsign,
      fromDate.toISOString(),
      now.toISOString(),
    )
  } catch {
    weatherReadings.value = []
  } finally {
    weatherLoading.value = false
  }
}

async function fetchStats() {
  if (!props.callsign) return
  statsLoading.value = true
  try {
    stats.value = await getStationStats(props.callsign)
  } catch {
    stats.value = null
  } finally {
    statsLoading.value = false
  }
}

async function fetchSignal() {
  if (!props.callsign) return
  signalLoading.value = true
  try {
    signalPoints.value = await getStationSignal(
      props.callsign,
      signalRange.value === '7d' ? 168 : 24,
    )
  } catch {
    signalPoints.value = []
  } finally {
    signalLoading.value = false
  }
}
watch(signalRange, () => void fetchSignal())

async function performLookup() {
  if (!props.callsign) return
  lookupLoading.value = true
  lookupFailed.value = false
  try {
    const result = await lookupCallsign(props.callsign)
    if (result) {
      lookupData.value = result
      if (station.value) {
        station.value = { ...station.value, qrzLookupData: result }
      }
    } else {
      lookupFailed.value = true
    }
  } finally {
    lookupLoading.value = false
  }
}

function onPacketRowClick(p: PacketDto) {
  if (p.latitude != null && p.longitude != null) {
    emit('highlightPosition', p.latitude, p.longitude)
  }
  inspectedPacketId.value = p.id
}

function onDialogSelectStation(callsign: string) {
  inspectedPacketId.value = null
  selectionStore.selectStation(callsign)
}

async function toggleWatchStatus() {
  if (!props.callsign || !station.value) return
  watchLoading.value = true
  try {
    await toggleWatch(props.callsign)
    station.value = { ...station.value, isOnWatchList: !station.value.isOnWatchList }
  } finally {
    watchLoading.value = false
  }
}

function osmLink(lat: number, lon: number): string {
  return `https://www.openstreetmap.org/?mlat=${lat.toFixed(6)}&mlon=${lon.toFixed(6)}&zoom=14`
}

function packetTypeName(parsedType: number): string {
  return PACKET_TYPE_LABELS[parsedType as PacketType] ?? 'Unknown'
}

function packetTypeColor(parsedType: number): string {
  return PACKET_TYPE_COLORS[parsedType as PacketType] ?? 'grey'
}

function formatGap(minutes: number): string {
  if (minutes <= 0) return '—'
  if (minutes < 60) return `${minutes}m`
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return m > 0 ? `${h}h ${m}m` : `${h}h`
}

watch(
  () => props.callsign,
  async (val) => {
    if (!val) {
      station.value = null
      packets.value = []
      packetTotal.value = 0
      packetPage.value = 1
      weatherReadings.value = []
      weatherRange.value = '24h'
      lookupData.value = null
      lookupFailed.value = false
      stats.value = null
      signalPoints.value = []
      packetsNewData.value = false
      tab.value = 'info'
      return
    }
    packetPage.value = 1
    weatherRange.value = '24h'
    lookupData.value = null
    lookupFailed.value = false
    stats.value = null
    signalPoints.value = []
    packetsNewData.value = false
    tab.value = 'info'
    await Promise.all([fetchStation(), fetchPackets()])
    if (station.value?.isWeatherStation) {
      await fetchWeather()
    }
  },
  { immediate: true },
)

watch(weatherRange, () => {
  if (props.callsign && station.value?.isWeatherStation) {
    fetchWeather()
  }
})

watch(
  () => props.refreshKey,
  async (newKey, oldKey) => {
    if (!props.callsign || newKey === oldKey) return
    await fetchStation()
    if (packetPage.value === 1) {
      await fetchPackets()
    }
    if (station.value?.isWeatherStation) {
      await fetchWeather()
    }
    if (tab.value !== 'packets') {
      packetsNewData.value = true
    }
  },
)

watch(packetPage, () => {
  if (props.callsign) fetchPackets()
})

watch(tab, (newTab) => {
  if (newTab === 'packets') {
    packetsNewData.value = false
  }
  if (newTab === 'stats' && props.callsign && !stats.value && !statsLoading.value) {
    fetchStats()
  }
  if (
    newTab === 'signal' &&
    props.callsign &&
    signalPoints.value.length === 0 &&
    !signalLoading.value
  ) {
    fetchSignal()
  }
})
</script>

<template>
  <div
    v-if="callsign"
    class="detail-panel-content"
    :class="{ 'detail-panel-content--page': props.pageVariant }"
  >
    <!-- Header (hidden in page mode — the page provides its own).
         Mock: one row — symbol · callsign · star ······ close -->
    <div v-if="!props.pageVariant" class="panel-header">
      <div :style="symbolStyle" class="symbol-icon flex-shrink-0" />
      <span class="panel-callsign">{{ callsign }}</span>
      <v-btn
        :icon="station?.isOnWatchList ? 'mdi-star' : 'mdi-star-outline'"
        :color="station?.isOnWatchList ? 'amber' : 'default'"
        variant="text"
        size="x-small"
        :loading="watchLoading"
        :disabled="!station"
        title="Toggle watch list"
        @click="toggleWatchStatus"
      />
      <v-spacer />
      <v-btn icon="mdi-close" variant="text" size="x-small" @click="emit('close')" />
    </div>

    <!-- Horizontal text tabs under the header (mock: Info · Pkts · Wx · Stats · Sig) -->
    <nav v-if="!props.pageVariant" class="tab-row" role="tablist" @keydown="onTabKeydown">
      <button
        v-for="t in visibleTabs"
        :key="t.value"
        role="tab"
        :aria-selected="tab === t.value"
        :class="['tab-btn', { 'tab-btn--active': tab === t.value }]"
        :title="t.label"
        @click="tab = t.value"
      >
        {{ t.short }}
        <span v-if="t.badge" class="tab-new-dot" />
      </button>
    </nav>
    <v-divider v-if="!props.pageVariant" />

    <div class="panel-main">
      <div class="panel-body" role="tabpanel">
        <v-progress-linear v-if="loading" indeterminate color="primary" />

        <!-- Info tab -->
        <template v-if="tab === 'info' && station">
          <div class="info-section">
            <div class="info-label">Type</div>
            <div class="info-value">
              <v-chip :color="stationTypeColor" size="x-small" label>{{ stationTypeLabel }}</v-chip>
              <v-chip
                v-if="station.isWeatherStation"
                color="teal"
                size="x-small"
                label
                class="ml-1"
              >
                WX
              </v-chip>
            </div>

            <template v-if="station.status">
              <div class="info-label">Status</div>
              <div class="info-value">{{ station.status }}</div>
            </template>

            <div class="info-label">First Seen</div>
            <div class="info-value" :title="formatUtc(station.firstSeen)">
              {{ timeAgo(station.firstSeen, now) }}
            </div>

            <div class="info-label">Last Seen</div>
            <div class="info-value" :title="formatUtc(station.lastSeen)">
              {{ timeAgo(station.lastSeen, now) }}
            </div>

            <template v-if="heardViaLabel">
              <div class="info-label">Heard Via</div>
              <div class="info-value">
                <v-chip :color="heardViaColor" size="x-small" label>{{ heardViaLabel }}</v-chip>
              </div>
            </template>

            <template v-if="station.lastLat != null && station.lastLon != null">
              <div class="info-label">Coordinates</div>
              <div class="info-value">
                {{ station.lastLat.toFixed(5) }}, {{ station.lastLon.toFixed(5) }}
                <a
                  :href="osmLink(station.lastLat, station.lastLon)"
                  target="_blank"
                  rel="noopener"
                  class="osm-link ml-1"
                >
                  <v-icon size="12">mdi-open-in-new</v-icon> OSM
                </a>
              </div>
            </template>

            <template v-if="station.lastSpeed != null">
              <div class="info-label">Speed</div>
              <div class="info-value">{{ station.lastSpeed.toFixed(1) }} kn</div>
            </template>

            <template v-if="station.lastHeading != null">
              <div class="info-label">Heading</div>
              <div class="info-value">
                {{ station.lastHeading }}° {{ compassDir(station.lastHeading) }}
              </div>
            </template>

            <template v-if="station.lastAltitude != null">
              <div class="info-label">Altitude</div>
              <div class="info-value">
                {{ Math.round(station.lastAltitude).toLocaleString() }} ft
              </div>
            </template>

            <template v-if="station.gridSquare">
              <div class="info-label">Grid Square</div>
              <div class="info-value">{{ station.gridSquare }}</div>
            </template>

            <template v-if="station.lastMode">
              <div class="info-label">Mode</div>
              <div class="info-value">{{ station.lastMode }}</div>
            </template>

            <template v-if="station.lastFrequencyMhz">
              <div class="info-label">Frequency</div>
              <div class="info-value">{{ station.lastFrequencyMhz }} MHz</div>
            </template>
          </div>

          <!-- Latest raw packet line (mock's mono box) -->
          <div v-if="latestRaw" class="raw-packet-box mx-3 mt-2" :title="latestRaw">
            {{ latestRaw }}
          </div>

          <!-- Operator lookup section -->
          <div
            class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
          >
            OPERATOR LOOKUP
          </div>

          <template v-if="lookupData">
            <div class="info-section">
              <template v-if="lookupData.name">
                <div class="info-label">Name</div>
                <div class="info-value">{{ lookupData.name }}</div>
              </template>
              <template v-if="lookupData.city || lookupData.state">
                <div class="info-label">Location</div>
                <div class="info-value">
                  {{ [lookupData.city, lookupData.state].filter(Boolean).join(', ') }}
                </div>
              </template>
              <template v-if="lookupData.licenseClass">
                <div class="info-label">License</div>
                <div class="info-value">{{ lookupData.licenseClass }}</div>
              </template>
              <template v-if="lookupData.gridSquare">
                <div class="info-label">Grid</div>
                <div class="info-value">{{ lookupData.gridSquare }}</div>
              </template>
            </div>
          </template>

          <template v-else-if="lookupFailed">
            <div class="px-3 pb-3 text-caption text-medium-emphasis">No record found</div>
          </template>

          <template v-else>
            <div class="px-3 pb-3">
              <v-btn
                size="x-small"
                variant="tonal"
                color="primary"
                :loading="lookupLoading"
                prepend-icon="mdi-magnify"
                @click="performLookup"
              >
                Lookup callsign
              </v-btn>
            </div>
          </template>
        </template>
        <template v-else-if="tab === 'info' && !loading">
          <div class="text-center text-medium-emphasis py-4">No data</div>
        </template>

        <!-- Packets tab -->
        <template v-if="tab === 'packets'">
          <v-progress-linear v-if="packetsLoading" indeterminate color="primary" />
          <div
            v-if="packets.length === 0 && !packetsLoading"
            class="text-center text-medium-emphasis py-4"
          >
            No packets
          </div>
          <div v-for="p in packets" :key="p.id" class="packet-row" @click="onPacketRowClick(p)">
            <div class="d-flex align-center ga-2">
              <v-chip :color="packetTypeColor(p.parsedType)" size="x-small" label>
                {{ packetTypeName(p.parsedType) }}
              </v-chip>
              <span
                class="heard-via-dot"
                :class="p.isDirectHeard ? 'heard-via-dot--direct' : 'heard-via-dot--digi'"
                :title="p.isDirectHeard ? 'Direct' : 'Via Digi'"
              />
              <span
                class="text-caption text-medium-emphasis flex-shrink-0"
                :title="formatUtc(p.receivedAt)"
              >
                {{ timeAgo(p.receivedAt, now) }}
              </span>
            </div>
            <div v-if="p.comment" class="text-body-2 text-truncate mt-1">{{ p.comment }}</div>
            <div v-if="p.latitude != null" class="text-caption text-medium-emphasis">
              {{ p.latitude.toFixed(4) }}, {{ p.longitude!.toFixed(4) }}
              <v-icon v-if="p.latitude != null" size="10" color="primary"
                >mdi-crosshairs-gps</v-icon
              >
            </div>
            <div
              v-if="
                p.signalData &&
                (p.signalData.decodeQuality != null || p.signalData.frequencyOffsetHz != null)
              "
              class="text-caption text-medium-emphasis mt-1"
            >
              <span v-if="p.signalData.decodeQuality != null"
                >Q: {{ p.signalData.decodeQuality }}</span
              >
              <span
                v-if="p.signalData.decodeQuality != null && p.signalData.frequencyOffsetHz != null"
                >,
              </span>
              <span v-if="p.signalData.frequencyOffsetHz != null">
                Δf: {{ p.signalData.frequencyOffsetHz > 0 ? '+' : ''
                }}{{ p.signalData.frequencyOffsetHz.toFixed(0) }}Hz
              </span>
            </div>
          </div>
          <!-- Pagination -->
          <div v-if="totalPages > 1" class="d-flex align-center justify-center ga-1 pa-2">
            <v-btn
              icon="mdi-chevron-left"
              size="x-small"
              variant="text"
              :disabled="packetPage <= 1"
              @click="packetPage--"
            />
            <span class="text-caption">{{ packetPage }} / {{ totalPages }}</span>
            <v-btn
              icon="mdi-chevron-right"
              size="x-small"
              variant="text"
              :disabled="packetPage >= totalPages"
              @click="packetPage++"
            />
          </div>
        </template>

        <!-- Stats tab -->
        <template v-if="tab === 'stats'">
          <v-progress-linear v-if="statsLoading" indeterminate color="primary" />
          <template v-if="stats && !statsLoading">
            <div class="info-section">
              <div class="info-label">Packets today</div>
              <div class="info-value">{{ stats.packetsToday.toLocaleString() }}</div>

              <div class="info-label">All time</div>
              <div class="info-value">{{ stats.packetsAllTime.toLocaleString() }}</div>

              <div class="info-label">Avg / hour</div>
              <div class="info-value">{{ stats.averagePacketsPerHour.toFixed(2) }}</div>

              <div class="info-label">Longest gap</div>
              <div class="info-value">{{ formatGap(stats.longestGapMinutes) }}</div>
            </div>

            <div
              class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
            >
              PACKETS PER HOUR (LAST 24H)
            </div>
            <div class="bar-wrap px-3 pb-3">
              <Bar :data="hourBarData" :options="hourBarOptions" />
            </div>
          </template>
          <div v-else-if="!statsLoading" class="text-center text-medium-emphasis py-4">
            No statistics available
          </div>
        </template>

        <!-- Signal tab -->
        <template v-if="tab === 'signal'">
          <v-progress-linear v-if="signalLoading" indeterminate color="green" />

          <!-- Range toggle -->
          <div class="d-flex align-center ga-2 px-3 pt-2">
            <v-btn-toggle v-model="signalRange" density="compact" mandatory variant="outlined">
              <v-btn value="24h" size="x-small">24h</v-btn>
              <v-btn value="7d" size="x-small">7d</v-btn>
            </v-btn-toggle>
            <span class="text-caption text-medium-emphasis">
              {{ signalPoints.length }} RF packet{{ signalPoints.length === 1 ? '' : 's' }}
            </span>
          </div>

          <template v-if="!signalLoading && signalPoints.length === 0">
            <div class="px-3 py-4 text-caption text-medium-emphasis">
              No RF packets from this station in the selected window — signal data comes from
              packets the modem decodes off the air.
            </div>
          </template>

          <template v-if="!signalLoading && signalPoints.length > 0">
            <!-- Summary tiles -->
            <div class="signal-tiles px-3 pt-3">
              <div class="signal-tile">
                <div class="signal-tile-value">
                  {{ signalSummary.avgAudio != null ? signalSummary.avgAudio.toFixed(2) : '—' }}
                </div>
                <div class="signal-tile-label">avg audio level</div>
              </div>
              <div class="signal-tile">
                <div class="signal-tile-value">
                  {{ signalSummary.peakAudio != null ? signalSummary.peakAudio.toFixed(2) : '—' }}
                </div>
                <div class="signal-tile-label">peak audio</div>
              </div>
              <div class="signal-tile">
                <div class="signal-tile-value">{{ signalSummary.directPct.toFixed(0) }}%</div>
                <div class="signal-tile-label">heard direct</div>
              </div>
              <div class="signal-tile">
                <div class="signal-tile-value">
                  {{ signalSummary.viaDigis[0]?.[0] ?? '—' }}
                </div>
                <div class="signal-tile-label">top digipeater</div>
              </div>
            </div>

            <!-- Audio level over time -->
            <template v-if="hasAudioData">
              <div
                class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
              >
                AUDIO LEVEL OVER TIME
              </div>
              <div class="signal-chart-wrap px-3 pb-1">
                <Line :data="audioChartData" :options="audioChartOptions" />
              </div>
            </template>

            <!-- How the packets reach us -->
            <div
              class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
            >
              HEARD VIA
            </div>
            <div class="px-3 pb-2">
              <div v-for="bar in heardViaBars" :key="bar.label" class="heard-bar-row">
                <span class="heard-bar-label" :class="{ 'text-success': bar.direct }">
                  {{ bar.label }}
                </span>
                <div class="heard-bar-track">
                  <div
                    class="heard-bar-fill"
                    :class="bar.direct ? 'heard-bar-fill--direct' : ''"
                    :style="{ width: `${Math.max(2, bar.pct)}%` }"
                  />
                </div>
                <span class="heard-bar-count">{{ bar.count }} · {{ bar.pct.toFixed(0) }}%</span>
              </div>
            </div>

            <!-- Demod profiles -->
            <div v-if="signalSummary.profiles.length > 0" class="px-3 pb-2">
              <span class="text-caption text-medium-emphasis mr-1">Demod profile:</span>
              <v-chip
                v-for="[profile, count] in signalSummary.profiles"
                :key="profile"
                size="x-small"
                variant="tonal"
                class="mr-1"
              >
                {{ profile }} · {{ count }}
              </v-chip>
            </div>

            <!-- Decode quality / frequency offset — populated by TNCs that report them -->
            <template v-if="hasDecodeQualityData">
              <div
                class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
              >
                DECODE QUALITY
              </div>
              <div class="signal-chart-wrap px-3 pb-3">
                <Line :data="signalChartData" :options="signalChartOptions" />
              </div>
            </template>
            <template v-if="hasOffsetData">
              <div
                class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
              >
                FREQUENCY OFFSET
              </div>
              <div class="signal-chart-wrap px-3 pb-3">
                <Line :data="offsetChartData" :options="signalChartOptions" />
              </div>
            </template>
          </template>
        </template>

        <!-- Weather tab -->
        <template v-if="tab === 'weather'">
          <v-progress-linear v-if="weatherLoading" indeterminate color="teal" />

          <div
            v-if="!weatherLoading && weatherReadings.length === 0"
            class="text-center text-medium-emphasis py-4"
          >
            No weather data
          </div>

          <template v-if="currentWeather">
            <!-- Current conditions -->
            <div
              class="wx-section-label px-3 pt-2 pb-1 text-caption text-medium-emphasis font-weight-medium"
            >
              CURRENT CONDITIONS
            </div>

            <div class="info-section">
              <template v-if="tempF != null">
                <div class="info-label">Temperature</div>
                <div class="info-value">
                  {{ tempF.toFixed(1) }}°F
                  <span v-if="tempC != null" class="text-medium-emphasis ml-1"
                    >({{ tempC }}°C)</span
                  >
                </div>
              </template>

              <template v-if="currentWeather.humidity != null">
                <div class="info-label">Humidity</div>
                <div class="info-value">{{ currentWeather.humidity }}%</div>
              </template>

              <template
                v-if="currentWeather.windSpeed != null || currentWeather.windDirection != null"
              >
                <div class="info-label">Wind</div>
                <div class="info-value d-flex align-center ga-1">
                  <template v-if="currentWeather.windDirection != null">
                    <v-icon
                      size="16"
                      :style="{
                        transform: `rotate(${currentWeather.windDirection}deg)`,
                        display: 'inline-block',
                      }"
                    >
                      mdi-arrow-up
                    </v-icon>
                    <span>{{ compassDir(currentWeather.windDirection) }}</span>
                  </template>
                  <span v-if="currentWeather.windSpeed != null">
                    {{ currentWeather.windSpeed.toFixed(1) }} mph
                  </span>
                </div>
              </template>

              <template v-if="currentWeather.windGust != null">
                <div class="info-label">Gust</div>
                <div class="info-value">{{ currentWeather.windGust.toFixed(1) }} mph</div>
              </template>

              <template v-if="currentWeather.pressure != null">
                <div class="info-label">Pressure</div>
                <div class="info-value">{{ currentWeather.pressure.toFixed(1) }} mb</div>
              </template>

              <template v-if="currentWeather.rainLastHour != null">
                <div class="info-label">Rain 1h</div>
                <div class="info-value">{{ currentWeather.rainLastHour.toFixed(2) }}"</div>
              </template>

              <template v-if="currentWeather.rainLast24h != null">
                <div class="info-label">Rain 24h</div>
                <div class="info-value">{{ currentWeather.rainLast24h.toFixed(2) }}"</div>
              </template>

              <template v-if="currentWeather.rainSinceMidnight != null">
                <div class="info-label">Rain today</div>
                <div class="info-value">{{ currentWeather.rainSinceMidnight.toFixed(2) }}"</div>
              </template>
            </div>

            <!-- History chart -->
            <div
              class="wx-section-label px-3 pt-3 pb-1 text-caption text-medium-emphasis font-weight-medium"
            >
              HISTORY
            </div>
            <div class="d-flex justify-center px-3 pb-2">
              <v-btn-toggle
                v-model="weatherRange"
                density="compact"
                variant="outlined"
                mandatory
                color="primary"
              >
                <v-btn value="24h" size="small">24 h</v-btn>
                <v-btn value="7d" size="small">7 d</v-btn>
              </v-btn-toggle>
            </div>
            <div v-if="hasWeatherChartData" class="wx-chart-wrap px-3 pb-3">
              <Line
                :data="weatherChartData"
                :options="weatherChartOptions"
                :plugins="[crosshairPlugin]"
              />
            </div>
            <div v-else-if="!weatherLoading" class="text-caption text-medium-emphasis px-3 pb-3">
              No chart data for this range
            </div>
          </template>
        </template>
      </div>
    </div>

    <!-- Footer (mock): Full page ↗ + message -->
    <div v-if="!props.pageVariant && props.showPageLink && callsign" class="panel-foot">
      <v-btn
        variant="tonal"
        color="primary"
        size="small"
        class="flex-grow-1"
        :to="`/stations/${encodeURIComponent(callsign)}`"
      >
        Full page ↗
      </v-btn>
      <v-btn
        icon="mdi-email-outline"
        variant="text"
        size="small"
        title="Message station"
        to="/messages"
      />
    </div>

    <PacketInspectionDialog
      :packet-id="inspectedPacketId"
      @close="inspectedPacketId = null"
      @select-station="onDialogSelectStation"
    />
  </div>
</template>

<style scoped>
.detail-panel-content {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  background: rgb(var(--v-theme-surface));
}

.panel-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px 6px;
}

.panel-callsign {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
  font-size: 1.05rem;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-foot {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  flex-shrink: 0;
}

.raw-packet-box {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.68rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  background: rgba(var(--v-theme-on-surface), 0.06);
  border-radius: 7px;
  padding: 7px 9px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.symbol-icon {
  image-rendering: pixelated;
  border-radius: 2px;
}

.panel-main {
  display: flex;
  flex-direction: column;
  flex: 1;
  overflow: hidden;
}

/* Horizontal text tabs under the header (mock: Info · Pkts · Wx · Stats · Sig) */
.tab-row {
  display: flex;
  gap: 2px;
  padding: 0 10px;
  overflow-x: auto;
  flex-shrink: 0;
}

.tab-btn {
  position: relative;
  display: inline-flex;
  align-items: center;
  padding: 7px 10px;
  font-size: 0.8rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  cursor: pointer;
  white-space: nowrap;
  transition: color 0.15s;
  outline: none;
}

.tab-btn:hover {
  color: rgba(var(--v-theme-on-surface), 0.9);
}

.tab-btn:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: -2px;
}

.tab-btn--active {
  color: rgb(var(--v-theme-primary));
  border-bottom-color: rgb(var(--v-theme-primary));
}

.tab-new-dot {
  position: absolute;
  top: 5px;
  right: 3px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: rgb(var(--v-theme-primary));
}

.panel-body {
  flex: 1;
  overflow-y: auto;
  padding: 8px 0;
}

.info-section {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 12px;
  padding: 4px 12px;
  align-items: start;
}

.info-label {
  font-size: 0.75rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  font-weight: 500;
  white-space: nowrap;
  padding-top: 2px;
}

.info-value {
  font-size: 0.875rem;
  word-break: break-word;
}

.osm-link {
  color: rgba(var(--v-theme-primary), 1);
  text-decoration: none;
  font-size: 0.75rem;
}

.packet-row {
  padding: 6px 12px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  cursor: pointer;
  transition: background 0.15s;
}

.packet-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.05);
}

.wx-section-label {
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.wx-chart-wrap {
  height: 200px;
}

.bar-wrap {
  height: 96px;
}

.signal-chart-wrap {
  height: 120px;
}

.heard-via-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}

.heard-via-dot--direct {
  background: #4caf50;
}

.signal-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(110px, 1fr));
  gap: 8px;
}

.signal-tile {
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-radius: 8px;
  padding: 8px 10px;
}

.signal-tile-value {
  font-size: 1rem;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.signal-tile-label {
  font-size: 0.68rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
}

.heard-bar-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 2px 0;
}

.heard-bar-label {
  flex: 0 0 90px;
  font-size: 0.72rem;
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.heard-bar-track {
  flex: 1;
  height: 8px;
  border-radius: 4px;
  background: rgba(var(--v-theme-on-surface), 0.08);
  overflow: hidden;
}

.heard-bar-fill {
  height: 100%;
  border-radius: 4px;
  background: rgb(var(--v-theme-rf));
}

.heard-bar-fill--direct {
  background: rgb(var(--v-theme-success));
}

.heard-bar-count {
  flex: 0 0 66px;
  text-align: right;
  font-size: 0.7rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
  font-variant-numeric: tabular-nums;
}

.heard-via-dot--digi {
  background: #f9a825;
}
</style>

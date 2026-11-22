<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { Bar } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Tooltip as ChartTooltip,
  type TooltipItem,
} from 'chart.js'
import { useTheme } from 'vuetify'
import { getModemRxHistory, type ModemRxHistoryDto } from '@/api/modemApi'
import { apiErrorDetail } from '@/api/axios'
import { formatDateLabel, formatTimeOfDay } from '@/utils/time'
import { axisColors, decodeColors, withAlpha } from '@/components/network/chartColors'

ChartJS.register(CategoryScale, LinearScale, BarElement, ChartTooltip)

/** One radio's decoded and bad-CRC frame counts over a chosen range, from the stored per-minute history. */
const props = defineProps<{ radioId: string }>()

const RANGES = [
  { hours: 1, label: '1 h' },
  { hours: 6, label: '6 h' },
  { hours: 24, label: '24 h' },
  { hours: 168, label: '7 d' },
  { hours: 720, label: '30 d' },
] as const

const REFRESH_MS = 30_000

const hours = ref<number>(24)
const history = ref<ModemRxHistoryDto | null>(null)
const error = ref<string | null>(null)
let timer: ReturnType<typeof setInterval> | null = null
let requestSeq = 0

async function refresh() {
  const seq = ++requestSeq
  try {
    const data = await getModemRxHistory(props.radioId, hours.value)
    // A slower response for a range the operator has since left is dropped.
    if (seq !== requestSeq) return
    history.value = data
    error.value = null
  } catch (e) {
    if (seq === requestSeq) error.value = apiErrorDetail(e, 'Decode history unavailable')
  }
}

watch([hours, () => props.radioId], () => void refresh())

onMounted(() => {
  void refresh()
  timer = setInterval(() => void refresh(), REFRESH_MS)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
})

const theme = useTheme()

const palette = computed(() => {
  const colors = theme.current.value.colors
  return { ...decodeColors(colors), ...axisColors(colors) }
})

const buckets = computed(() => history.value?.buckets ?? [])

const bucketLabel = computed(() => {
  const m = history.value?.bucketMinutes ?? 1
  return m < 60 ? `${m} min` : `${m / 60} h`
})

/** Ranges longer than a day need the date on each tick as well as the time. */
function tickLabel(iso: string): string {
  const time = formatTimeOfDay(iso, { seconds: false })
  return hours.value > 24
    ? `${formatDateLabel(iso, { month: 'short', day: 'numeric' })} ${time}`
    : time
}

function fullLabel(iso: string): string {
  return `${formatDateLabel(iso, { month: 'short', day: 'numeric' })} ${formatTimeOfDay(iso, { seconds: false })}`
}

function percent(part: number, whole: number): string {
  return whole > 0 ? `${Math.round((part / whole) * 100)} %` : '—'
}

const totals = computed(() => {
  let decoded = 0
  let invalid = 0
  let repaired = 0
  for (const b of buckets.value) {
    decoded += b.decodedFrames
    invalid += b.invalidFrames
    repaired += b.repairedFrames
  }
  return { decoded, invalid, repaired, frames: decoded + invalid }
})

const data = computed(() => ({
  labels: buckets.value.map((b) => tickLabel(b.start)),
  datasets: [
    {
      label: 'Bad CRC',
      data: buckets.value.map((b) => b.invalidFrames),
      backgroundColor: palette.value.badCrc,
      borderRadius: 2,
      stack: 'frames',
    },
    {
      label: 'Decoded',
      data: buckets.value.map((b) => b.decodedFrames),
      backgroundColor: withAlpha(palette.value.decoded, 0.85),
      borderRadius: 2,
      stack: 'frames',
    },
  ],
}))

const options = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: false as const,
  interaction: { mode: 'index' as const, intersect: false },
  plugins: {
    legend: { display: false },
    tooltip: {
      enabled: true as const,
      callbacks: {
        title: (items: TooltipItem<'bar'>[]) => {
          const b = buckets.value[items[0]?.dataIndex ?? -1]
          return b ? `${fullLabel(b.start)} · ${bucketLabel.value}` : ''
        },
        footer: (items: TooltipItem<'bar'>[]) => {
          const b = buckets.value[items[0]?.dataIndex ?? -1]
          if (!b) return ''
          const lines = [
            `Bad CRC share: ${percent(b.invalidFrames, b.invalidFrames + b.decodedFrames)}`,
          ]
          if (b.repairedFrames > 0) lines.push(`Recovered by bit repair: ${b.repairedFrames}`)
          return lines
        },
      },
    },
  },
  scales: {
    x: {
      stacked: true,
      ticks: {
        font: { size: 10 },
        maxRotation: 0,
        autoSkip: true,
        maxTicksLimit: 8,
        color: palette.value.ink,
      },
      grid: { display: false },
    },
    y: {
      stacked: true,
      beginAtZero: true,
      ticks: { font: { size: 10 }, color: palette.value.ink, precision: 0 },
      grid: { color: palette.value.grid },
    },
  },
}))
</script>

<template>
  <div class="d-flex align-center flex-wrap ga-3 mb-3">
    <div class="text-body-2">
      <template v-if="history">
        Decoded <strong>{{ totals.decoded.toLocaleString() }}</strong> · Bad CRC
        <strong>{{ totals.invalid.toLocaleString() }}</strong>
        ({{ percent(totals.invalid, totals.frames) }} of frames)
        <template v-if="totals.repaired">
          · Recovered by bit repair <strong>{{ totals.repaired.toLocaleString() }}</strong>
        </template>
      </template>
    </div>
    <v-spacer />
    <v-btn-toggle
      v-model="hours"
      mandatory
      density="compact"
      variant="outlined"
      aria-label="Decode history range"
    >
      <v-btn v-for="r in RANGES" :key="r.hours" :value="r.hours" size="small">
        {{ r.label }}
      </v-btn>
    </v-btn-toggle>
  </div>

  <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-2">
    {{ error }}
  </v-alert>

  <div
    v-if="history && totals.frames === 0"
    class="chart-wrap d-flex align-center justify-center text-body-2 text-medium-emphasis"
  >
    No frames recorded in this range.
  </div>
  <div
    v-else-if="history"
    class="chart-wrap"
    role="img"
    :aria-label="`Decoded and bad CRC frames per ${bucketLabel}`"
  >
    <Bar :data="data" :options="options" />
  </div>
  <div v-else class="chart-wrap" />

  <div class="d-flex align-center flex-wrap ga-4 mt-2 text-caption">
    <span class="d-flex align-center ga-1">
      <span class="swatch" :style="{ background: withAlpha(palette.decoded, 0.85) }" />
      Decoded
    </span>
    <span class="d-flex align-center ga-1">
      <span class="swatch" :style="{ background: palette.badCrc }" />
      Bad CRC
    </span>
    <span class="text-medium-emphasis">Frames per {{ bucketLabel }}</span>
  </div>
</template>

<style scoped>
.chart-wrap {
  height: 200px;
  position: relative;
}

.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}
</style>

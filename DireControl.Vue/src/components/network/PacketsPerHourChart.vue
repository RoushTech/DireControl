<script setup lang="ts">
import { computed } from 'vue'
import { Bar } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Tooltip as ChartTooltip,
} from 'chart.js'
import { useTheme } from 'vuetify'
import { hourOf } from '@/utils/time'
import { axisColors, sourceColors, withAlpha } from './chartColors'

ChartJS.register(CategoryScale, LinearScale, BarElement, ChartTooltip)

const props = defineProps<{
  /** RF packets per hour, oldest first (index 23 = current hour). */
  rf: number[]
  /** APRS-IS packets per hour, same bucketing. */
  is: number[]
  /** Epoch ms the series was fetched at; the last bucket ends here. */
  asOf: number
}>()

const theme = useTheme()

const palette = computed(() => {
  const colors = theme.current.value.colors
  return { ...sourceColors(colors), ...axisColors(colors) }
})

const labels = computed(() =>
  Array.from({ length: 24 }, (_, i) => {
    const h = hourOf(props.asOf - (23 - i) * 3_600_000)
    return `${String(h).padStart(2, '0')}:00`
  }),
)

const data = computed(() => ({
  labels: labels.value,
  datasets: [
    {
      label: 'RF',
      data: props.rf,
      backgroundColor: withAlpha(palette.value.rf, 0.85),
      borderRadius: 2,
      stack: 'packets',
    },
    {
      label: 'APRS-IS',
      data: props.is,
      backgroundColor: withAlpha(palette.value.is, 0.55),
      borderRadius: 2,
      stack: 'packets',
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
    tooltip: { enabled: true as const },
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
  <div class="chart-wrap" role="img" aria-label="Packets per hour, RF and APRS-IS, last 24 hours">
    <Bar :data="data" :options="options" />
  </div>
  <div class="d-flex align-center ga-4 mt-2 text-caption">
    <span class="d-flex align-center ga-1">
      <span class="swatch" :style="{ background: withAlpha(palette.rf, 0.85) }" />
      RF
    </span>
    <span class="d-flex align-center ga-1">
      <span class="swatch" :style="{ background: withAlpha(palette.is, 0.55) }" />
      APRS-IS
    </span>
  </div>
</template>

<style scoped>
.chart-wrap {
  height: 180px;
  position: relative;
}

.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}
</style>

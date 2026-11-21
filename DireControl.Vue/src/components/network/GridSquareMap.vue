<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { useTheme } from 'vuetify'

const props = defineProps<{ gridSquares: string[] }>()

const theme = useTheme()
const mapEl = ref<HTMLElement | null>(null)
let map: L.Map | null = null
let layers: L.Rectangle[] = []
let resizeObserver: ResizeObserver | null = null

/** Maidenhead 4-character square → lat/lon bounds. */
function gridSquareToBounds(grid: string): [[number, number], [number, number]] | null {
  if (grid.length < 4) return null
  const c0 = grid.charCodeAt(0) - 65
  const c1 = grid.charCodeAt(1) - 65
  if (c0 < 0 || c0 > 17 || c1 < 0 || c1 > 17) return null
  const d2 = parseInt(grid.charAt(2), 10)
  const d3 = parseInt(grid.charAt(3), 10)
  if (isNaN(d2) || isNaN(d3)) return null
  const lon = c0 * 20 - 180 + d2 * 2
  const lat = c1 * 10 - 90 + d3
  return [
    [lat, lon],
    [lat + 1, lon + 2],
  ]
}

function draw() {
  if (!map) return
  layers.forEach((r) => r.remove())
  layers = []
  const color = String(theme.current.value.colors.success)
  for (const sq of props.gridSquares) {
    const bounds = gridSquareToBounds(sq)
    if (!bounds) continue
    const rect = L.rectangle(bounds, {
      color,
      fillColor: color,
      fillOpacity: 0.45,
      weight: 1,
      opacity: 0.8,
    }).addTo(map)
    rect.bindTooltip(sq, { direction: 'top' })
    layers.push(rect)
  }
}

onMounted(() => {
  if (!mapEl.value) return
  map = L.map(mapEl.value, { center: [20, 0], zoom: 1, scrollWheelZoom: false })
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap contributors',
    maxZoom: 6,
  }).addTo(map)
  draw()
  // The map can mount inside a tab that is still animating in; keep its size honest.
  resizeObserver = new ResizeObserver(() => map?.invalidateSize())
  resizeObserver.observe(mapEl.value)
})

watch(() => props.gridSquares, draw)
watch(() => theme.current.value.colors.success, draw)

onUnmounted(() => {
  resizeObserver?.disconnect()
  map?.remove()
  map = null
})
</script>

<template>
  <div ref="mapEl" class="grid-map" />
</template>

<style scoped>
.grid-map {
  height: 280px;
  border-radius: 4px;
  overflow: hidden;
}
</style>

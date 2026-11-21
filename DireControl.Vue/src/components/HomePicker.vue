<script setup lang="ts">
/**
 * Home-position picker: Leaflet map click, latitude/longitude fields and a
 * Maidenhead grid square, all kept in step. Self-contained — it makes no API
 * calls; the parent saves the `v-model` value. There is no GPS on this
 * hardware, so the position is always set by hand.
 */
import { onMounted, onUnmounted, ref, watch } from 'vue'
import L from 'leaflet'

export interface HomePosition {
  lat: number
  lon: number
}

const model = defineModel<HomePosition | null>({ default: null })
/** False while the fields hold something that isn't a valid position. */
const valid = defineModel<boolean>('valid', { default: true })

const latText = ref('')
const lonText = ref('')
const gridText = ref('')
const mapEl = ref<HTMLElement | null>(null)

let map: L.Map | null = null
let marker: L.Marker | null = null
let resizeObserver: ResizeObserver | null = null

// ─── Maidenhead ───────────────────────────────────────────────────────────────
function toGrid(lat: number, lon: number): string {
  const A = 'A'.charCodeAt(0)
  const x = Math.min(Math.max(lon + 180, 0), 359.9999999)
  const y = Math.min(Math.max(lat + 90, 0), 179.9999999)
  const field =
    String.fromCharCode(A + Math.floor(x / 20)) + String.fromCharCode(A + Math.floor(y / 10))
  const square = `${Math.floor((x % 20) / 2)}${Math.floor(y % 10)}`
  const sub =
    String.fromCharCode(A + Math.floor(((x % 2) * 60) / 5)).toLowerCase() +
    String.fromCharCode(A + Math.floor(((y % 1) * 60) / 2.5)).toLowerCase()
  return field + square + sub
}

/** Centre of a 4- or 6-character grid square. */
function fromGrid(grid: string): HomePosition | null {
  const g = grid.trim().toUpperCase()
  if (!/^[A-R]{2}[0-9]{2}([A-X]{2})?$/.test(g)) return null
  const A = 'A'.charCodeAt(0)
  let lon = (g.charCodeAt(0) - A) * 20 - 180 + Number(g[2]) * 2
  let lat = (g.charCodeAt(1) - A) * 10 - 90 + Number(g[3])
  if (g.length === 6) {
    lon += ((g.charCodeAt(4) - A) * 5) / 60 + 2.5 / 60
    lat += ((g.charCodeAt(5) - A) * 2.5) / 60 + 1.25 / 60
  } else {
    lon += 1
    lat += 0.5
  }
  return { lat, lon }
}

// ─── Fields ───────────────────────────────────────────────────────────────────
const latRules = [
  (v: string) =>
    v.trim() === '' || (isFinite(Number(v)) && Math.abs(Number(v)) <= 90) || '−90 to 90',
]
const lonRules = [
  (v: string) =>
    v.trim() === '' || (isFinite(Number(v)) && Math.abs(Number(v)) <= 180) || '−180 to 180',
]
const gridRules = [(v: string) => v.trim() === '' || !!fromGrid(v) || 'e.g. DM79 or DM79lr']

function parsed(): HomePosition | null | 'invalid' {
  const latS = latText.value.trim()
  const lonS = lonText.value.trim()
  if (latS === '' && lonS === '') return null
  const lat = Number(latS)
  const lon = Number(lonS)
  if (latS === '' || lonS === '' || !isFinite(lat) || !isFinite(lon)) return 'invalid'
  if (Math.abs(lat) > 90 || Math.abs(lon) > 180) return 'invalid'
  return { lat, lon }
}

function same(a: HomePosition | null, b: HomePosition | null): boolean {
  if (!a || !b) return a === b
  return Math.abs(a.lat - b.lat) < 1e-7 && Math.abs(a.lon - b.lon) < 1e-7
}

function showPosition(pos: HomePosition | null) {
  latText.value = pos ? pos.lat.toFixed(6) : ''
  lonText.value = pos ? pos.lon.toFixed(6) : ''
  gridText.value = pos ? toGrid(pos.lat, pos.lon) : ''
}

function commit(pos: HomePosition | null) {
  valid.value = true
  if (!same(pos, model.value)) model.value = pos
  syncMarker(pos)
}

/** Lat/lon typed — publish once both make a valid position. */
function onCoordsInput() {
  const p = parsed()
  if (p === 'invalid') {
    valid.value = false
    return
  }
  gridText.value = p ? toGrid(p.lat, p.lon) : ''
  commit(p)
}

function onGridChange() {
  if (gridText.value.trim() === '') return
  const p = fromGrid(gridText.value)
  if (!p) return
  latText.value = p.lat.toFixed(6)
  lonText.value = p.lon.toFixed(6)
  commit(p)
  map?.setView([p.lat, p.lon], Math.max(map.getZoom(), 9))
}

// External changes (the parent loaded or reset the value).
watch(
  model,
  (pos) => {
    const p = parsed()
    if (p !== 'invalid' && same(p, pos)) return
    showPosition(pos)
    valid.value = true
    syncMarker(pos)
  },
  { immediate: true },
)

// ─── Map ──────────────────────────────────────────────────────────────────────
function syncMarker(pos: HomePosition | null) {
  if (!map) return
  if (!pos) {
    if (marker) {
      marker.remove()
      marker = null
    }
    return
  }
  if (marker) {
    marker.setLatLng([pos.lat, pos.lon])
  } else {
    // Same home pin the map uses — Leaflet's default marker PNGs aren't
    // bundled by Vite and render as a broken-image box.
    const icon = L.divIcon({
      html: `<div style="background:#1976D2;border-radius:50%;width:32px;height:32px;display:flex;align-items:center;justify-content:center;border:3px solid white;box-shadow:0 2px 6px rgba(0,0,0,0.5);"><span class='mdi mdi-home' style='color:white;font-size:18px;line-height:1;'></span></div>`,
      className: '',
      iconSize: [32, 32],
      iconAnchor: [16, 16],
    })
    marker = L.marker([pos.lat, pos.lon], { icon }).addTo(map)
  }
  map.panTo([pos.lat, pos.lon])
}

onMounted(() => {
  if (!mapEl.value) return
  const pos = model.value
  map = L.map(mapEl.value).setView(pos ? [pos.lat, pos.lon] : [39.0, -98.0], pos ? 11 : 4)
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap contributors',
    maxZoom: 19,
  }).addTo(map)
  map.on('click', (e: L.LeafletMouseEvent) => {
    const p = { lat: Number(e.latlng.lat.toFixed(6)), lon: Number(e.latlng.lng.toFixed(6)) }
    showPosition(p)
    commit(p)
  })
  syncMarker(pos)
  // Containers that start hidden or resize (dialogs, steppers) need a nudge.
  resizeObserver = new ResizeObserver(() => map?.invalidateSize())
  resizeObserver.observe(mapEl.value)
})

onUnmounted(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
  map?.remove()
  map = null
  marker = null
})
</script>

<template>
  <div class="home-picker">
    <div
      ref="mapEl"
      class="home-picker-map"
      role="application"
      aria-label="Map: click or tap to set the home position"
    />
    <div class="home-picker-fields">
      <v-text-field
        v-model="latText"
        label="Latitude"
        inputmode="decimal"
        :rules="latRules"
        hide-details="auto"
        @update:model-value="onCoordsInput"
      />
      <v-text-field
        v-model="lonText"
        label="Longitude"
        inputmode="decimal"
        :rules="lonRules"
        hide-details="auto"
        @update:model-value="onCoordsInput"
      />
      <v-text-field
        v-model="gridText"
        label="Grid square"
        :rules="gridRules"
        hide-details="auto"
        @change="onGridChange"
        @keydown.enter="onGridChange"
      />
    </div>
    <div class="text-caption text-medium-emphasis mt-2">
      Click or tap the map, type coordinates, or enter a grid square. Beacon position, range rings
      and all distances use this point.
    </div>
  </div>
</template>

<style scoped>
.home-picker-map {
  width: 100%;
  height: 260px;
  border-radius: 8px;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  margin-bottom: 12px;
}

.home-picker-fields {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 10px 12px;
  align-items: start;
}
</style>

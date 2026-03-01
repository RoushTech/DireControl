<script setup lang="ts">
import { onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { useTheme } from 'vuetify'
import L from 'leaflet'
import 'leaflet.heat'
import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { getStations, getStationTrack, getStationPackets, getSettings } from '@/api/stationsApi'
import { getGeofences, getProximityRules } from '@/api/alertsApi'
import { getCoverageGridSquares, getPacketPositions, type CoverageGridSquareDto } from '@/api/analysisApi'
import { StationType, type StationDto, type SettingsDto } from '@/types/station'
import type { PacketBroadcastDto, ResolvedPathEntry } from '@/types/packet'
import type { TileProviderConfig } from '@/types/map'
import { createAprsIcon, parseAprsSymbol } from '@/utils/aprsIcon'
import { estimatePosition } from '@/utils/estimatedPosition'
import TileProviderSwitcher from '@/components/TileProviderSwitcher.vue'
import StationDetailPanel from '@/components/StationDetailPanel.vue'
import StationListSidebar from '@/components/StationListSidebar.vue'
import RangeRingsPanel from '@/components/RangeRingsPanel.vue'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useBeaconStreamStore } from '@/stores/beaconStream'

const TILE_PROVIDERS: Record<string, TileProviderConfig> = {
  // ── Light ──────────────────────────────────────────────────────────────────
  osm: {
    name: 'OpenStreetMap',
    url: 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'light',
    group: 'light',
  },
  stadiaAlidadeSmooth: {
    name: 'Stadia Alidade Smooth',
    url: 'https://tiles.stadiamaps.com/tiles/alidade_smooth/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://stadiamaps.com/">Stadia Maps</a> &copy; <a href="https://openmaptiles.org/">OpenMapTiles</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'light',
    group: 'light',
  },
  cartoLight: {
    name: 'Carto Light',
    url: 'https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/">CARTO</a>',
    theme: 'light',
    group: 'light',
  },
  topo: {
    name: 'OpenTopoMap',
    url: 'https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://opentopomap.org">OpenTopoMap</a> (<a href="https://creativecommons.org/licenses/by-sa/3.0/">CC-BY-SA</a>)',
    theme: 'light',
    group: 'light',
  },
  esriTopo: {
    name: 'Esri World Topo',
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',
    attribution:
      'Esri, HERE, Garmin, Intermap, &copy; OpenStreetMap contributors',
    theme: 'light',
    group: 'light',
  },
  // ── Dark ───────────────────────────────────────────────────────────────────
  cartoDark: {
    name: 'Carto Dark Matter',
    url: 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/">CARTO</a>',
    theme: 'dark',
    group: 'dark',
  },
  stadiaAlidadeDark: {
    name: 'Stadia Alidade Dark',
    url: 'https://tiles.stadiamaps.com/tiles/alidade_smooth_dark/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://stadiamaps.com/">Stadia Maps</a> &copy; <a href="https://openmaptiles.org/">OpenMapTiles</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'dark',
    group: 'dark',
  },
  jawgDark: {
    name: 'Jawg Dark',
    url: 'https://tile.jawg.io/jawg-dark/{z}/{x}/{y}{r}.png?access-token={apiKey}',
    attribution:
      '&copy; <a href="https://www.jawg.io">Jawg Maps</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'dark',
    group: 'dark',
    requiresApiKey: true,
    apiKeyParam: 'jawg',
  },
  // ── Satellite ──────────────────────────────────────────────────────────────
  satellite: {
    name: 'Esri World Imagery',
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    attribution:
      '&copy; Esri &mdash; Source: Esri, Maxar, Earthstar Geographics',
    theme: 'dark',
    group: 'satellite',
  },
  // ── Specialist ─────────────────────────────────────────────────────────────
  openRailwayMap: {
    name: 'OpenRailwayMap',
    url: 'https://{s}.tiles.openrailwaymap.org/standard/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors, Style: &copy; <a href="https://www.openrailwaymap.org/">OpenRailwayMap</a>',
    theme: 'light',
    group: 'specialist',
  },
}

const HOP_SEGMENT_COLORS = ['#4A90D9', '#7B68EE', '#DA70D6'] // blue, purple, orchid
const HOP_COLOR_FALLBACK = '#FF8C00'  // dark orange for hop 3+
const UNKNOWN_SEGMENT_COLOR = '#999999'  // grey for dashed unknown segments
const FINAL_HOP_COLOR = '#2ECC71'       // green for the last hop to our station
const PATH_FADE_DURATION_S = 90
const PATH_FADE_TARGET_OPACITY = 0.3

const STORAGE_KEY = 'direcontrol-tile-provider'
const SIDEBAR_KEY = 'direcontrol-sidebar-open'
const PANEL_WIDTH_KEY = 'direcontrol-detail-panel-width'
const API_KEYS_STORAGE_KEY = 'direcontrol-api-keys'
const PANEL_MIN_WIDTH = 280
const PANEL_MAX_WIDTH_RATIO = 0.5
const DEFAULT_CENTER: [number, number] = [39.8283, -98.5795]
const DEFAULT_ZOOM = 5

const selectionStore = useStationSelectionStore()
const beaconStore = useBeaconStreamStore()
const theme = useTheme()

const mapContainer = ref<HTMLDivElement>()
const sidebarRef = ref<InstanceType<typeof StationListSidebar> | null>(null)
const map = shallowRef<L.Map>()
const tileLayer = shallowRef<L.TileLayer>()
const markers = new Map<string, L.Marker>()
const stationCache = new Map<string, StationDto>()

function loadApiKeys(): Record<string, string> {
  try {
    const raw = localStorage.getItem(API_KEYS_STORAGE_KEY)
    if (raw) return JSON.parse(raw) as Record<string, string>
  } catch { /* ignore */ }
  return {}
}
const apiKeys = ref<Record<string, string>>(loadApiKeys())

function providerIsAvailable(key: string): boolean {
  const p = TILE_PROVIDERS[key]
  if (!p) return false
  if (p.requiresApiKey && p.apiKeyParam) return !!apiKeys.value[p.apiKeyParam]
  return true
}

// Fall back to 'osm' if the persisted provider requires an API key that isn't present
const _storedProvider = localStorage.getItem(STORAGE_KEY) ?? 'osm'
const selectedProvider = ref(providerIsAvailable(_storedProvider) ? _storedProvider : 'osm')

const providerFallbackSnackbar = ref(false)
const providerFallbackMessage = ref('')

// Panel/sidebar state
const showSidebar = ref(localStorage.getItem(SIDEBAR_KEY) !== 'false')
const panelWidth = ref(Math.max(PANEL_MIN_WIDTH, parseInt(localStorage.getItem(PANEL_WIDTH_KEY) ?? '380', 10)))
const isResizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0
const detailRefreshKey = ref(0)
const stationsList = ref<StationDto[]>([])
const sessionPacketCounts = ref<Record<string, number>>({})

// Highlight marker for packet position
let highlightMarker: L.CircleMarker | null = null
let highlightTimeout: ReturnType<typeof setTimeout> | null = null

// Movement tracks state
const showTracks = ref(false)
const trackLayers = new Map<string, L.LayerGroup>()

// Packet path visualisation state
const pathLayerGroup = shallowRef<L.LayerGroup | null>(null)

// Estimated position (ghost marker) state
const showGhostMarkers = ref(true)
const ghostLayers = new Map<string, L.LayerGroup>()
let ghostUpdateInterval: ReturnType<typeof setInterval> | null = null
let staleDecayInterval: ReturnType<typeof setInterval> | null = null

// Stale station state
const staleStationCache = new Map<string, StationDto>()
const staleMarkers = new Map<string, L.Marker>()
const showStaleStations = ref(false)
const staleStationsList = ref<StationDto[]>([])

// Geofence / proximity-rule circle overlays
const showOverlays = ref(false)
let overlayLayerGroup: L.LayerGroup | null = null

// Settings cache
let settingsCache: SettingsDto | null = null

// Home station state
let homeMarker: L.Marker | null = null
const showNoHomePositionBanner = ref(false)
let homePositionPollInterval: ReturnType<typeof setInterval> | null = null

// Range rings state
const RINGS_STORAGE_KEY = 'direcontrol-range-rings'
function loadRingDistancesFromStorage(): number[] {
  try {
    const raw = localStorage.getItem(RINGS_STORAGE_KEY)
    if (raw) {
      const parsed = JSON.parse(raw) as unknown
      if (Array.isArray(parsed) && parsed.every((v: unknown) => typeof v === 'number' && v > 0)) {
        return parsed as number[]
      }
    }
  } catch {
    // ignore
  }
  return [5, 10, 25]
}
const showRings = ref(false)
const ringDistances = ref<number[]>(loadRingDistancesFromStorage())
let ringLayerGroup: L.LayerGroup | null = null

// Heatmap state
const showHeatmap = ref(false)
const heatmapLoading = ref(false)
let heatmapLayer: L.HeatLayer | null = null
let heatmapPositions: [number, number][] | null = null

// Coverage state
const showCoverage = ref(false)
const coverageLoading = ref(false)
let coverageLayerGroup: L.LayerGroup | null = null
let coverageData: CoverageGridSquareDto[] | null = null

let connection: HubConnection | null = null

function invalidateSizeAfterTransition() {
  setTimeout(() => map.value?.invalidateSize(), 320)
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleTimeString()
}

function popupContent(callsign: string, lastSeen: string, lat: number, lon: number): string {
  return `<strong>${callsign}</strong><br>Last seen: ${formatTime(lastSeen)}<br>Coords: ${lat.toFixed(4)}, ${lon.toFixed(4)}`
}

function buildIcon(station: StationDto | undefined): L.DivIcon {
  if (!station) return createAprsIcon('/', '/')
  const { table, code } = parseAprsSymbol(station.symbol)
  const isMobile =
    station.stationType === StationType.Mobile ||
    (station.lastSpeed != null && station.lastSpeed > 0)
  const heading = isMobile ? station.lastHeading : null
  const baseIcon = createAprsIcon(table, code, heading, station.isWeatherStation)

  if (!station.isOnWatchList) return baseIcon

  // Add a small star badge to watched stations
  const baseHtml = (baseIcon.options.html as string) ?? ''
  const html = `<div style="position:relative;display:inline-block;">${baseHtml}<div style="position:absolute;top:-5px;right:-5px;font-size:11px;line-height:1;text-shadow:0 0 3px rgba(0,0,0,0.9);z-index:1;pointer-events:none;">★</div></div>`
  return L.divIcon({
    html,
    className: 'aprs-icon-container',
    iconSize: [24, 24],
    iconAnchor: [12, 12],
    popupAnchor: [0, -12],
  })
}

function isMobileStation(station: StationDto): boolean {
  return station.stationType === StationType.Mobile
}

function updateStationsList() {
  stationsList.value = [...stationCache.values()]
}

function updateStaleStationsList() {
  staleStationsList.value = [...staleStationCache.values()]
}

function buildStaleIcon(station: StationDto): L.DivIcon {
  const { table, code } = parseAprsSymbol(station.symbol)
  return createAprsIcon(table, code, null, false, 0.35)
}

function removeStaleMarker(callsign: string) {
  const existing = staleMarkers.get(callsign)
  if (existing) {
    existing.remove()
    staleMarkers.delete(callsign)
  }
  staleStationCache.delete(callsign)
}

function clearStaleFromMap() {
  for (const marker of staleMarkers.values()) {
    marker.remove()
  }
  staleMarkers.clear()
  staleStationCache.clear()
  updateStaleStationsList()
}

function hideStaleMarkers() {
  for (const marker of staleMarkers.values()) {
    marker.remove()
  }
  staleMarkers.clear()
}

function showCachedStaleMarkers() {
  for (const s of staleStationCache.values()) {
    if (s.lastLat != null && s.lastLon != null) {
      addStaleMarker(s)
    }
  }
}

function addStaleMarker(station: StationDto) {
  if (!map.value || station.lastLat == null || station.lastLon == null) return
  staleStationCache.set(station.callsign, station)
  const existing = staleMarkers.get(station.callsign)
  if (existing) {
    existing.remove()
    staleMarkers.delete(station.callsign)
  }
  const icon = buildStaleIcon(station)
  const marker = L.marker([station.lastLat, station.lastLon], { icon, opacity: 0.5 })
    .bindPopup(`<strong>${station.callsign}</strong><br><em>Stale</em><br>Last seen: ${formatTime(station.lastSeen)}`)
  marker.on('click', () => onMarkerClick(station.callsign))
  marker.addTo(map.value)
  staleMarkers.set(station.callsign, marker)
}

async function loadStaleStations() {
  if (!map.value) return
  try {
    const all = await getStations(true)
    clearStaleFromMap()
    for (const s of all) {
      if (stationCache.has(s.callsign)) continue
      staleStationCache.set(s.callsign, s)
      if (showStaleStations.value && s.lastLat != null && s.lastLon != null) {
        addStaleMarker(s)
      }
    }
    updateStaleStationsList()
  } catch (err) {
    console.error('Failed to load stale stations:', err)
  }
}

function toggleStaleStations() {
  showStaleStations.value = !showStaleStations.value
  if (showStaleStations.value) {
    showCachedStaleMarkers()
  } else {
    hideStaleMarkers()
  }
}

// --- Overlays (Geofences + Proximity Rules) ---

async function loadAndDrawOverlays() {
  if (!map.value) return
  clearOverlays()
  const group = L.layerGroup()
  try {
    const [fences, rules] = await Promise.all([getGeofences(), getProximityRules()])
    for (const f of fences) {
      if (!f.isActive) continue
      L.circle([f.centerLat, f.centerLon], {
        radius: f.radiusMeters,
        color: '#43A047',
        weight: 2,
        fillOpacity: 0.08,
        dashArray: '6 4',
      })
        .bindTooltip(`Geofence: ${f.name}<br>${f.radiusMeters >= 1000 ? (f.radiusMeters / 1000).toFixed(1) + ' km' : Math.round(f.radiusMeters) + ' m'}`, { sticky: true })
        .addTo(group)
    }
    for (const r of rules) {
      if (!r.isActive) continue
      L.circle([r.centerLat, r.centerLon], {
        radius: r.radiusMetres,
        color: '#1E88E5',
        weight: 2,
        fillOpacity: 0.08,
        dashArray: '6 4',
      })
        .bindTooltip(`Proximity: ${r.name}${r.targetCallsign ? ` (${r.targetCallsign})` : ''}<br>${r.radiusMetres >= 1000 ? (r.radiusMetres / 1000).toFixed(1) + ' km' : Math.round(r.radiusMetres) + ' m'}`, { sticky: true })
        .addTo(group)
    }
  } catch (err) {
    console.error('Failed to load overlays:', err)
  }
  overlayLayerGroup = group
  group.addTo(map.value)
}

function clearOverlays() {
  if (overlayLayerGroup) {
    overlayLayerGroup.remove()
    overlayLayerGroup = null
  }
}

function toggleOverlays() {
  showOverlays.value = !showOverlays.value
  if (showOverlays.value) {
    loadAndDrawOverlays()
  } else {
    clearOverlays()
  }
}

// --- Range Rings ---

async function ensureSettings(): Promise<SettingsDto | null> {
  if (!settingsCache) {
    try {
      settingsCache = await getSettings()
    } catch (err) {
      console.error('Failed to load settings:', err)
      return null
    }
  }
  return settingsCache
}

function clearHomeMarker() {
  if (homeMarker) {
    homeMarker.remove()
    homeMarker = null
  }
}

async function drawHomeMarker() {
  clearHomeMarker()
  if (!map.value) return
  const settings = await ensureSettings()
  if (!settings?.homePosition) return
  const { lat, lon } = settings.homePosition
  const icon = L.divIcon({
    html: `<div style="background:#1976D2;border-radius:50%;width:32px;height:32px;display:flex;align-items:center;justify-content:center;border:3px solid white;box-shadow:0 2px 6px rgba(0,0,0,0.5);"><span class='mdi mdi-home' style='color:white;font-size:18px;line-height:1;'></span></div>`,
    className: '',
    iconSize: [32, 32],
    iconAnchor: [16, 16],
    popupAnchor: [0, -20],
  })
  homeMarker = L.marker([lat, lon], { icon, zIndexOffset: 1000 })
    .addTo(map.value)
    .bindPopup(`<strong>${settings.ourCallsign}</strong><br>Home Station`)
}

async function checkHomePosition() {
  settingsCache = null
  const settings = await ensureSettings()
  if (settings?.homePosition) {
    showNoHomePositionBanner.value = false
    await drawHomeMarker()
    if (homePositionPollInterval) {
      clearInterval(homePositionPollInterval)
      homePositionPollInterval = null
    }
  }
}

function clearRings() {
  if (ringLayerGroup) {
    ringLayerGroup.remove()
    ringLayerGroup = null
  }
}

function getRingStyle(providerKey: string): { color: string; weight: number; labelColor: string; labelBg: string } {
  const dark = TILE_PROVIDERS[providerKey]?.theme === 'dark'
  return dark
    ? { color: '#FFFFFF', weight: 2.5, labelColor: '#ffffff', labelBg: 'rgba(20,20,30,0.75)' }
    : { color: '#1a1a1a', weight: 2, labelColor: '#1a1a1a', labelBg: 'rgba(255,255,255,0.80)' }
}

async function drawRings() {
  if (!map.value) return
  clearRings()
  const settings = await ensureSettings()
  if (!settings?.homePosition) return
  const lat = settings.homePosition.lat
  const lon = settings.homePosition.lon
  const style = getRingStyle(selectedProvider.value)
  const group = L.layerGroup()
  for (const dist of ringDistances.value) {
    const radiusMeters = dist * 1609.344
    L.circle([lat, lon], {
      radius: radiusMeters,
      color: style.color,
      weight: style.weight,
      fill: false,
      opacity: 0.85,
      dashArray: '8, 6',
    }).addTo(group)
    // Label marker placed at the north edge of the ring
    const labelLat = lat + radiusMeters / 111_320
    L.marker([labelLat, lon], {
      icon: L.divIcon({
        html: `<div class="ring-label" style="color: ${style.labelColor}; background: ${style.labelBg}; border-color: ${style.color}40">${dist} mi</div>`,
        className: '',
        iconSize: undefined,
        iconAnchor: [20, 10],
      }),
      interactive: false,
    }).addTo(group)
  }
  ringLayerGroup = group
  group.addTo(map.value)
}

// --- Heatmap ---

function clearHeatmap() {
  if (heatmapLayer) {
    heatmapLayer.remove()
    heatmapLayer = null
  }
}

async function toggleHeatmap() {
  showHeatmap.value = !showHeatmap.value
  if (!showHeatmap.value) {
    clearHeatmap()
    return
  }
  if (!map.value) return
  heatmapLoading.value = true
  try {
    if (!heatmapPositions) {
      const positions = await getPacketPositions()
      heatmapPositions = positions.map(p => [p.latitude, p.longitude] as [number, number])
    }
    heatmapLayer = L.heatLayer(heatmapPositions, {
      radius: 18,
      blur: 15,
      maxZoom: 17,
      minOpacity: 0.3,
    }).addTo(map.value)
  } catch (err) {
    console.error('Failed to load heatmap positions:', err)
    showHeatmap.value = false
  } finally {
    heatmapLoading.value = false
  }
}

// --- Coverage Map ---

function maidenheadToBounds(grid: string): L.LatLngBounds | null {
  const g = grid.toUpperCase()
  if (g.length < 4) return null
  const lonField = g.charCodeAt(0) - 65
  const latField = g.charCodeAt(1) - 65
  if (lonField < 0 || lonField > 17 || latField < 0 || latField > 17) return null
  const lonSquare = parseInt(g[2]!)
  const latSquare = parseInt(g[3]!)
  if (isNaN(lonSquare) || isNaN(latSquare)) return null
  let swLon = lonField * 20 - 180 + lonSquare * 2
  let swLat = latField * 10 - 90 + latSquare
  let lonWidth = 2
  let latHeight = 1
  if (g.length >= 6) {
    const lonSub = g.charCodeAt(4) - 65
    const latSub = g.charCodeAt(5) - 65
    if (lonSub >= 0 && lonSub < 24 && latSub >= 0 && latSub < 24) {
      swLon += lonSub * (2 / 24)
      swLat += latSub * (1 / 24)
      lonWidth = 2 / 24
      latHeight = 1 / 24
    }
  }
  return L.latLngBounds([swLat, swLon], [swLat + latHeight, swLon + lonWidth])
}

function coverageColor(packetCount: number): string {
  if (packetCount >= 50) return '#1b5e20'
  if (packetCount >= 16) return '#388e3c'
  if (packetCount >= 6) return '#81c784'
  return '#c8e6c9'
}

function clearCoverage() {
  if (coverageLayerGroup) {
    coverageLayerGroup.remove()
    coverageLayerGroup = null
  }
}

function drawCoverage() {
  if (!map.value || !coverageData) return
  clearCoverage()
  const group = L.layerGroup()
  for (const sq of coverageData) {
    const bounds = maidenheadToBounds(sq.gridSquare)
    if (!bounds) continue
    const color = coverageColor(sq.packetCount)
    L.rectangle(bounds, {
      color: '#2e7d32',
      weight: 0.5,
      fillColor: color,
      fillOpacity: 0.5,
    })
      .bindTooltip(
        `${sq.gridSquare}<br>${sq.packetCount} packet${sq.packetCount === 1 ? '' : 's'}`,
        { sticky: true },
      )
      .addTo(group)
  }
  coverageLayerGroup = group
  group.addTo(map.value)
}

async function toggleCoverage() {
  showCoverage.value = !showCoverage.value
  if (!showCoverage.value) {
    clearCoverage()
    return
  }
  if (!map.value) return
  coverageLoading.value = true
  try {
    if (!coverageData) {
      coverageData = await getCoverageGridSquares()
    }
    drawCoverage()
  } catch (err) {
    console.error('Failed to load coverage data:', err)
    showCoverage.value = false
  } finally {
    coverageLoading.value = false
  }
}

// --- Sidebar & Panel ---

function toggleSidebar() {
  showSidebar.value = !showSidebar.value
  localStorage.setItem(SIDEBAR_KEY, String(showSidebar.value))
  invalidateSizeAfterTransition()
}

function onSidebarSelectStation(callsign: string) {
  selectionStore.selectStation(callsign)
  const s = stationCache.get(callsign) ?? staleStationCache.get(callsign)
  if (s?.lastLat != null && s?.lastLon != null) {
    map.value?.flyTo([s.lastLat, s.lastLon], Math.max(map.value.getZoom(), 12))
  }
  showPacketPath(callsign)
}

function onDetailClose() {
  selectionStore.deselect()
  clearPathLayer()
  invalidateSizeAfterTransition()
}

function onHighlightPosition(lat: number, lon: number) {
  if (!map.value) return
  if (highlightTimeout) {
    clearTimeout(highlightTimeout)
    highlightTimeout = null
  }
  if (highlightMarker) {
    highlightMarker.remove()
    highlightMarker = null
  }
  highlightMarker = L.circleMarker([lat, lon], {
    radius: 12,
    color: '#FF5722',
    fillColor: '#FF5722',
    fillOpacity: 0.5,
    weight: 3,
  }).addTo(map.value)
  map.value.panTo([lat, lon])
  highlightTimeout = setTimeout(() => {
    if (highlightMarker) {
      highlightMarker.remove()
      highlightMarker = null
    }
    highlightTimeout = null
  }, 5000)
}

// --- Movement Tracks ---

async function fetchAndDrawTrack(callsign: string) {
  if (!map.value) return
  removeTrack(callsign)
  try {
    const points = await getStationTrack(callsign)
    if (points.length < 2) return
    const group = L.layerGroup()
    const totalPoints = points.length
    for (let i = 0; i < totalPoints - 1; i++) {
      const from = points[i]!
      const to = points[i + 1]!
      const opacity = 0.2 + (0.8 * (i / (totalPoints - 1)))
      const weight = 2 + Math.round(2 * (i / (totalPoints - 1)))
      const segment = L.polyline(
        [[from.latitude, from.longitude], [to.latitude, to.longitude]],
        { color: '#1976D2', weight, opacity, lineCap: 'round', lineJoin: 'round' },
      )
      group.addLayer(segment)
    }
    for (let i = 0; i < totalPoints; i++) {
      const pt = points[i]!
      const opacity = 0.3 + (0.7 * (i / Math.max(totalPoints - 1, 1)))
      const circle = L.circleMarker([pt.latitude, pt.longitude], {
        radius: 4,
        color: '#1976D2',
        fillColor: '#1976D2',
        fillOpacity: opacity,
        weight: 1,
        opacity,
      })
      const speedStr = pt.speed != null ? `${pt.speed.toFixed(1)} knots` : 'N/A'
      circle.bindPopup(
        `<strong>Track Point</strong><br>Time: ${formatTime(pt.receivedAt)}<br>Speed: ${speedStr}`,
      )
      group.addLayer(circle)
    }
    trackLayers.set(callsign, group)
    if (showTracks.value) {
      group.addTo(map.value)
    }
  } catch (err) {
    console.error(`Failed to fetch track for ${callsign}:`, err)
  }
}

function removeTrack(callsign: string) {
  const existing = trackLayers.get(callsign)
  if (existing) {
    existing.remove()
    trackLayers.delete(callsign)
  }
}

function toggleTracks() {
  showTracks.value = !showTracks.value
  if (!map.value) return
  if (showTracks.value) {
    for (const [callsign, station] of stationCache) {
      if (isMobileStation(station) && station.lastLat != null && station.lastLon != null) {
        const existing = trackLayers.get(callsign)
        if (existing) {
          existing.addTo(map.value)
        } else {
          fetchAndDrawTrack(callsign)
        }
      }
    }
  } else {
    for (const group of trackLayers.values()) {
      group.remove()
    }
  }
}

async function loadTracksForMobileStations() {
  if (!showTracks.value) return
  for (const [callsign, station] of stationCache) {
    if (isMobileStation(station) && station.lastLat != null && station.lastLon != null) {
      if (!trackLayers.has(callsign)) {
        await fetchAndDrawTrack(callsign)
      }
    }
  }
}

// --- Estimated Position (Ghost Markers) ---

function removeGhostLayer(callsign: string) {
  const existing = ghostLayers.get(callsign)
  if (existing) {
    existing.remove()
    ghostLayers.delete(callsign)
  }
}

function updateGhostLayers() {
  if (!map.value) return
  for (const [callsign, station] of stationCache) {
    if (!isMobileStation(station) || station.lastLat == null || station.lastLon == null) {
      removeGhostLayer(callsign)
      continue
    }
    const est = estimatePosition(
      station.lastLat,
      station.lastLon,
      station.lastHeading,
      station.lastSpeed,
      station.lastSeen,
    )
    if (!est) {
      removeGhostLayer(callsign)
      continue
    }
    // Rebuild ghost layer for this station
    removeGhostLayer(callsign)
    const group = L.layerGroup()

    // Dashed connector: real position → estimated position
    L.polyline(
      [[station.lastLat, station.lastLon], [est.lat, est.lon]],
      { color: '#9E9E9E', weight: 2, dashArray: '6 5', opacity: 0.7 },
    ).addTo(group)

    // Uncertainty circle around estimated position
    L.circle([est.lat, est.lon], {
      radius: est.uncertaintyRadiusMeters,
      color: '#9E9E9E',
      weight: 1,
      dashArray: '4 4',
      fillOpacity: 0.05,
      opacity: 0.5,
    }).addTo(group)

    // Ghost marker: faded APRS icon
    const { table, code } = parseAprsSymbol(station.symbol)
    const ghostIcon = createAprsIcon(table, code, station.lastHeading, false, 0.45, false)
    const elapsedStr = est.elapsedMinutes < 1 ? '<1m' : `${Math.round(est.elapsedMinutes)}m`
    L.marker([est.lat, est.lon], { icon: ghostIcon })
      .bindTooltip(`Est. — ${elapsedStr} ago`, {
        permanent: true,
        direction: 'top',
        className: 'ghost-label',
        offset: [0, -14],
      })
      .addTo(group)

    ghostLayers.set(callsign, group)
    if (showGhostMarkers.value) {
      group.addTo(map.value)
    }
  }
}

function toggleGhostMarkers() {
  showGhostMarkers.value = !showGhostMarkers.value
  if (!map.value) return
  if (showGhostMarkers.value) {
    updateGhostLayers()
  } else {
    for (const group of ghostLayers.values()) {
      group.remove()
    }
    ghostLayers.clear()
  }
}

// --- Packet Path Visualisation ---

function clearPathLayer() {
  if (pathLayerGroup.value) {
    pathLayerGroup.value.remove()
    pathLayerGroup.value = null
  }
}

// Identifies generic APRS path aliases that do not represent a specific station.
const GENERIC_ALIAS_RE = /^(WIDE|RELAY|TRACE|NCA|GATE|ECHO|IGATE)(\d(-\d)?)?$/i
function isGenericAlias(callsign: string): boolean {
  return GENERIC_ALIAS_RE.test(callsign)
}

function haversineKm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 6371
  const dLat = (lat2 - lat1) * Math.PI / 180
  const dLon = (lon2 - lon1) * Math.PI / 180
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) * Math.sin(dLon / 2) ** 2
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function bearingDeg(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const dLon = (lon2 - lon1) * Math.PI / 180
  const la1 = lat1 * Math.PI / 180
  const la2 = lat2 * Math.PI / 180
  const y = Math.sin(dLon) * Math.cos(la2)
  const x = Math.cos(la1) * Math.sin(la2) - Math.sin(la1) * Math.cos(la2) * Math.cos(dLon)
  return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360
}

function addPathArrow(
  group: L.LayerGroup,
  fromLat: number, fromLon: number,
  toLat: number, toLon: number,
  color: string,
) {
  const midLat = (fromLat + toLat) / 2
  const midLon = (fromLon + toLon) / 2
  const bearing = bearingDeg(fromLat, fromLon, toLat, toLon)
  const arrowIcon = L.divIcon({
    html: `<div style="width:0;height:0;border-left:6px solid transparent;border-right:6px solid transparent;border-bottom:12px solid ${color};transform:rotate(${bearing}deg);transform-origin:6px 6px;pointer-events:none;"></div>`,
    className: '',
    iconSize: [12, 12],
    iconAnchor: [6, 6],
  })
  group.addLayer(L.marker([midLat, midLon], { icon: arrowIcon, interactive: false, zIndexOffset: -50 }))
}

type PathSegment = {
  fromLat: number
  fromLon: number
  toLat: number
  toLon: number
  fromCallsign: string
  toCallsign: string
  hopIndexFrom: number
  isLastHop: boolean
  unknownsBetween: ResolvedPathEntry[]
}

function buildPathSegments(path: ResolvedPathEntry[]): PathSegment[] {
  const segments: PathSegment[] = []
  let lastKnownIdx = -1

  for (let i = 0; i < path.length; i++) {
    const entry = path[i]!
    if (!(entry.known ?? false) || entry.latitude == null || entry.longitude == null) continue

    if (lastKnownIdx >= 0) {
      const from = path[lastKnownIdx]!
      const unknownsBetween = path.slice(lastKnownIdx + 1, i).filter(e => !(e.known ?? false))
      segments.push({
        fromLat: from.latitude!,
        fromLon: from.longitude!,
        toLat: entry.latitude,
        toLon: entry.longitude,
        fromCallsign: from.callsign,
        toCallsign: entry.callsign,
        hopIndexFrom: from.hopIndex ?? 0,
        isLastHop: i === path.length - 1,
        unknownsBetween,
      })
    }
    lastKnownIdx = i
  }

  return segments
}

function unknownHopLabel(unknowns: ResolvedPathEntry[]): string {
  if (unknowns.length === 0) return ''
  const allGeneric = unknowns.every(e => isGenericAlias(e.callsign))
  if (allGeneric) {
    const aliases = [...new Set(unknowns.map(e => e.callsign))].join(', ')
    return `Via ${aliases} (path not traced)`
  }
  return unknowns.length === 1 ? '1 unknown hop' : `${unknowns.length} unknown hops`
}

function hopSegmentColor(hopIndexFrom: number, isLastHop: boolean, isUnknown: boolean): string {
  if (isUnknown) return UNKNOWN_SEGMENT_COLOR
  if (isLastHop) return FINAL_HOP_COLOR
  return HOP_SEGMENT_COLORS[hopIndexFrom] ?? HOP_COLOR_FALLBACK
}

async function showPacketPath(callsign: string) {
  if (!map.value) return
  clearPathLayer()
  try {
    const { items } = await getStationPackets(callsign, 1, 1)
    if (items.length === 0) return
    const packet = items[0]!
    if (!packet.resolvedPath || packet.resolvedPath.length < 2) return

    const group = L.layerGroup()
    const segments = buildPathSegments(packet.resolvedPath)
    if (segments.length === 0) return

    const totalHops = packet.resolvedPath.length - 1  // excludes source entry (hopIndex 0)

    for (const seg of segments) {
      const isUnknown = seg.unknownsBetween.length > 0
      const color = hopSegmentColor(seg.hopIndexFrom, seg.isLastHop, isUnknown)
      const distKm = haversineKm(seg.fromLat, seg.fromLon, seg.toLat, seg.toLon)
      const distStr = distKm >= 10 ? `${distKm.toFixed(0)} km` : `${distKm.toFixed(1)} km`

      const line = L.polyline(
        [[seg.fromLat, seg.fromLon], [seg.toLat, seg.toLon]],
        {
          color,
          weight: 3,
          opacity: 1,
          dashArray: isUnknown ? '8 6' : undefined,
          lineCap: 'round',
        },
      )

      // Per-segment hover tooltip
      if (isUnknown) {
        const label = unknownHopLabel(seg.unknownsBetween)
        line.bindTooltip(
          `<strong>${seg.fromCallsign} → ${seg.toCallsign}</strong> (${label})<br>${distStr}`,
          { sticky: true, direction: 'top' },
        )
      } else {
        const hopNum = seg.hopIndexFrom + 1
        const toStation = stationCache.get(seg.toCallsign) ?? staleStationCache.get(seg.toCallsign)
        const lastSeenStr = toStation ? ` · ${formatTime(toStation.lastSeen)}` : ''
        line.bindTooltip(
          `<strong>${seg.fromCallsign} → ${seg.toCallsign}</strong><br>Hop ${hopNum} of ${totalHops}${lastSeenStr}<br>${distStr}`,
          { sticky: true, direction: 'top' },
        )
      }

      group.addLayer(line)

      // Permanent midpoint label for unknown/dashed segments
      if (isUnknown) {
        const label = unknownHopLabel(seg.unknownsBetween)
        const midLat = (seg.fromLat + seg.toLat) / 2
        const midLon = (seg.fromLon + seg.toLon) / 2
        const midMarker = L.marker([midLat, midLon], {
          icon: L.divIcon({ html: '', className: '', iconSize: [0, 0], iconAnchor: [0, 0] }),
          interactive: false,
          zIndexOffset: -100,
        })
        midMarker.bindTooltip(label, { permanent: true, className: 'path-unknown-label', direction: 'top' })
        group.addLayer(midMarker)
      }

      // Arrowhead at segment midpoint indicating direction of packet flow
      addPathArrow(group, seg.fromLat, seg.fromLon, seg.toLat, seg.toLon, color)
    }

    pathLayerGroup.value = group
    group.addTo(map.value)

    // Fade from opacity 1 to PATH_FADE_TARGET_OPACITY over PATH_FADE_DURATION_S seconds.
    // Two rAF ticks ensure the browser has painted at full opacity before the transition starts.
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        group.eachLayer(layer => {
          if (layer instanceof L.Polyline) {
            const el = (layer as unknown as { _path?: SVGPathElement })._path
            if (el) {
              el.style.transition = `opacity ${PATH_FADE_DURATION_S}s linear`
              el.style.opacity = String(PATH_FADE_TARGET_OPACITY)
            }
          }
        })
      })
    })
  } catch (err) {
    console.error(`Failed to show packet path for ${callsign}:`, err)
  }
}

function onMarkerClick(callsign: string) {
  if (selectionStore.selectedCallsign === callsign) {
    selectionStore.deselect()
    clearPathLayer()
    invalidateSizeAfterTransition()
  } else {
    selectionStore.selectStation(callsign)
    showPacketPath(callsign)
    invalidateSizeAfterTransition()
  }
}

// --- Beacon Flash & Stale Decay ---

function packetTypeFlashClass(parsedType: string): string {
  switch (parsedType) {
    case 'Position':  return 'beacon-flash-position'
    case 'Message':   return 'beacon-flash-message'
    case 'Weather':   return 'beacon-flash-weather'
    case 'Telemetry': return 'beacon-flash-telemetry'
    default:          return 'beacon-flash-unknown'
  }
}

function triggerBeaconFlash(callsign: string, parsedType: string) {
  const el = markers.get(callsign)?.getElement()
  if (!el) return
  const iconEl = el.querySelector('.aprs-icon')
  if (!iconEl) return
  const ring = document.createElement('div')
  ring.className = `beacon-flash ${packetTypeFlashClass(parsedType)}`
  iconEl.appendChild(ring)
  ring.addEventListener('animationend', () => ring.remove(), { once: true })
}

function updateStaleDecayClasses() {
  const now = Date.now()
  const expiryMs = (settingsCache?.stationExpiryTimeoutMinutes ?? 120) * 60 * 1000
  for (const [callsign, station] of stationCache) {
    const marker = markers.get(callsign)
    if (!marker) continue
    const el = marker.getElement()
    if (!el) continue
    const ageMs = now - new Date(station.lastSeen).getTime()
    const ratio = ageMs / expiryMs
    el.classList.remove('stale-light', 'stale-medium', 'stale-heavy')
    if (ratio >= 0.8) el.classList.add('stale-heavy')
    else if (ratio >= 0.55) el.classList.add('stale-medium')
    else if (ratio >= 0.3) el.classList.add('stale-light')
  }
}

// --- Marker Management ---

function addOrUpdateMarker(callsign: string, lat: number, lon: number, lastSeen: string) {
  if (!map.value) return
  const station = stationCache.get(callsign)
  const existing = markers.get(callsign)
  if (existing) {
    existing.setLatLng([lat, lon])
    existing.setPopupContent(popupContent(callsign, lastSeen, lat, lon))
  } else {
    const icon = buildIcon(station)
    const marker = L.marker([lat, lon], { icon })
      .bindPopup(popupContent(callsign, lastSeen, lat, lon))
      .addTo(map.value)
    marker.on('click', () => onMarkerClick(callsign))
    markers.set(callsign, marker)
  }
}

function setTileProvider(key: string) {
  const provider = TILE_PROVIDERS[key]
  if (!provider || !map.value) return

  // If the provider requires an API key that isn't present, fall back to OSM
  if (provider.requiresApiKey && provider.apiKeyParam && !apiKeys.value[provider.apiKeyParam]) {
    providerFallbackMessage.value = `${provider.name} requires an API key — add it in Settings. Fallen back to OpenStreetMap.`
    providerFallbackSnackbar.value = true
    key = 'osm'
  }

  const resolvedProvider = TILE_PROVIDERS[key]!
  let url = resolvedProvider.url
  // Substitute API key placeholder if present
  if (resolvedProvider.requiresApiKey && resolvedProvider.apiKeyParam) {
    const apiKey = apiKeys.value[resolvedProvider.apiKeyParam] ?? ''
    url = url.replace('{apiKey}', apiKey)
  }

  if (tileLayer.value) {
    tileLayer.value.remove()
  }
  tileLayer.value = L.tileLayer(url, {
    attribution: resolvedProvider.attribution,
    maxZoom: 19,
  }).addTo(map.value)
  selectedProvider.value = key
  localStorage.setItem(STORAGE_KEY, key)

  if (showRings.value) {
    drawRings()
  }
}

async function loadStations() {
  try {
    const stations: StationDto[] = await getStations()
    for (const s of stations) {
      const prevStation = stationCache.get(s.callsign)
      stationCache.set(s.callsign, s)
      // If this station was previously stale, remove it from stale markers
      if (staleStationCache.has(s.callsign)) {
        removeStaleMarker(s.callsign)
        updateStaleStationsList()
      }
      if (s.lastLat != null && s.lastLon != null) {
        const existing = markers.get(s.callsign)
        if (existing) {
          existing.setLatLng([s.lastLat, s.lastLon])
          existing.setPopupContent(popupContent(s.callsign, s.lastSeen, s.lastLat, s.lastLon))
          // Only rebuild the icon DOM when shape-defining properties change;
          // for heading-only updates do an in-place transform so CSS transition fires.
          const iconShapeChanged =
            !prevStation ||
            prevStation.symbol !== s.symbol ||
            prevStation.isWeatherStation !== s.isWeatherStation ||
            prevStation.isOnWatchList !== s.isOnWatchList
          if (iconShapeChanged) {
            existing.setIcon(buildIcon(s))
          } else if (prevStation?.lastHeading !== s.lastHeading) {
            const el = existing.getElement()
            const wrapper = el?.querySelector('.aprs-heading-wrapper') as HTMLElement | null
            if (wrapper) {
              wrapper.style.transform = s.lastHeading != null ? `rotate(${s.lastHeading}deg)` : ''
            } else if (s.lastHeading != null) {
              // Heading introduced for the first time — need full icon rebuild
              existing.setIcon(buildIcon(s))
            }
          }
        } else {
          addOrUpdateMarker(s.callsign, s.lastLat, s.lastLon, s.lastSeen)
        }
      }
    }
    const activeCallsigns = new Set(stations.map(s => s.callsign))
    for (const [callsign, marker] of markers) {
      if (!activeCallsigns.has(callsign)) {
        marker.remove()
        markers.delete(callsign)
        stationCache.delete(callsign)
        removeTrack(callsign)
        removeGhostLayer(callsign)
        if (selectionStore.selectedCallsign === callsign) {
          selectionStore.deselect()
          clearPathLayer()
        }
      }
    }
    updateStationsList()
    await loadTracksForMobileStations()
    updateGhostLayers()
  } catch (err) {
    console.error('Failed to load stations:', err)
  }
}

async function connectSignalR() {
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/packets')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('packetReceived', (packet: PacketBroadcastDto) => {
    // Forward to beacon stream store
    beaconStore.addPacket(packet)

    // Increment session packet count
    sessionPacketCounts.value[packet.callsign] =
      (sessionPacketCounts.value[packet.callsign] ?? 0) + 1

    // Trigger detail panel refresh and redraw path for the selected station
    if (packet.callsign === selectionStore.selectedCallsign) {
      detailRefreshKey.value++
      showPacketPath(packet.callsign)
    }

    // Beacon flash ring on the marker (fires for any packet type, with/without position)
    triggerBeaconFlash(packet.callsign, packet.parsedType)

    // Reset stale decay class — this station is actively transmitting
    const markerEl = markers.get(packet.callsign)?.getElement()
    if (markerEl) {
      markerEl.classList.remove('stale-light', 'stale-medium', 'stale-heavy')
    }

    // Keep stationCache in sync for all packet types — ensures lastSeen and
    // position are always current so the station list stays reactive.
    const cachedStation = stationCache.get(packet.callsign)
    if (cachedStation) {
      stationCache.set(packet.callsign, {
        ...cachedStation,
        lastSeen: packet.receivedAt,
        ...(packet.latitude != null && packet.longitude != null
          ? { lastLat: packet.latitude, lastLon: packet.longitude }
          : {}),
      })
      updateStationsList()
    }

    if (packet.latitude != null && packet.longitude != null) {
      // A fresh real position supersedes the ghost
      removeGhostLayer(packet.callsign)

      const isNew = !markers.has(packet.callsign)
      addOrUpdateMarker(packet.callsign, packet.latitude, packet.longitude, packet.receivedAt)
      if (isNew) {
        // loadStations() will call updateStationsList() once the REST response
        // arrives and populates full station data for the new callsign.
        loadStations()
      } else {
        const station = stationCache.get(packet.callsign)
        if (showTracks.value && station && isMobileStation(station)) {
          fetchAndDrawTrack(packet.callsign)
        }
      }
    }
  })

  connection.on('stationsStale', (callsigns: string[]) => {
    for (const callsign of callsigns) {
      const marker = markers.get(callsign)
      if (marker) {
        marker.remove()
        markers.delete(callsign)
      }
      stationCache.delete(callsign)
      removeTrack(callsign)
      removeGhostLayer(callsign)
      if (selectionStore.selectedCallsign === callsign) {
        clearPathLayer()
      }
    }
    updateStationsList()
    // If show stale is on, refresh to include newly stale stations
    if (showStaleStations.value) {
      loadStaleStations()
    }
  })

  try {
    await connection.start()
    console.log('SignalR connected')
  } catch (err) {
    console.error('SignalR connection failed:', err)
  }
}

// Watch: ring distances — persist to localStorage and redraw if rings are showing
watch(ringDistances, (newDists) => {
  localStorage.setItem(RINGS_STORAGE_KEY, JSON.stringify(newDists))
  if (showRings.value) {
    drawRings()
  }
}, { deep: true })

// Watch: show rings toggle from the panel
watch(showRings, (enabled) => {
  if (enabled) {
    drawRings()
  } else {
    clearRings()
  }
})

// Watch: auto-switch tile to match light/dark theme when on cartoLight or cartoDark
watch(() => theme.global.current.value.dark, (dark) => {
  if (selectedProvider.value === 'cartoLight' || selectedProvider.value === 'cartoDark') {
    setTileProvider(dark ? 'cartoDark' : 'cartoLight')
  }
})

// Watch: when selectedCallsign changes (e.g. from BeaconStreamView navigation), open path + fly
watch(() => selectionStore.selectedCallsign, (callsign, prev) => {
  if (callsign && callsign !== prev) {
    showPacketPath(callsign)
    const s = stationCache.get(callsign) ?? staleStationCache.get(callsign)
    if (s?.lastLat != null && s?.lastLon != null) {
      map.value?.flyTo([s.lastLat, s.lastLon], Math.max(map.value?.getZoom() ?? 10, 12))
    }
    invalidateSizeAfterTransition()
  } else if (!callsign) {
    clearPathLayer()
    invalidateSizeAfterTransition()
  }
})

// Shortcut handlers dispatched by App.vue
function onShortcutEsc() {
  if (selectionStore.selectedCallsign) {
    onDetailClose()
  }
}

function onShortcutFocusSearch() {
  sidebarRef.value?.focusSearch()
}

function openPopOut() {
  window.open('/map-only', '_blank', 'width=1200,height=800,noopener')
}

function onResizeHandleDown(e: MouseEvent) {
  isResizing.value = true
  resizeStartX = e.clientX
  resizeStartWidth = panelWidth.value
  document.addEventListener('mousemove', onResizeMouseMove)
  document.addEventListener('mouseup', onResizeMouseUp)
  document.body.style.userSelect = 'none'
  document.body.style.cursor = 'ew-resize'
}

function onResizeMouseMove(e: MouseEvent) {
  if (!isResizing.value) return
  const delta = resizeStartX - e.clientX
  const maxWidth = Math.floor(window.innerWidth * PANEL_MAX_WIDTH_RATIO)
  panelWidth.value = Math.min(maxWidth, Math.max(PANEL_MIN_WIDTH, resizeStartWidth + delta))
}

function onResizeMouseUp() {
  isResizing.value = false
  document.removeEventListener('mousemove', onResizeMouseMove)
  document.removeEventListener('mouseup', onResizeMouseUp)
  document.body.style.userSelect = ''
  document.body.style.cursor = ''
  localStorage.setItem(PANEL_WIDTH_KEY, String(panelWidth.value))
}

onMounted(async () => {
  if (!mapContainer.value) return
  map.value = L.map(mapContainer.value, {
    center: DEFAULT_CENTER,
    zoom: DEFAULT_ZOOM,
    zoomControl: true,
  })
  setTileProvider(selectedProvider.value)
  await loadStations()
  await loadStaleStations()
  await connectSignalR()

  // Draw home station marker; show banner + start poll if position not known yet
  await drawHomeMarker()
  if (!settingsCache?.homePosition) {
    showNoHomePositionBanner.value = true
    homePositionPollInterval = setInterval(checkHomePosition, 60_000)
  }

  // Initial ghost render + periodic update every 30 s
  updateGhostLayers()
  ghostUpdateInterval = setInterval(updateGhostLayers, 30_000)

  // Initial stale decay pass + periodic update every 60 s
  updateStaleDecayClasses()
  staleDecayInterval = setInterval(updateStaleDecayClasses, 60_000)

  // Handle pending selection (e.g. navigation from BeaconStreamView)
  if (selectionStore.selectedCallsign) {
    const s = stationCache.get(selectionStore.selectedCallsign)
    if (s?.lastLat != null && s?.lastLon != null) {
      map.value?.flyTo([s.lastLat, s.lastLon], Math.max(map.value.getZoom(), 12))
    }
    await showPacketPath(selectionStore.selectedCallsign)
    invalidateSizeAfterTransition()
  }

  window.addEventListener('shortcut:esc', onShortcutEsc)
  window.addEventListener('shortcut:focus-search', onShortcutFocusSearch)
})

onUnmounted(async () => {
  if (connection) {
    await connection.stop()
    connection = null
  }
  if (ghostUpdateInterval) {
    clearInterval(ghostUpdateInterval)
    ghostUpdateInterval = null
  }
  if (staleDecayInterval) {
    clearInterval(staleDecayInterval)
    staleDecayInterval = null
  }
  for (const group of ghostLayers.values()) {
    group.remove()
  }
  ghostLayers.clear()
  for (const group of trackLayers.values()) {
    group.remove()
  }
  trackLayers.clear()
  for (const marker of staleMarkers.values()) {
    marker.remove()
  }
  staleMarkers.clear()
  staleStationCache.clear()
  clearOverlays()
  clearPathLayer()
  clearRings()
  clearHomeMarker()
  if (homePositionPollInterval) {
    clearInterval(homePositionPollInterval)
    homePositionPollInterval = null
  }
  clearHeatmap()
  clearCoverage()
  heatmapPositions = null
  coverageData = null
  if (highlightTimeout) clearTimeout(highlightTimeout)
  if (highlightMarker) highlightMarker.remove()
  if (map.value) {
    map.value.remove()
    map.value = undefined
  }
  markers.clear()
  window.removeEventListener('shortcut:esc', onShortcutEsc)
  window.removeEventListener('shortcut:focus-search', onShortcutFocusSearch)
  document.removeEventListener('mousemove', onResizeMouseMove)
  document.removeEventListener('mouseup', onResizeMouseUp)
})

defineExpose({ TILE_PROVIDERS })
</script>

<template>
  <div class="map-layout">
    <!-- Left sidebar -->
    <div class="sidebar-left" :class="{ 'sidebar-open': showSidebar }">
      <StationListSidebar
        ref="sidebarRef"
        :stations="stationsList"
        :packet-counts="sessionPacketCounts"
        :selected-callsign="selectionStore.selectedCallsign"
        :stale-stations="staleStationsList"
        :show-stale="showStaleStations"
        @select-station="onSidebarSelectStation"
        @update:show-stale="showStaleStations = $event; $event ? showCachedStaleMarkers() : hideStaleMarkers()"
      />
    </div>

    <!-- Map area -->
    <div class="map-wrapper">
      <div ref="mapContainer" class="map-container" />

      <!-- No home position banner -->
      <v-alert
        v-if="showNoHomePositionBanner"
        class="home-position-banner"
        type="info"
        density="compact"
        closable
        @click:close="showNoHomePositionBanner = false"
      >
        Home position not yet known — range rings will appear once your station is heard
      </v-alert>

      <TileProviderSwitcher
        :providers="TILE_PROVIDERS"
        :selected="selectedProvider"
        :api-keys="apiKeys"
        @update:selected="setTileProvider"
      />

      <!-- Sidebar toggle -->
      <v-btn
        class="sidebar-toggle-btn"
        :icon="showSidebar ? 'mdi-chevron-left' : 'mdi-chevron-right'"
        size="small"
        variant="elevated"
        color="surface"
        @click="toggleSidebar"
      />

      <!-- Track toggle -->
      <v-btn
        class="track-toggle-btn"
        :color="showTracks ? 'primary' : 'grey-darken-1'"
        size="small"
        variant="elevated"
        @click="toggleTracks"
      >
        <v-icon start>mdi-map-marker-path</v-icon>
        {{ showTracks ? 'Hide Tracks' : 'Show Tracks' }}
      </v-btn>

      <!-- Ghost marker toggle -->
      <v-btn
        class="ghost-toggle-btn"
        :color="showGhostMarkers ? 'indigo' : 'grey-darken-1'"
        size="small"
        variant="elevated"
        @click="toggleGhostMarkers"
      >
        <v-icon start>mdi-map-marker-question</v-icon>
        {{ showGhostMarkers ? 'Hide Est.' : 'Show Est.' }}
      </v-btn>

      <!-- Stale stations toggle -->
      <v-btn
        class="stale-toggle-btn"
        :color="showStaleStations ? 'brown-lighten-1' : 'grey-darken-1'"
        size="small"
        variant="elevated"
        @click="toggleStaleStations"
      >
        <v-icon start>mdi-clock-alert-outline</v-icon>
        {{ showStaleStations ? 'Hide Stale' : 'Show Stale' }}
      </v-btn>

      <!-- Overlays toggle (geofences + proximity rules) -->
      <v-btn
        class="overlays-toggle-btn"
        :color="showOverlays ? 'teal-darken-1' : 'grey-darken-1'"
        size="small"
        variant="elevated"
        @click="toggleOverlays"
      >
        <v-icon start>mdi-layers</v-icon>
        {{ showOverlays ? 'Hide Zones' : 'Show Zones' }}
      </v-btn>

      <!-- Heatmap toggle -->
      <v-btn
        class="heatmap-toggle-btn"
        :color="showHeatmap ? 'deep-orange-darken-1' : 'grey-darken-1'"
        :loading="heatmapLoading"
        size="small"
        variant="elevated"
        @click="toggleHeatmap"
      >
        <v-icon start>mdi-fire</v-icon>
        {{ showHeatmap ? 'Hide Heat' : 'Heatmap' }}
      </v-btn>

      <!-- Coverage map toggle -->
      <v-btn
        class="coverage-toggle-btn"
        :color="showCoverage ? 'green-darken-2' : 'grey-darken-1'"
        :loading="coverageLoading"
        size="small"
        variant="elevated"
        @click="toggleCoverage"
      >
        <v-icon start>mdi-grid</v-icon>
        {{ showCoverage ? 'Hide Grid' : 'Coverage' }}
      </v-btn>

      <!-- Pop-out button -->
      <v-btn
        class="popout-btn"
        color="grey-darken-1"
        size="small"
        variant="elevated"
        icon="mdi-open-in-new"
        :title="'Open map in new window'"
        @click="openPopOut"
      />

      <!-- Range rings panel -->
      <div class="range-rings-container">
        <RangeRingsPanel
          v-model:show-rings="showRings"
          v-model:distances="ringDistances"
        />
      </div>
    </div>

    <!-- Right detail panel -->
    <div
      class="panel-right"
      :class="{ 'panel-open': selectionStore.selectedCallsign, 'panel-resizing': isResizing }"
      :style="selectionStore.selectedCallsign ? { width: panelWidth + 'px' } : undefined"
    >
      <div class="panel-resize-handle" @mousedown.prevent="onResizeHandleDown" />
      <StationDetailPanel
        :callsign="selectionStore.selectedCallsign"
        :refresh-key="detailRefreshKey"
        @close="onDetailClose"
        @highlight-position="onHighlightPosition"
      />
    </div>
  </div>

  <!-- Tile provider fallback notification -->
  <v-snackbar
    v-model="providerFallbackSnackbar"
    :timeout="5000"
    color="warning"
    location="bottom"
  >
    {{ providerFallbackMessage }}
  </v-snackbar>
</template>

<style scoped>
.map-layout {
  display: flex;
  width: 100%;
  height: 100%;
  overflow: hidden;
}

/* Left sidebar */
.sidebar-left {
  width: 0;
  flex-shrink: 0;
  overflow: hidden;
  transition: width 0.3s ease;
}

.sidebar-left.sidebar-open {
  width: 280px;
}

/* Map wrapper */
.map-wrapper {
  flex: 1;
  position: relative;
  overflow: hidden;
}

.map-container {
  width: 100%;
  height: 100%;
}

/* Right panel */
.panel-right {
  width: 0;
  flex-shrink: 0;
  overflow: hidden;
  transition: width 0.3s ease;
  border-left: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  position: relative;
}

.panel-right.panel-open {
  min-width: 280px;
}

.panel-right.panel-resizing {
  transition: none;
}

.panel-resize-handle {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 6px;
  cursor: ew-resize;
  z-index: 10;
}

.panel-resize-handle:hover {
  background: rgba(var(--v-theme-primary), 0.18);
}

/* Map control button positions */
.sidebar-toggle-btn {
  position: absolute;
  top: 10px;
  left: 10px;
  z-index: 1000;
}

.track-toggle-btn {
  position: absolute;
  top: 10px;
  left: 60px;
  z-index: 1000;
}

.ghost-toggle-btn {
  position: absolute;
  top: 10px;
  left: 195px;
  z-index: 1000;
}

.stale-toggle-btn {
  position: absolute;
  top: 10px;
  left: 330px;
  z-index: 1000;
}

.overlays-toggle-btn {
  position: absolute;
  top: 10px;
  left: 465px;
  z-index: 1000;
}

.heatmap-toggle-btn {
  position: absolute;
  top: 10px;
  left: 600px;
  z-index: 1000;
}

.coverage-toggle-btn {
  position: absolute;
  top: 10px;
  left: 725px;
  z-index: 1000;
}

.popout-btn {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1000;
}

.range-rings-container {
  position: absolute;
  top: 50px;
  left: 10px;
  z-index: 1000;
}
</style>

<style>
.aprs-icon-container {
  background: transparent !important;
  border: none !important;
}

.ghost-label {
  background: rgba(20, 20, 30, 0.72) !important;
  border: 1px solid rgba(200, 200, 255, 0.25) !important;
  border-radius: 3px !important;
  color: #ccd !important;
  font-size: 10px !important;
  padding: 1px 5px !important;
  white-space: nowrap !important;
  box-shadow: none !important;
}

.ring-label {
  border: 1px solid rgba(128, 128, 128, 0.4);
  border-radius: 3px;
  font-size: 10px;
  font-weight: 600;
  padding: 1px 6px;
  white-space: nowrap;
  pointer-events: none;
  line-height: 1.4;
}

/* ── Heading wrapper & chevron ─────────────────────────────────────────────── */

.aprs-heading-wrapper {
  position: relative;
  width: 24px;
  height: 24px;
  transform-origin: 12px 12px;
  transition: transform 0.4s ease;
}

/* Up-pointing triangle; rotates with .aprs-heading-wrapper to face heading */
.aprs-chevron {
  position: absolute;
  top: -8px;
  left: 50%;
  margin-left: -5px;
  width: 0;
  height: 0;
  border-left: 5px solid transparent;
  border-right: 5px solid transparent;
  border-bottom: 7px solid rgba(255, 255, 255, 0.9);
  filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.7));
  pointer-events: none;
}

/* ── Weather dual-ring pulse ────────────────────────────────────────────────── */

.wx-ring {
  position: absolute;
  top: 50%;
  left: 50%;
  width: 36px;
  height: 36px;
  margin-top: -18px;
  margin-left: -18px;
  border-radius: 50%;
  border: 2px solid rgba(0, 188, 212, 0.85);
  pointer-events: none;
  z-index: -1;
}

.wx-ring-1 {
  animation: wx-pulse 3s ease-out infinite;
}

.wx-ring-2 {
  animation: wx-pulse 3s ease-out infinite 0.4s;
}

/* Small "W" callout badge in bottom-right corner of weather icons */
.wx-badge-w {
  position: absolute;
  bottom: -2px;
  right: -4px;
  font-size: 8px;
  font-weight: 700;
  line-height: 1;
  color: #00BCD4;
  text-shadow: 0 0 3px rgba(0, 0, 0, 0.9);
  pointer-events: none;
  z-index: 1;
}

@keyframes wx-pulse {
  0%   { transform: scale(0.6); opacity: 1; }
  70%  { transform: scale(1.6); opacity: 0; }
  100% { transform: scale(1.6); opacity: 0; }
}

/* ── Beacon flash rings ─────────────────────────────────────────────────────── */

.beacon-flash {
  position: absolute;
  top: 50%;
  left: 50%;
  width: 32px;
  height: 32px;
  margin-top: -16px;
  margin-left: -16px;
  border-radius: 50%;
  border-width: 2px;
  border-style: solid;
  pointer-events: none;
  animation: beacon-expand 0.8s ease-out forwards;
}

.beacon-flash-position  { border-color: rgba(33,  150, 243, 0.9); }
.beacon-flash-message   { border-color: rgba(76,  175,  80, 0.9); }
.beacon-flash-weather   { border-color: rgba(0,   150, 136, 0.9); }
.beacon-flash-telemetry { border-color: rgba(156,  39, 176, 0.9); }
.beacon-flash-unknown   { border-color: rgba(158, 158, 158, 0.9); }

@keyframes beacon-expand {
  0%   { transform: scale(0.5); opacity: 1; }
  100% { transform: scale(2.4); opacity: 0; }
}

/* ── Stale station visual decay ─────────────────────────────────────────────── */

.aprs-icon-container.stale-light .aprs-icon {
  filter: grayscale(20%);
}

.aprs-icon-container.stale-medium .aprs-icon {
  filter: grayscale(50%);
}

.aprs-icon-container.stale-heavy .aprs-icon {
  filter: grayscale(80%);
  opacity: 0.6;
}

/* ── Packet path unknown-hop label ──────────────────────────────────────────── */

.path-unknown-label {
  background: rgba(80, 80, 80, 0.85);
  color: #fff;
  border: none;
  border-radius: 3px;
  padding: 2px 6px;
  font-size: 11px;
  white-space: nowrap;
  pointer-events: none;
  box-shadow: 0 1px 4px rgba(0,0,0,0.4);
}

.path-unknown-label::before {
  display: none;
}

/* ── Home position banner ────────────────────────────────────────────────────── */

.home-position-banner {
  position: absolute;
  bottom: 32px;
  left: 50%;
  transform: translateX(-50%);
  width: max-content;
  max-width: 480px;
  z-index: 1000;
  pointer-events: auto;
}
</style>

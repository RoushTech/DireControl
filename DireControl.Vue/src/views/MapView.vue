<script setup lang="ts">
import { onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { useTheme, useDisplay } from 'vuetify'
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
import { getWeatherManifest, getWeatherStatus, type WeatherManifest } from '@/api/weatherApi'
import { StationType, type StationDto, type SettingsDto } from '@/types/station'
import type { PacketBroadcastDto, ResolvedPathEntry } from '@/types/packet'
import type { TileProviderConfig } from '@/types/map'
import { createAprsIcon, parseAprsSymbol } from '@/utils/aprsIcon'
import { estimatePosition } from '@/utils/estimatedPosition'
import { useUnits } from '@/composables/useUnits'
import { useMapPrefs } from '@/composables/useMapPrefs'
import TileProviderSwitcher from '@/components/TileProviderSwitcher.vue'
import StationDetailPanel from '@/components/StationDetailPanel.vue'
import StationListSidebar from '@/components/StationListSidebar.vue'
import RangeRingsPanel from '@/components/RangeRingsPanel.vue'
import OwnStationPanel from '@/components/OwnStationPanel.vue'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useRadiosStore } from '@/stores/radiosStore'
import type { OwnBeaconBroadcastDto, DigiConfirmationBroadcastDto } from '@/types/radio'
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
const radiosStore = useRadiosStore()
const theme = useTheme()
const { mobile } = useDisplay()
const { distanceUnit, formatDistance } = useUnits()
const {
  tracks: showTracks,
  estPos: showGhostMarkers,
  stale: showStaleStations,
  zones: showOverlays,
  heatmap: showHeatmap,
  coverage: showCoverage,
  rangeRings: showRings,
  ringPanelOpen,
  radar: showRadar,
  wind: showWind,
  lightning: showLightning,
  radarOpacity,
  windOpacity,
  lightningOpacity,
} = useMapPrefs()

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

// Mobile-specific sheet/menu state
const mobileLayerMenuOpen = ref(false)
const mobileStationSheetOpen = ref(false)

// Highlight marker for packet position
let highlightMarker: L.CircleMarker | null = null
let highlightTimeout: ReturnType<typeof setTimeout> | null = null

// Movement tracks state
const trackLayers = new Map<string, L.LayerGroup>()

// Packet path visualisation state
// Each entry holds the map layer group, an optional fade timer, and whether
// the path is "persistent" (user-selected) or "auto" (fades after 8 s).
type PathEntry = { group: L.LayerGroup; fadeTimer: ReturnType<typeof setTimeout> | null; persistent: boolean; resolvedPath: ResolvedPathEntry[] }
const activePaths = new Map<string, PathEntry>()

// Estimated position (ghost marker) state
const ghostLayers = new Map<string, L.LayerGroup>()
let ghostUpdateInterval: ReturnType<typeof setInterval> | null = null
let staleDecayInterval: ReturnType<typeof setInterval> | null = null

// Stale station state
const staleStationCache = new Map<string, StationDto>()
const staleMarkers = new Map<string, L.Marker>()
const staleStationsList = ref<StationDto[]>([])

// Geofence / proximity-rule circle overlays
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
const ringDistances = ref<number[]>(loadRingDistancesFromStorage())

// Migrate ring distances stored as miles (values ≤ 100 and no previously saved km values
// would have been in the range 5–100 miles). A pragmatic heuristic: if all values are
// integers ≤ 100 the storage was written before the km-based format. Convert once.
;(function migrateLegacyMilesRings() {
  const vals = ringDistances.value
  if (vals.every((v) => Number.isInteger(v) && v <= 100)) {
    ringDistances.value = vals.map((v) => Math.round(v * 1.609344))
    localStorage.setItem(RINGS_STORAGE_KEY, JSON.stringify(ringDistances.value))
  }
})()
let ringLayerGroup: L.LayerGroup | null = null

// Heatmap state
const heatmapLoading = ref(false)
let heatmapLayer: L.HeatLayer | null = null
let heatmapPositions: [number, number][] | null = null

// Coverage state
const coverageLoading = ref(false)
let coverageLayerGroup: L.LayerGroup | null = null
let coverageData: CoverageGridSquareDto[] | null = null

// Weather overlay state
interface WeatherLayerStatus {
  available: boolean
  frameCount?: number
  lastUpdated?: string
  reason?: string
}
interface WeatherStatus {
  radar: WeatherLayerStatus
  wind: WeatherLayerStatus
  lightning: WeatherLayerStatus
}
let radarManifest: WeatherManifest | null = null
const weatherStatus = shallowRef<WeatherStatus | null>(null)
let radarFrameLayers: L.TileLayer[] = []
let radarFrameMeta: { time: number }[] = []
let radarFrameReady: boolean[] = []
let currentRadarFrame = 0
let radarAnimTimeout: ReturnType<typeof setTimeout> | null = null
let radarRefreshInterval: ReturnType<typeof setInterval> | null = null
const radarPlaying = ref(false)
const radarTimestamp = ref('')
const radarFrameCount = ref(0)
const radarCurrentIdx = ref(0)
const radarLoading = ref(false)
const radarFrameInterval = ref(500)  // ms between frames
const radarControlsVisible = ref(false)
const windControlsVisible = ref(false)
const lightningControlsVisible = ref(false)
let radarControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let windControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let lightningControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let windLayer: L.TileLayer | null = null
let lightningLayer: L.TileLayer | null = null
let lightningRefreshInterval: ReturnType<typeof setInterval> | null = null

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
  marker.on('click', (e: L.LeafletMouseEvent) => {
    L.DomEvent.stopPropagation(e)
    onMarkerClick(station.callsign)
  })
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
        .bindTooltip(`Geofence: ${f.name}<br>${formatDistance(f.radiusMeters / 1000)}`, { sticky: true })
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
        .bindTooltip(`Proximity: ${r.name}${r.targetCallsign ? ` (${r.targetCallsign})` : ''}<br>${formatDistance(r.radiusMetres / 1000)}`, { sticky: true })
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

async function fetchWeatherStatus(): Promise<WeatherStatus | null> {
  try {
    weatherStatus.value = await getWeatherStatus()
    return weatherStatus.value
  } catch (err) {
    console.error('Failed to fetch weather status:', err)
    return null
  }
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
    const radiusMeters = dist * 1000
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
        html: `<div class="ring-label" style="color: ${style.labelColor}; background: ${style.labelBg}; border-color: ${style.color}40">${formatDistance(dist)}</div>`,
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

// --- Weather Overlays ---

function ensureWeatherPane() {
  if (!map.value) return
  if (!map.value.getPane('weatherPane')) {
    const pane = map.value.createPane('weatherPane')
    pane.style.zIndex = '450'  // above overlayPane (400), below markerPane (600)
    pane.style.pointerEvents = 'none'
  }
}

// ── RainViewer ──

async function fetchRadarManifest(): Promise<WeatherManifest | null> {
  try {
    return await getWeatherManifest()
  } catch (err) {
    console.error('Failed to fetch radar manifest:', err)
    return null
  }
}

function buildRadarLayer(framePath: string, manifest: WeatherManifest): L.TileLayer {
  const stripped = framePath.startsWith('/') ? framePath.slice(1) : framePath
  const zoomOffset = manifest.tileSize === 512 ? -1 : 0
  return L.tileLayer(
    `/api/weather/radar/tile/{z}/{x}/{y}/${stripped}`,
    { opacity: 0, tileSize: manifest.tileSize, zoomOffset, zIndex: 10, pane: 'weatherPane', maxNativeZoom: manifest.maxNativeZoom, maxZoom: 19 },
  )
}

function formatRadarTime(unixSeconds: number): string {
  return new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ' local'
}

function showRadarFrame(idx: number) {
  if (!map.value || radarFrameLayers.length === 0) return
  const clampedIdx = Math.max(0, Math.min(idx, radarFrameLayers.length - 1))
  radarFrameLayers.forEach((layer, i) => {
    if (i === clampedIdx) {
      if (!map.value!.hasLayer(layer)) layer.addTo(map.value!)
      layer.setOpacity(radarOpacity.value)
    } else if (map.value!.hasLayer(layer)) {
      // Fade to transparent rather than removing — prevents the blank-frame flicker
      // caused by Leaflet's per-tile CSS fade-in when re-adding a layer.
      layer.setOpacity(0)
    }
  })
  currentRadarFrame = clampedIdx
  radarCurrentIdx.value = clampedIdx
  if (radarFrameMeta[clampedIdx]) {
    radarTimestamp.value = formatRadarTime(radarFrameMeta[clampedIdx]!.time)
  }
}

async function playRadar() {
  if (radarPlaying.value) return
  radarPlaying.value = true
  keepRadarControlsVisible()

  const advance = async () => {
    if (!radarPlaying.value || radarFrameLayers.length === 0) return
    const next = (currentRadarFrame + 1) % radarFrameLayers.length
    const layer = radarFrameLayers[next]!
    // Add the next frame to the map invisibly so its tiles start loading
    if (!map.value!.hasLayer(layer)) {
      layer.addTo(map.value!)
      layer.setOpacity(0)
    }
    // Wait for all visible tiles on that frame to finish loading (up to 3 s)
    if (!radarFrameReady[next]) {
      await new Promise<void>(resolve => {
        const done = () => {
          radarFrameReady[next] = true
          clearTimeout(loadTimeout)
          // Wait for Leaflet's per-tile opacity fade-in (200 ms) to complete before
          // revealing the frame, otherwise tiles appear partially transparent on first play.
          setTimeout(resolve, 250)
        }
        layer.once('load', done)
        const loadTimeout = setTimeout(() => { layer.off('load', done); resolve() }, 3000)
      })
    }
    if (!radarPlaying.value) return
    showRadarFrame(next)
    radarAnimTimeout = setTimeout(advance, radarFrameInterval.value)
  }

  radarAnimTimeout = setTimeout(advance, radarFrameInterval.value)
}

function pauseRadar() {
  radarPlaying.value = false
  if (radarAnimTimeout) {
    clearTimeout(radarAnimTimeout)
    radarAnimTimeout = null
  }
}

function stepRadarFrame(delta: -1 | 1) {
  pauseRadar()
  const next = (currentRadarFrame + delta + radarFrameLayers.length) % radarFrameLayers.length
  showRadarFrame(next)
  keepRadarControlsVisible()
}

// ── Weather controls auto-hide ─────────────────────────────────────────────

function keepRadarControlsVisible() {
  if (!showRadar.value) return
  radarControlsVisible.value = true
  if (radarControlsHideTimer) clearTimeout(radarControlsHideTimer)
  radarControlsHideTimer = setTimeout(() => { radarControlsVisible.value = false }, 5000)
}

function keepWindControlsVisible() {
  if (!showWind.value) return
  windControlsVisible.value = true
  if (windControlsHideTimer) clearTimeout(windControlsHideTimer)
  windControlsHideTimer = setTimeout(() => { windControlsVisible.value = false }, 5000)
}

function keepLightningControlsVisible() {
  if (!showLightning.value) return
  lightningControlsVisible.value = true
  if (lightningControlsHideTimer) clearTimeout(lightningControlsHideTimer)
  lightningControlsHideTimer = setTimeout(() => { lightningControlsVisible.value = false }, 5000)
}

function clearRadarLayers() {
  pauseRadar()
  for (const layer of radarFrameLayers) {
    layer.remove()
  }
  radarFrameLayers = []
  radarFrameMeta = []
  radarFrameReady = []
  radarFrameCount.value = 0
  radarTimestamp.value = ''
  radarCurrentIdx.value = 0
}

async function enableRadar() {
  if (!map.value) return
  radarLoading.value = true
  ensureWeatherPane()
  try {
    radarManifest = await fetchRadarManifest()
    if (!radarManifest) { showRadar.value = false; return }
    const allFrames = [
      ...radarManifest.radar.past,
      ...(radarManifest.radar.nowcast ?? []),
    ]
    radarFrameMeta = allFrames.map(f => ({ time: f.time }))
    radarFrameReady = Array.from({ length: allFrames.length }, () => false)
    radarFrameLayers = allFrames.map(f => buildRadarLayer(f.path, radarManifest!))
    radarFrameCount.value = radarFrameLayers.length
    // Start on the last historical frame so we see the most recent real data first
    showRadarFrame(radarManifest.radar.past.length - 1)
    keepRadarControlsVisible()
    radarRefreshInterval = setInterval(async () => {
      if (!showRadar.value) return
      const wasPlaying = radarPlaying.value
      pauseRadar()
      clearRadarLayers()
      radarManifest = await fetchRadarManifest()
      if (!radarManifest) return
      const refreshedFrames = [
        ...radarManifest.radar.past,
        ...(radarManifest.radar.nowcast ?? []),
      ]
      radarFrameMeta = refreshedFrames.map(f => ({ time: f.time }))
      radarFrameReady = Array.from({ length: refreshedFrames.length }, () => false)
      radarFrameLayers = refreshedFrames.map(f => buildRadarLayer(f.path, radarManifest!))
      radarFrameCount.value = radarFrameLayers.length
      showRadarFrame(radarManifest.radar.past.length - 1)
      if (wasPlaying) playRadar()
    }, 5 * 60 * 1000)
  } finally {
    radarLoading.value = false
  }
}

function disableRadar() {
  pauseRadar()
  clearRadarLayers()
  if (radarRefreshInterval) {
    clearInterval(radarRefreshInterval)
    radarRefreshInterval = null
  }
  radarManifest = null
  radarControlsVisible.value = false
  if (radarControlsHideTimer) { clearTimeout(radarControlsHideTimer); radarControlsHideTimer = null }
}

async function toggleRadar() {
  showRadar.value = !showRadar.value
  if (showRadar.value) {
    await enableRadar()
  } else {
    disableRadar()
  }
}

// ── Wind (OpenWeatherMap) ──

function enableWind() {
  if (!map.value) return
  disableWind()
  ensureWeatherPane()
  windLayer = L.tileLayer(
    '/api/weather/wind/tile/{z}/{x}/{y}',
    { opacity: windOpacity.value, zIndex: 11, pane: 'weatherPane', maxNativeZoom: 18, maxZoom: 19 },
  ).addTo(map.value)
  keepWindControlsVisible()
}

function disableWind() {
  if (windLayer) {
    windLayer.remove()
    windLayer = null
  }
  windControlsVisible.value = false
  if (windControlsHideTimer) { clearTimeout(windControlsHideTimer); windControlsHideTimer = null }
}

async function toggleWind() {
  showWind.value = !showWind.value
  if (showWind.value && weatherStatus.value?.wind.available) {
    enableWind()
  } else {
    showWind.value = false
    disableWind()
  }
}

// ── Lightning (Tomorrow.io) ──

function buildLightningLayer(): L.TileLayer {
  return L.tileLayer(
    '/api/weather/lightning/tile/{z}/{x}/{y}',
    { opacity: lightningOpacity.value, zIndex: 12, pane: 'weatherPane', maxNativeZoom: 6, maxZoom: 19 },
  )
}

function enableLightning() {
  if (!map.value) return
  disableLightning()
  ensureWeatherPane()
  lightningLayer = buildLightningLayer().addTo(map.value)
  keepLightningControlsVisible()
  // Refresh every 5 minutes so Leaflet fetches fresh tiles from the backend cache
  lightningRefreshInterval = setInterval(() => {
    if (!showLightning.value || !map.value) return
    lightningLayer?.remove()
    lightningLayer = buildLightningLayer().addTo(map.value!)
  }, 5 * 60 * 1000)
}

function disableLightning() {
  if (lightningLayer) {
    lightningLayer.remove()
    lightningLayer = null
  }
  if (lightningRefreshInterval) {
    clearInterval(lightningRefreshInterval)
    lightningRefreshInterval = null
  }
  lightningControlsVisible.value = false
  if (lightningControlsHideTimer) { clearTimeout(lightningControlsHideTimer); lightningControlsHideTimer = null }
}

async function toggleLightning() {
  showLightning.value = !showLightning.value
  if (showLightning.value && weatherStatus.value?.lightning.available) {
    enableLightning()
  } else {
    showLightning.value = false
    disableLightning()
  }
}

// --- Sidebar & Panel ---

function toggleSidebar() {
  showSidebar.value = !showSidebar.value
  localStorage.setItem(SIDEBAR_KEY, String(showSidebar.value))
  invalidateSizeAfterTransition()
}

function onSidebarSelectStation(callsign: string) {
  console.log('[Select] onSidebarSelectStation for', callsign, '— stack:', new Error().stack)
  selectionStore.selectStation(callsign)
  const s = stationCache.get(callsign) ?? staleStationCache.get(callsign)
  if (s?.lastLat != null && s?.lastLon != null) {
    map.value?.flyTo([s.lastLat, s.lastLon], Math.max(map.value.getZoom(), 12))
  }
  // Do NOT call removePath(prev) or showPacketPath here — the selectedCallsign
  // watcher is the single caller for both, preventing duplicate draws.
}

function onDetailClose() {
  console.log('[Select] onDetailClose called — stack:', new Error().stack)
  const cs = selectionStore.selectedCallsign
  selectionStore.deselect()
  if (cs) removePath(cs)
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

function removePath(callsign: string) {
  console.log('[Path] removePath called for', callsign, '— stack:', new Error().stack)
  const entry = activePaths.get(callsign)
  if (!entry) {
    console.warn('[Path] No activePaths entry to remove for', callsign, '— map has', activePaths.size, 'entries:', [...activePaths.keys()])
    return
  }
  console.log('[Path] Removing path group for', callsign, '— persistent:', entry.persistent)
  if (entry.fadeTimer) clearTimeout(entry.fadeTimer)
  entry.group.remove()
  activePaths.delete(callsign)
}

function clearAllPaths() {
  for (const [, entry] of activePaths) {
    if (entry.fadeTimer) clearTimeout(entry.fadeTimer)
    entry.group.remove()
  }
  activePaths.clear()
}

function redrawAllPaths() {
  if (!map.value) return
  for (const [, entry] of activePaths) {
    entry.group.clearLayers()
    drawPathLayers(entry.group, entry.resolvedPath)
  }
}

function schedulePathFade(callsign: string) {
  const entry = activePaths.get(callsign)
  if (!entry || entry.persistent) return

  // Begin CSS opacity fade at 5 s; fully remove layers at 8 s (3 s transition).
  entry.fadeTimer = setTimeout(() => {
    const e = activePaths.get(callsign)
    if (!e) return
    e.group.eachLayer(layer => {
      if (layer instanceof L.Polyline) {
        const el = (layer as unknown as { _path?: SVGPathElement })._path
        if (el) {
          el.style.transition = 'opacity 3s ease-out'
          el.style.opacity = '0'
        }
      } else if (layer instanceof L.Marker) {
        const el = layer.getElement()
        if (el) {
          el.style.transition = 'opacity 3s ease-out'
          el.style.opacity = '0'
        }
      }
    })
    e.fadeTimer = setTimeout(() => removePath(callsign), 3000)
  }, 5000)
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

/** Populate a Leaflet LayerGroup with polylines and arrowheads for a resolved path. */
function drawPathLayers(group: L.LayerGroup, resolvedPath: ResolvedPathEntry[]) {
  const segments = buildPathSegments(resolvedPath)
  if (segments.length === 0) return false

  const totalHops = resolvedPath.length - 1  // excludes source entry (hopIndex 0)

  for (const seg of segments) {
    const isUnknown = seg.unknownsBetween.length > 0
    const color = hopSegmentColor(seg.hopIndexFrom, seg.isLastHop, isUnknown)
    const distKm = haversineKm(seg.fromLat, seg.fromLon, seg.toLat, seg.toLon)
    const distStr = formatDistance(distKm)

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

    addPathArrow(group, seg.fromLat, seg.fromLon, seg.toLat, seg.toLon, color)
  }

  return true
}

/**
 * Auto-draw a path from a SignalR broadcast packet.  Fades out after 8 s.
 * If the station is currently selected (persistent path), the auto-draw is
 * skipped — the persistent path already shows from the API fetch.
 */
function drawAutoPath(callsign: string, resolvedPath: ResolvedPathEntry[]) {
  if (!map.value || resolvedPath.length < 2) return
  if (callsign === selectionStore.selectedCallsign) return

  // Clear any existing auto-path for this callsign (reset timer on re-beacon)
  removePath(callsign)

  const group = L.layerGroup()
  if (!drawPathLayers(group, resolvedPath)) return

  group.addTo(map.value)
  const entry: PathEntry = { group, fadeTimer: null, persistent: false, resolvedPath }
  activePaths.set(callsign, entry)
  schedulePathFade(callsign)
}

/**
 * Fetch the most recent packet for a station and draw its path persistently.
 * Used when the user explicitly selects a station (click or sidebar).
 * The path stays until the station is deselected.
 */
async function showPacketPath(callsign: string) {
  console.log('[Path] showPacketPath called for', callsign, '— stack:', new Error().stack)
  if (!map.value) return
  // Clear any existing path (auto or persistent) for this callsign
  removePath(callsign)
  try {
    const { items } = await getStationPackets(callsign, 1, 1)
    // Guard: station may have been deselected while the fetch was in flight
    if (selectionStore.selectedCallsign !== callsign) {
      console.log('[Path] showPacketPath guard fired — callsign', callsign, 'no longer selected, aborting draw')
      return
    }
    if (items.length === 0) return
    const packet = items[0]!
    if (!packet.resolvedPath || packet.resolvedPath.length < 2) return

    const group = L.layerGroup()
    if (!drawPathLayers(group, packet.resolvedPath)) return

    group.addTo(map.value)
    console.log('[Path] Drew path for', callsign, '— map container:', map.value.getContainer().id, '— activePaths size before set:', activePaths.size)
    activePaths.set(callsign, { group, fadeTimer: null, persistent: true, resolvedPath: packet.resolvedPath })
    console.log('[Path] activePaths size after set:', activePaths.size)
  } catch (err) {
    console.error(`Failed to show packet path for ${callsign}:`, err)
  }
}

function onMarkerClick(callsign: string) {
  if (selectionStore.selectedCallsign === callsign) {
    console.log('[Select] onMarkerClick deselect for', callsign, '— stack:', new Error().stack)
    selectionStore.deselect()
    removePath(callsign)
    invalidateSizeAfterTransition()
  } else {
    console.log('[Select] onMarkerClick select for', callsign, '— stack:', new Error().stack)
    selectionStore.selectStation(callsign)
    // Do NOT call removePath(prev) or showPacketPath here — the selectedCallsign
    // watcher is the single caller for both, preventing duplicate draws.
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

function triggerHomeMarkerFlash() {
  const el = homeMarker?.getElement()
  if (!el) return
  const iconEl = el.querySelector('.aprs-icon')
  if (!iconEl) return
  const ring = document.createElement('div')
  ring.className = 'beacon-flash beacon-flash-ownbeacon'
  iconEl.appendChild(ring)
  ring.addEventListener('animationend', () => ring.remove(), { once: true })
}

function drawConfirmationLine(dto: DigiConfirmationBroadcastDto) {
  if (dto.lat == null || dto.lon == null) return
  const homePos = settingsCache?.homePosition
  if (!homePos) return
  if (!map.value) return

  const line = L.polyline(
    [[homePos.lat, homePos.lon], [dto.lat, dto.lon]],
    { color: '#FFD700', weight: 2, opacity: 1 },
  ).addTo(map.value)

  // Fade out starting at 5 s (3 s CSS transition), remove at 8 s
  setTimeout(() => {
    const el = (line as unknown as { _path?: SVGPathElement })._path
    if (el) {
      el.style.transition = 'opacity 3s ease-out'
      el.style.opacity = '0'
    }
    setTimeout(() => line.remove(), 3000)
  }, 5000)
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
    marker.on('click', (e: L.LeafletMouseEvent) => {
      L.DomEvent.stopPropagation(e)
      onMarkerClick(callsign)
    })
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
          removePath(callsign)
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

    // Trigger detail panel refresh for the selected station.
    // Re-fetch the persistent path from the API so it reflects the latest packet.
    if (packet.callsign === selectionStore.selectedCallsign) {
      detailRefreshKey.value++
      showPacketPath(packet.callsign)
    } else if (packet.resolvedPath && packet.resolvedPath.length >= 2) {
      // Auto-draw a fading path for non-selected stations.
      drawAutoPath(packet.callsign, packet.resolvedPath)
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
      removePath(callsign)
      if (selectionStore.selectedCallsign === callsign) {
        selectionStore.deselect()
      }
    }
    updateStationsList()
    // If show stale is on, refresh to include newly stale stations
    if (showStaleStations.value) {
      loadStaleStations()
    }
  })

  connection.on('ownBeaconReceived', (dto: OwnBeaconBroadcastDto) => {
    radiosStore.onOwnBeaconReceived(dto)
    triggerHomeMarkerFlash()
  })

  connection.on('digiConfirmation', (dto: DigiConfirmationBroadcastDto) => {
    radiosStore.onDigiConfirmation(dto)
    drawConfirmationLine(dto)
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

// Watch: distance unit — update ring labels, path tooltips, and overlay tooltips
watch(distanceUnit, () => {
  if (showRings.value) drawRings()
  redrawAllPaths()
  if (showOverlays.value) loadAndDrawOverlays()
})

// Watch: auto-switch tile to match light/dark theme when on cartoLight or cartoDark
watch(() => theme.global.current.value.dark, (dark) => {
  if (selectedProvider.value === 'cartoLight' || selectedProvider.value === 'cartoDark') {
    setTileProvider(dark ? 'cartoDark' : 'cartoLight')
  }
})

// Watch: weather overlay opacity — apply immediately to live layers
watch(radarOpacity, (v) => radarFrameLayers[currentRadarFrame]?.setOpacity(v))
watch(windOpacity, (v) => windLayer?.setOpacity(v))
watch(lightningOpacity, (v) => lightningLayer?.setOpacity(v))

// Watch: when selectedCallsign changes (e.g. from BeaconStreamView navigation), open path + fly
watch(() => selectionStore.selectedCallsign, (callsign, prev) => {
  console.log('[Select] selectedCallsign watch fired — callsign:', callsign, '| prev:', prev)
  if (callsign && callsign !== prev) {
    if (prev) removePath(prev)
    showPacketPath(callsign)
    const s = stationCache.get(callsign) ?? staleStationCache.get(callsign)
    if (s?.lastLat != null && s?.lastLon != null) {
      map.value?.flyTo([s.lastLat, s.lastLon], Math.max(map.value?.getZoom() ?? 10, 12))
    }
    invalidateSizeAfterTransition()
  } else if (!callsign) {
    if (prev) removePath(prev)
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
  map.value.on('click', () => {
    console.log('[Select] map background click — selectedCallsign:', selectionStore.selectedCallsign, '— stack:', new Error().stack)
    if (selectionStore.selectedCallsign) onDetailClose()
  })
  console.log('[Map] click listeners after attach:', (map.value as unknown as { _events?: { click?: unknown[] } })._events?.click?.length ?? 'unknown')
  await loadStations()
  await loadStaleStations()
  await connectSignalR()

  // Initialise radios — load list and last beacons, start the store's own SignalR connection.
  radiosStore.startSignalR()
  await radiosStore.fetchRadios()
  await radiosStore.fetchAllLastBeacons()

  // Draw home station marker; show banner + start poll if position not known yet
  await drawHomeMarker()
  if (settingsCache?.homePosition) {
    // Center map on home position at zoom 9 on every page load (issue #32 item 1)
    map.value.setView([settingsCache.homePosition.lat, settingsCache.homePosition.lon], 9)
  } else {
    showNoHomePositionBanner.value = true
    homePositionPollInterval = setInterval(checkHomePosition, 60_000)
  }

  // Initial ghost render + periodic update every 30 s
  updateGhostLayers()
  ghostUpdateInterval = setInterval(updateGhostLayers, 30_000)

  // Initial stale decay pass + periodic update every 60 s
  updateStaleDecayClasses()
  staleDecayInterval = setInterval(updateStaleDecayClasses, 60_000)

  // Restore persisted layer states (issue #32 item 2)
  if (showRings.value) await drawRings()
  if (showOverlays.value) await loadAndDrawOverlays()
  if (showHeatmap.value) {
    heatmapLoading.value = true
    try {
      if (!heatmapPositions) {
        const positions = await getPacketPositions()
        heatmapPositions = positions.map(p => [p.latitude, p.longitude] as [number, number])
      }
      if (map.value) {
        heatmapLayer = L.heatLayer(heatmapPositions, { radius: 18, blur: 15, maxZoom: 17, minOpacity: 0.3 }).addTo(map.value)
      }
    } catch {
      showHeatmap.value = false
    } finally {
      heatmapLoading.value = false
    }
  }
  if (showCoverage.value) {
    coverageLoading.value = true
    try {
      if (!coverageData) coverageData = await getCoverageGridSquares()
      drawCoverage()
    } catch {
      showCoverage.value = false
    } finally {
      coverageLoading.value = false
    }
  }

  // Restore persisted weather overlay states
  await fetchWeatherStatus()
  if (showRadar.value) await enableRadar()
  if (showWind.value && weatherStatus.value?.wind.available) enableWind()
  if (showLightning.value && weatherStatus.value?.lightning.available) enableLightning()

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
  clearAllPaths()
  clearRings()
  clearHomeMarker()
  if (homePositionPollInterval) {
    clearInterval(homePositionPollInterval)
    homePositionPollInterval = null
  }
  clearHeatmap()
  clearCoverage()
  disableRadar()
  disableWind()
  disableLightning()
  if (radarControlsHideTimer) clearTimeout(radarControlsHideTimer)
  if (windControlsHideTimer) clearTimeout(windControlsHideTimer)
  if (lightningControlsHideTimer) clearTimeout(lightningControlsHideTimer)
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
    <!-- Left sidebar (desktop only) -->
    <div v-if="!mobile" class="sidebar-left" :class="{ 'sidebar-open': showSidebar }">
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

      <!-- Own station beacon panel (bottom-left overlay) -->
      <OwnStationPanel />

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

      <!-- Sidebar toggle (desktop only) -->
      <v-btn
        v-if="!mobile"
        class="sidebar-toggle-btn"
        :icon="showSidebar ? 'mdi-chevron-left' : 'mdi-chevron-right'"
        size="small"
        variant="elevated"
        color="surface"
        @click="toggleSidebar"
      />

      <!-- Track toggle -->
      <v-btn
        v-if="!mobile"
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
        v-if="!mobile"
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
        v-if="!mobile"
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
        v-if="!mobile"
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
        v-if="!mobile"
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
        v-if="!mobile"
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

      <!-- Radar toggle -->
      <v-btn
        v-if="!mobile"
        class="radar-toggle-btn"
        :color="showRadar ? 'blue-darken-2' : 'grey-darken-1'"
        :loading="radarLoading"
        size="small"
        variant="elevated"
        @click="toggleRadar"
      >
        <v-icon start>mdi-weather-rainy</v-icon>
        {{ showRadar ? 'Hide Radar' : 'Radar' }}
      </v-btn>

      <!-- Wind toggle -->
      <v-tooltip
        v-if="!mobile"
        :disabled="weatherStatus?.wind.available ?? false"
        :text="weatherStatus?.wind.reason ?? 'OpenWeatherMap API key required — configure in Settings.'"
        location="bottom"
      >
        <template #activator="{ props: tp }">
          <v-btn
            v-bind="tp"
            class="wind-toggle-btn"
            :color="showWind ? 'cyan-darken-1' : 'grey-darken-1'"
            :disabled="!(weatherStatus?.wind.available ?? false)"
            size="small"
            variant="elevated"
            @click="toggleWind"
          >
            <v-icon start>mdi-weather-windy</v-icon>
            {{ showWind ? 'Hide Wind' : 'Wind' }}
          </v-btn>
        </template>
      </v-tooltip>

      <!-- Lightning toggle -->
      <v-tooltip
        v-if="!mobile"
        :disabled="weatherStatus?.lightning.available ?? false"
        :text="weatherStatus?.lightning.reason ?? 'Tomorrow.io API key required — configure in Settings.'"
        location="bottom"
      >
        <template #activator="{ props: tp }">
          <v-btn
            v-bind="tp"
            class="lightning-toggle-btn"
            :color="showLightning ? 'yellow-darken-2' : 'grey-darken-1'"
            :disabled="!(weatherStatus?.lightning.available ?? false)"
            size="small"
            variant="elevated"
            @click="toggleLightning"
          >
            <v-icon start>mdi-weather-lightning</v-icon>
            {{ showLightning ? 'Hide Lightning' : 'Lightning' }}
          </v-btn>
        </template>
      </v-tooltip>

      <!-- Radar animation bar (shown when radar is active and controls are visible, desktop only) -->
      <div
        v-if="showRadar && !mobile && radarFrameCount > 0 && radarControlsVisible"
        class="radar-animation-bar"
        @mouseenter="keepRadarControlsVisible"
      >
        <v-btn icon="mdi-skip-previous" size="x-small" variant="text" @click="stepRadarFrame(-1)" />
        <v-btn
          :icon="radarPlaying ? 'mdi-pause' : 'mdi-play'"
          size="x-small"
          variant="text"
          @click="radarPlaying ? pauseRadar() : playRadar()"
        />
        <v-btn icon="mdi-skip-next" size="x-small" variant="text" @click="stepRadarFrame(1)" />
        <span class="radar-timestamp">{{ radarTimestamp }}</span>
        <span class="radar-frame-dots">{{ radarCurrentIdx + 1 }}&nbsp;/&nbsp;{{ radarFrameCount }}</span>
        <span class="radar-bar-divider" />
        <v-select
          v-model="radarFrameInterval"
          :items="[
            { title: '¼×', value: 2000 },
            { title: '½×', value: 1000 },
            { title: '1×', value: 500 },
            { title: '2×', value: 250 },
            { title: '4×', value: 125 },
          ]"
          item-title="title"
          item-value="value"
          density="compact"
          hide-details
          class="radar-speed-select"
          @update:model-value="keepRadarControlsVisible"
        />
        <span class="radar-bar-divider" />
        <span class="radar-opacity-label">Opacity</span>
        <v-slider v-model="radarOpacity" class="radar-opacity-slider" min="0.1" max="1" step="0.05" density="compact" hide-details @update:model-value="keepRadarControlsVisible" />
        <span class="radar-opacity-pct">{{ Math.round(radarOpacity * 100) }}%</span>
      </div>

      <!-- Wind opacity row (shown when wind layer is active and controls visible, desktop only) -->
      <div
        v-if="showWind && !mobile && windControlsVisible"
        class="wind-opacity-row"
        @mouseenter="keepWindControlsVisible"
      >
        <span class="layer-opacity-label">Wind opacity</span>
        <v-slider v-model="windOpacity" class="layer-opacity-slider" min="0.1" max="1" step="0.05" density="compact" hide-details @update:model-value="keepWindControlsVisible" />
        <span class="layer-opacity-pct">{{ Math.round(windOpacity * 100) }}%</span>
      </div>

      <!-- Lightning opacity row (shown when lightning layer is active and controls visible, desktop only) -->
      <div
        v-if="showLightning && !mobile && lightningControlsVisible"
        class="lightning-opacity-row"
        @mouseenter="keepLightningControlsVisible"
      >
        <span class="layer-opacity-label">Lightning opacity</span>
        <v-slider v-model="lightningOpacity" class="layer-opacity-slider" min="0.1" max="1" step="0.05" density="compact" hide-details @update:model-value="keepLightningControlsVisible" />
        <span class="layer-opacity-pct">{{ Math.round(lightningOpacity * 100) }}%</span>
      </div>

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

      <!-- Mobile: layer menu button (⋮) -->
      <v-menu v-if="mobile" v-model="mobileLayerMenuOpen" location="bottom start" :close-on-content-click="false">
        <template #activator="{ props: menuProps }">
          <v-btn
            v-bind="menuProps"
            class="mobile-layer-btn"
            color="surface"
            size="small"
            variant="elevated"
            icon="mdi-layers"
            title="Layers"
          />
        </template>
        <v-list density="compact" min-width="220">
          <v-list-item @click="toggleTracks">
            <template #prepend><v-icon :color="showTracks ? 'primary' : 'grey'">mdi-map-marker-path</v-icon></template>
            <v-list-item-title>{{ showTracks ? 'Hide Tracks' : 'Show Tracks' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleGhostMarkers">
            <template #prepend><v-icon :color="showGhostMarkers ? 'indigo' : 'grey'">mdi-map-marker-question</v-icon></template>
            <v-list-item-title>{{ showGhostMarkers ? 'Hide Est. Positions' : 'Show Est. Positions' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleStaleStations">
            <template #prepend><v-icon :color="showStaleStations ? 'brown-lighten-1' : 'grey'">mdi-clock-alert-outline</v-icon></template>
            <v-list-item-title>{{ showStaleStations ? 'Hide Stale' : 'Show Stale' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleOverlays">
            <template #prepend><v-icon :color="showOverlays ? 'teal-darken-1' : 'grey'">mdi-layers</v-icon></template>
            <v-list-item-title>{{ showOverlays ? 'Hide Zones' : 'Show Zones' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleHeatmap">
            <template #prepend><v-icon :color="showHeatmap ? 'deep-orange-darken-1' : 'grey'">mdi-fire</v-icon></template>
            <v-list-item-title>{{ showHeatmap ? 'Hide Heatmap' : 'Heatmap' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleCoverage">
            <template #prepend><v-icon :color="showCoverage ? 'green-darken-2' : 'grey'">mdi-grid</v-icon></template>
            <v-list-item-title>{{ showCoverage ? 'Hide Coverage' : 'Coverage Grid' }}</v-list-item-title>
          </v-list-item>
          <v-divider class="my-1" />
          <v-list-item @click="showRings = !showRings">
            <template #prepend><v-icon :color="showRings ? 'blue-darken-1' : 'grey'">mdi-circle-double</v-icon></template>
            <v-list-item-title>{{ showRings ? 'Hide Range Rings' : 'Show Range Rings' }}</v-list-item-title>
          </v-list-item>
          <v-divider class="my-1" />
          <v-list-item @click="toggleRadar">
            <template #prepend><v-icon :color="showRadar ? 'blue-darken-2' : 'grey'">mdi-weather-rainy</v-icon></template>
            <v-list-item-title>{{ showRadar ? 'Hide Radar' : 'Radar' }}</v-list-item-title>
          </v-list-item>
          <v-list-item :disabled="!(weatherStatus?.wind.available ?? false)" @click="toggleWind">
            <template #prepend><v-icon :color="showWind ? 'cyan-darken-1' : 'grey'">mdi-weather-windy</v-icon></template>
            <v-list-item-title>{{ showWind ? 'Hide Wind' : 'Wind' }}</v-list-item-title>
          </v-list-item>
          <v-list-item :disabled="!(weatherStatus?.lightning.available ?? false)" @click="toggleLightning">
            <template #prepend><v-icon :color="showLightning ? 'yellow-darken-2' : 'grey'">mdi-weather-lightning</v-icon></template>
            <v-list-item-title>{{ showLightning ? 'Hide Lightning' : 'Lightning' }}</v-list-item-title>
          </v-list-item>
        </v-list>
      </v-menu>

      <!-- Mobile: station list sheet trigger -->
      <v-btn
        v-if="mobile"
        class="mobile-stations-btn"
        color="surface"
        size="small"
        variant="elevated"
        prepend-icon="mdi-format-list-bulleted"
        @click="mobileStationSheetOpen = true"
      >
        Stations
      </v-btn>

      <!-- Range rings panel (desktop only) -->
      <div v-if="!mobile" class="range-rings-container">
        <RangeRingsPanel
          v-model:show-rings="showRings"
          v-model:distances="ringDistances"
          v-model:expanded="ringPanelOpen"
        />
      </div>
    </div>

    <!-- Right detail panel (desktop only) -->
    <div
      v-if="!mobile"
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

  <!-- Mobile: station list bottom sheet -->
  <v-bottom-sheet v-if="mobile" v-model="mobileStationSheetOpen" max-height="70vh">
    <v-card>
      <v-card-title class="d-flex align-center">
        Stations
        <v-spacer />
        <v-btn icon="mdi-close" size="small" variant="text" @click="mobileStationSheetOpen = false" />
      </v-card-title>
      <v-divider />
      <div style="overflow-y: auto; max-height: calc(70vh - 60px)">
        <StationListSidebar
          :stations="stationsList"
          :packet-counts="sessionPacketCounts"
          :selected-callsign="selectionStore.selectedCallsign"
          :stale-stations="staleStationsList"
          :show-stale="showStaleStations"
          @select-station="(cs) => { onSidebarSelectStation(cs); mobileStationSheetOpen = false }"
          @update:show-stale="showStaleStations = $event; $event ? showCachedStaleMarkers() : hideStaleMarkers()"
        />
      </div>
    </v-card>
  </v-bottom-sheet>

  <!-- Mobile: station detail bottom sheet -->
  <v-bottom-sheet
    v-if="mobile"
    :model-value="!!selectionStore.selectedCallsign"
    max-height="65vh"
    @update:model-value="v => !v && onDetailClose()"
  >
    <v-card style="height: 65vh; display: flex; flex-direction: column;">
      <div style="overflow-y: auto; flex: 1;">
        <StationDetailPanel
          :callsign="selectionStore.selectedCallsign"
          :refresh-key="detailRefreshKey"
          @close="onDetailClose"
          @highlight-position="onHighlightPosition"
        />
      </div>
    </v-card>
  </v-bottom-sheet>

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
  min-height: 0;
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

.radar-toggle-btn {
  position: absolute;
  top: 10px;
  left: 850px;
  z-index: 1000;
}

.wind-toggle-btn {
  position: absolute;
  top: 10px;
  left: 975px;
  z-index: 1000;
}

.lightning-toggle-btn {
  position: absolute;
  top: 10px;
  left: 1100px;
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

/* Mobile controls */
.mobile-layer-btn {
  position: absolute;
  top: 10px;
  left: 10px;
  z-index: 1000;
}

.mobile-stations-btn {
  position: absolute;
  bottom: 16px;
  left: 50%;
  transform: translateX(-50%);
  z-index: 1000;
}

.radar-animation-bar {
  position: absolute;
  top: 50px;
  left: 850px;
  z-index: 1000;
  display: flex;
  align-items: center;
  gap: 2px;
  background: rgba(var(--v-theme-surface), 0.92);
  border-radius: 6px;
  padding: 2px 8px;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.3);
}

.radar-timestamp {
  font-size: 11px;
  white-space: nowrap;
  margin-left: 6px;
  opacity: 0.9;
}

.radar-frame-dots {
  font-size: 11px;
  white-space: nowrap;
  margin-left: 4px;
  opacity: 0.7;
}

.radar-bar-divider {
  display: inline-block;
  width: 1px;
  height: 16px;
  background: rgba(128, 128, 128, 0.4);
  margin: 0 6px;
  flex-shrink: 0;
}

.radar-opacity-label {
  font-size: 11px;
  white-space: nowrap;
  opacity: 0.8;
  flex-shrink: 0;
}

.radar-opacity-slider {
  width: 120px;
  flex-shrink: 0;
}

.radar-speed-select {
  width: 72px;
  flex-shrink: 0;
}

.radar-opacity-pct {
  font-size: 11px;
  white-space: nowrap;
  width: 30px;
  text-align: right;
  opacity: 0.8;
}

.wind-opacity-row,
.lightning-opacity-row {
  position: absolute;
  left: 850px;
  z-index: 1000;
  display: flex;
  align-items: center;
  gap: 6px;
  background: rgba(var(--v-theme-surface), 0.92);
  border-radius: 6px;
  padding: 2px 10px;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.3);
}

.wind-opacity-row {
  top: 88px;
}

.lightning-opacity-row {
  top: 126px;
}

.layer-opacity-label {
  font-size: 11px;
  white-space: nowrap;
  opacity: 0.8;
  flex-shrink: 0;
}

.layer-opacity-slider {
  width: 120px;
  flex-shrink: 0;
}

.layer-opacity-pct {
  font-size: 11px;
  white-space: nowrap;
  width: 30px;
  text-align: right;
  opacity: 0.8;
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
.beacon-flash-ownbeacon { border-color: rgba(255, 215,   0, 0.9); }

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

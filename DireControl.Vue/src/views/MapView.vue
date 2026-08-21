<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { useTheme, useDisplay } from 'vuetify'
import L from 'leaflet'
import 'leaflet.heat'
import { usePacketHubStore } from '@/stores/packetHub'
import {
  getStations,
  getStationTrack,
  getStationPackets,
  getSettings,
  updateLightningAlerts,
} from '@/api/stationsApi'
import { getGeofences, getProximityRules } from '@/api/alertsApi'
import {
  getCoverageGridSquares,
  getPacketPositions,
  type CoverageGridSquareDto,
} from '@/api/analysisApi'
import {
  getLightningHistory,
  getLightningStrikes,
  getWeatherManifest,
  getWeatherStatus,
  type LightningHistoryStrike,
  type LightningStrike,
  type WeatherManifest,
} from '@/api/weatherApi'
import { StationType, type StationDto, type SettingsDto } from '@/types/station'
import type { PacketBroadcastDto, ResolvedPathEntry, TrackPointDto } from '@/types/packet'
import type { TriggeringStrike } from '@/types/alert'
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
import { cardinal, formatStrikeAge, useLightningAlertsStore } from '@/stores/lightningAlertsStore'
import type { DigiConfirmationBroadcastDto } from '@/types/radio'

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
    attribution: 'Esri, HERE, Garmin, Intermap, &copy; OpenStreetMap contributors',
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
    attribution: '&copy; Esri &mdash; Source: Esri, Maxar, Earthstar Geographics',
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
const HOP_COLOR_FALLBACK = '#FF8C00' // dark orange for hop 3+
const UNKNOWN_SEGMENT_COLOR = '#999999' // grey for dashed unknown segments
const FINAL_HOP_COLOR = '#2ECC71' // green for the last hop to our station

const STORAGE_KEY = 'direcontrol-tile-provider'
const SIDEBAR_KEY = 'direcontrol-sidebar-open'
const PANEL_WIDTH_KEY = 'direcontrol-detail-panel-width'
const API_KEYS_STORAGE_KEY = 'direcontrol-api-keys'
const PANEL_MIN_WIDTH = 280
const PANEL_MAX_WIDTH_RATIO = 0.5
const DEFAULT_CENTER: [number, number] = [39.8283, -98.5795]
const DEFAULT_ZOOM = 5

const selectionStore = useStationSelectionStore()
const radiosStore = useRadiosStore()
const theme = useTheme()
const { mobile } = useDisplay()
const { distanceUnit, formatDistance } = useUnits()
const lightningAlertsStore = useLightningAlertsStore()
const {
  tracks: showTracks,
  trackMinutes,
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

// ─── Layer panel ──────────────────────────────────────────────────────────────
const LAYER_PANEL_COLLAPSED_KEY = 'mapPrefs.layerPanelCollapsed'
const layerPanelCollapsed = ref(localStorage.getItem(LAYER_PANEL_COLLAPSED_KEY) === 'true')
watch(layerPanelCollapsed, (v) => localStorage.setItem(LAYER_PANEL_COLLAPSED_KEY, String(v)))

/** Back to defaults: tracks + estimated positions on, everything else off. */
function resetLayers() {
  if (!showTracks.value) toggleTracks()
  if (!showGhostMarkers.value) toggleGhostMarkers()
  if (showStaleStations.value) toggleStaleStations()
  if (showOverlays.value) toggleOverlays()
  if (showHeatmap.value) void toggleHeatmap()
  if (showCoverage.value) void toggleCoverage()
  if (showRadar.value) void toggleRadar()
  if (showWind.value) void toggleWind()
  if (showLightning.value) void toggleLightning()
}

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
  } catch {
    /* ignore */
  }
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

// Station load failure — shown persistently so a dead backend doesn't just look
// like an empty map. Cleared on the next successful load.
const stationsLoadFailed = ref(false)

// Panel/sidebar state
const showSidebar = ref(localStorage.getItem(SIDEBAR_KEY) !== 'false')
const panelWidth = ref(
  Math.max(PANEL_MIN_WIDTH, parseInt(localStorage.getItem(PANEL_WIDTH_KEY) ?? '380', 10)),
)
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

// Movement tracks state. Points are cached per station so the track can be re-clipped
// to a shorter window (pref change, or simply the passage of time) without refetching.
const trackLayers = new Map<string, L.LayerGroup>()
const trackPoints = new Map<string, TrackPointDto[]>()
let trackPruneInterval: ReturnType<typeof setInterval> | null = null

// Packet path visualisation state
// Each entry holds the map layer group, an optional fade timer, and whether
// the path is "persistent" (user-selected) or "auto" (fades after 8 s).
type PathEntry = {
  group: L.LayerGroup
  fadeTimer: ReturnType<typeof setTimeout> | null
  persistent: boolean
  resolvedPath: ResolvedPathEntry[]
}
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
// Index of the frame shown while idle (the newest past frame); when the shown frame
// differs, lightning rendering follows the radar frame time instead of live data.
const radarRestingIdx = ref(0)
// Bumped whenever playback stops or layers are rebuilt so that an advance() continuation
// still awaiting a tile load can detect it is stale and die instead of double-advancing.
let radarAnimGeneration = 0
let radarAnimTimeout: ReturnType<typeof setTimeout> | null = null
let radarRefreshInterval: ReturnType<typeof setInterval> | null = null
const radarPlaying = ref(false)
const radarTimestamp = ref('')
const radarFrameCount = ref(0)
const radarCurrentIdx = ref(0)
const radarLoading = ref(false)
const radarFrameInterval = ref(500) // ms between frames
const radarControlsVisible = ref(false)
const windControlsVisible = ref(false)
const lightningControlsVisible = ref(false)
let radarControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let windControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let lightningControlsHideTimer: ReturnType<typeof setTimeout> | null = null
let windLayer: L.TileLayer | null = null
let lightningLayerGroup: L.LayerGroup | null = null
let lightningRenderer: L.Canvas | null = null
let lightningRefreshInterval: ReturnType<typeof setInterval> | null = null
let lightningStrikes: LightningStrike[] = []
let lightningFetchedAt = 0
let lightningHistory: LightningHistoryStrike[] = []
let lightningHistoryLoaded = false
let lightningHistoryLoading = false

const packetHub = usePacketHubStore()

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

function onToggleShowStale(value: boolean) {
  showStaleStations.value = value
  if (value) showCachedStaleMarkers()
  else hideStaleMarkers()
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
  const marker = L.marker([station.lastLat, station.lastLon], { icon, opacity: 0.5 }).bindPopup(
    `<strong>${station.callsign}</strong><br><em>Stale</em><br>Last seen: ${formatTime(station.lastSeen)}`,
  )
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
        .bindTooltip(`Geofence: ${f.name}<br>${formatDistance(f.radiusMeters / 1000)}`, {
          sticky: true,
        })
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
        .bindTooltip(
          `Proximity: ${r.name}${r.targetCallsign ? ` (${r.targetCallsign})` : ''}<br>${formatDistance(r.radiusMetres / 1000)}`,
          { sticky: true },
        )
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

/** Zoom the map opens at, and the tightest zoom "back to home" will leave you at. */
const HOME_ZOOM = 9

// Reactive mirror of the home position — settingsCache is a plain field, so the
// template can't gate the recenter control on it.
const homePosition = shallowRef<{ lat: number; lon: number } | null>(null)

/**
 * Returns to the home station after the map has been moved — by a lightning alert
 * auto-pan, a station selection, or plain panning. Never zooms in: a wide view stays
 * wide, a view tighter than the default is pulled back out to it.
 */
function centerOnHome() {
  const home = homePosition.value
  if (!map.value || !home) return
  map.value.flyTo([home.lat, home.lon], Math.min(map.value.getZoom(), HOME_ZOOM), {
    duration: 0.6,
  })
}

async function drawHomeMarker() {
  clearHomeMarker()
  if (!map.value) return
  const settings = await ensureSettings()
  homePosition.value = settings?.homePosition ?? null
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
    // Leader lines need the home position, so redraw any alerts that predate it
    renderAlertStrikes()
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

function getRingStyle(providerKey: string): {
  color: string
  weight: number
  labelColor: string
  labelBg: string
} {
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
      heatmapPositions = positions.map((p) => [p.latitude, p.longitude] as [number, number])
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
    pane.style.zIndex = '450' // above overlayPane (400), below markerPane (600)
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
  return L.tileLayer(`/api/weather/radar/tile/{z}/{x}/{y}/${stripped}`, {
    opacity: 0,
    tileSize: manifest.tileSize,
    zoomOffset,
    zIndex: 10,
    pane: 'weatherPane',
    maxNativeZoom: manifest.maxNativeZoom,
    maxZoom: 19,
  })
}

function formatRadarTime(unixSeconds: number): string {
  return (
    new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) +
    ' local'
  )
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
  refreshLightningView()
}

async function playRadar() {
  if (radarPlaying.value) return
  radarPlaying.value = true
  keepRadarControlsVisible()
  void ensureLightningHistory()
  const gen = ++radarAnimGeneration

  const advance = async () => {
    if (gen !== radarAnimGeneration || !radarPlaying.value || radarFrameLayers.length === 0) return
    const next = (currentRadarFrame + 1) % radarFrameLayers.length
    const layer = radarFrameLayers[next]!
    // Add the next frame to the map invisibly so its tiles start loading
    if (!map.value!.hasLayer(layer)) {
      layer.addTo(map.value!)
      layer.setOpacity(0)
    }
    // Wait for all visible tiles on that frame to finish loading (up to 3 s)
    if (!radarFrameReady[next]) {
      await new Promise<void>((resolve) => {
        const done = () => {
          radarFrameReady[next] = true
          clearTimeout(loadTimeout)
          resolve()
        }
        layer.once('load', done)
        const loadTimeout = setTimeout(() => {
          layer.off('load', done)
          resolve()
        }, 3000)
      })
    }
    // The await above can outlive a pause or a layer rebuild; a stale generation must
    // die here or two advance chains end up running (double speed, jumping frames).
    if (gen !== radarAnimGeneration || !radarPlaying.value) return
    showRadarFrame(next)
    // Dwell on the newest frame before wrapping so the loop restart reads as deliberate.
    const dwell = next === radarFrameLayers.length - 1 ? 3 : 1
    radarAnimTimeout = setTimeout(advance, radarFrameInterval.value * dwell)
  }

  radarAnimTimeout = setTimeout(advance, radarFrameInterval.value)
}

function pauseRadar() {
  radarPlaying.value = false
  radarAnimGeneration++
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

/**
 * Stops playback and snaps radar back to the newest observed frame, which also drops
 * lightning out of frame-synced playback and back onto live strikes.
 */
function showLatestWeather() {
  pauseRadar()
  if (radarFrameLayers.length > 0) showRadarFrame(radarRestingIdx.value)
  refreshLightningView()
  keepRadarControlsVisible()
  keepLightningControlsVisible()
}

/** True while radar/lightning are showing something other than the newest data. */
const showingHistoricalWeather = computed(
  () =>
    radarFrameCount.value > 0 &&
    (radarPlaying.value || radarCurrentIdx.value !== radarRestingIdx.value),
)

// ── Weather controls auto-hide ─────────────────────────────────────────────

function keepRadarControlsVisible() {
  if (!showRadar.value) return
  radarControlsVisible.value = true
  if (radarControlsHideTimer) clearTimeout(radarControlsHideTimer)
  radarControlsHideTimer = setTimeout(() => {
    radarControlsVisible.value = false
  }, 5000)
}

function keepWindControlsVisible() {
  if (!showWind.value) return
  windControlsVisible.value = true
  if (windControlsHideTimer) clearTimeout(windControlsHideTimer)
  windControlsHideTimer = setTimeout(() => {
    windControlsVisible.value = false
  }, 5000)
}

function keepLightningControlsVisible() {
  if (!showLightning.value) return
  lightningControlsVisible.value = true
  if (lightningControlsHideTimer) clearTimeout(lightningControlsHideTimer)
  lightningControlsHideTimer = setTimeout(() => {
    lightningControlsVisible.value = false
  }, 5000)
}

function clearRadarLayers() {
  pauseRadar()
  for (const layer of radarFrameLayers) {
    // Only our own readiness handlers. A bare off() also strips the internal listener
    // Leaflet adds on layer add to unsubscribe the layer from the map when it is removed,
    // so the discarded frame stays wired to the map's zoom event with a null _map and
    // throws on the next zoom — which aborts the rest of that event's listener chain and
    // leaves every layer registered after it (the new radar frames, the lightning alert
    // markers) stuck at its last screen position until the page is reloaded.
    layer.off('loading load')
    layer.remove()
  }
  radarFrameLayers = []
  radarFrameMeta = []
  radarFrameReady = []
  radarFrameCount.value = 0
  radarTimestamp.value = ''
  radarCurrentIdx.value = 0
}

function buildRadarFrames(manifest: WeatherManifest) {
  const allFrames = [...manifest.radar.past, ...(manifest.radar.nowcast ?? [])]
  radarFrameMeta = allFrames.map((f) => ({ time: f.time }))
  radarFrameReady = Array.from({ length: allFrames.length }, () => false)
  radarFrameLayers = allFrames.map((f) => buildRadarLayer(f.path, manifest))
  radarFrameLayers.forEach((layer, i) => {
    // A pan/zoom exposes tiles this frame has never loaded; drop its readiness so the
    // animation waits for them again instead of revealing blank sectors.
    layer.on('loading', () => {
      radarFrameReady[i] = false
    })
    layer.on('load', () => {
      radarFrameReady[i] = true
    })
  })
  radarFrameCount.value = radarFrameLayers.length
  radarRestingIdx.value = Math.max(0, manifest.radar.past.length - 1)
  // Frame times changed, so any radar-synced lightning history needs a refetch.
  lightningHistoryLoaded = false
}

async function refreshRadarFrames() {
  if (!showRadar.value) return
  const wasPlaying = radarPlaying.value
  const prevTime = radarFrameMeta[currentRadarFrame]?.time
  clearRadarLayers()
  radarManifest = await fetchRadarManifest()
  if (!radarManifest) return
  buildRadarFrames(radarManifest)
  // Resume at the frame closest in time to where the user was rather than snapping to
  // the newest frame mid-loop.
  let idx = radarRestingIdx.value
  if (wasPlaying && prevTime != null && radarFrameMeta.length > 0) {
    let best = 0
    for (let i = 1; i < radarFrameMeta.length; i++) {
      if (
        Math.abs(radarFrameMeta[i]!.time - prevTime) <
        Math.abs(radarFrameMeta[best]!.time - prevTime)
      )
        best = i
    }
    idx = best
  }
  showRadarFrame(idx)
  if (wasPlaying) void playRadar()
}

async function enableRadar() {
  if (!map.value) return
  radarLoading.value = true
  ensureWeatherPane()
  try {
    radarManifest = await fetchRadarManifest()
    if (!radarManifest) {
      showRadar.value = false
      return
    }
    buildRadarFrames(radarManifest)
    // Start on the last historical frame so we see the most recent real data first
    showRadarFrame(radarRestingIdx.value)
    keepRadarControlsVisible()
    radarRefreshInterval = setInterval(() => void refreshRadarFrames(), 5 * 60 * 1000)
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
  if (radarControlsHideTimer) {
    clearTimeout(radarControlsHideTimer)
    radarControlsHideTimer = null
  }
  // With the radar gone, lightning (if enabled) returns to live rendering.
  refreshLightningView()
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
  windLayer = L.tileLayer('/api/weather/wind/tile/{z}/{x}/{y}', {
    opacity: windOpacity.value,
    zIndex: 11,
    pane: 'weatherPane',
    maxNativeZoom: 18,
    maxZoom: 19,
  }).addTo(map.value)
  keepWindControlsVisible()
}

function disableWind() {
  if (windLayer) {
    windLayer.remove()
    windLayer = null
  }
  windControlsVisible.value = false
  if (windControlsHideTimer) {
    clearTimeout(windControlsHideTimer)
    windControlsHideTimer = null
  }
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

// ── Lightning (Blitzortung.org) ──

const LIGHTNING_POLL_MS = 15_000
const LIGHTNING_MAX_AGE_S = 60 * 60

function lightningStrikeStyle(
  ageSeconds: number,
  maxAgeS = LIGHTNING_MAX_AGE_S,
): {
  color: string
  opacity: number
  radius: number
} {
  const f = Math.min(Math.max(ageSeconds / maxAgeS, 0), 1)
  // Newest strikes bright yellow-white fading toward dim red as they age out.
  return {
    color: `hsl(${55 * (1 - f)}, 100%, ${75 - 30 * f}%)`,
    opacity: (1 - 0.75 * f) * lightningOpacity.value,
    radius: f < 1 / 6 ? 4 : 3,
  }
}

async function refreshLightningStrikes() {
  if (!map.value || !showLightning.value) return
  const b = map.value.getBounds()
  try {
    const res = await getLightningStrikes({
      minLat: b.getSouth(),
      maxLat: b.getNorth(),
      minLon: b.getWest(),
      maxLon: b.getEast(),
    })
    lightningStrikes = res.strikes
    lightningFetchedAt = Date.now()
    if (!lightningFollowsRadar()) renderLightningStrikes()
  } catch {
    // keep last-known strikes; next poll retries
  }
}

function addLightningMarker(
  latitude: number,
  longitude: number,
  ageSeconds: number,
  maxAgeS = LIGHTNING_MAX_AGE_S,
) {
  const style = lightningStrikeStyle(ageSeconds, maxAgeS)
  lightningLayerGroup!.addLayer(
    L.circleMarker([latitude, longitude], {
      renderer: lightningRenderer!,
      pane: 'weatherPane',
      radius: style.radius,
      stroke: false,
      fillColor: style.color,
      fillOpacity: style.opacity,
      interactive: false,
    }),
  )
}

function renderLightningStrikes() {
  if (!map.value || !lightningLayerGroup || !lightningRenderer) return
  const extraAgeS = (Date.now() - lightningFetchedAt) / 1000
  lightningLayerGroup.clearLayers()
  for (const s of lightningStrikes) {
    addLightningMarker(s.latitude, s.longitude, s.ageSeconds + extraAgeS)
  }
}

// ── Lightning ↔ radar playback sync ──
// While the radar shows a historical frame, lightning is rendered from persisted
// history at that frame's time: only the strikes from that frame's own slice, fading
// out through the following frame so fast playback doesn't strobe.

function lightningFollowsRadar(): boolean {
  return (
    showLightning.value &&
    radarFrameMeta.length > 0 &&
    (radarPlaying.value || radarCurrentIdx.value !== radarRestingIdx.value)
  )
}

/** Seconds between radar frames (5 min for IEM, 10 min for RainViewer). */
function radarFrameStepS(): number {
  if (radarFrameMeta.length < 2) return 300
  return Math.max(60, radarFrameMeta[1]!.time - radarFrameMeta[0]!.time)
}

/** Strikes stay visible for their own frame slice plus one more frame of fade-out. */
function playbackLightningWindowS(): number {
  return radarFrameStepS() * 2
}

function renderLightningAtTime(frameTimeSeconds: number | undefined) {
  if (frameTimeSeconds == null || !map.value || !lightningLayerGroup || !lightningRenderer) return
  const maxAgeS = playbackLightningWindowS()
  lightningLayerGroup.clearLayers()
  for (const s of lightningHistory) {
    const age = frameTimeSeconds - s.timeSeconds
    if (age < 0 || age > maxAgeS) continue
    addLightningMarker(s.latitude, s.longitude, age, maxAgeS)
  }
}

async function ensureLightningHistory() {
  if (!map.value || !showLightning.value || radarFrameMeta.length === 0) return
  if (lightningHistoryLoaded || lightningHistoryLoading) return
  lightningHistoryLoading = true
  try {
    const b = map.value.getBounds()
    const times = radarFrameMeta.map((m) => m.time)
    const res = await getLightningHistory({
      minLat: b.getSouth(),
      maxLat: b.getNorth(),
      minLon: b.getWest(),
      maxLon: b.getEast(),
      fromSeconds: Math.min(...times) - playbackLightningWindowS(),
      toSeconds: Math.max(...times),
    })
    lightningHistory = res.strikes
    lightningHistoryLoaded = true
    refreshLightningView()
  } catch {
    // history stays unloaded; playback falls back to live strikes on the next attempt
  } finally {
    lightningHistoryLoading = false
  }
}

/** Re-renders lightning in whichever mode currently applies (frame-synced or live). */
function refreshLightningView() {
  if (!showLightning.value || !lightningLayerGroup) return
  if (lightningFollowsRadar()) {
    if (!lightningHistoryLoaded) {
      void ensureLightningHistory()
      return
    }
    renderLightningAtTime(radarFrameMeta[currentRadarFrame]?.time)
  } else {
    renderLightningStrikes()
  }
}

function onLightningMoveEnd() {
  void refreshLightningStrikes()
  // New viewport needs a new history window for radar-synced playback.
  lightningHistoryLoaded = false
  if (lightningFollowsRadar()) void ensureLightningHistory()
}

function enableLightning() {
  if (!map.value) return
  disableLightning()
  ensureWeatherPane()
  lightningRenderer = L.canvas({ pane: 'weatherPane' })
  lightningLayerGroup = L.layerGroup().addTo(map.value)
  keepLightningControlsVisible()
  void refreshLightningStrikes()
  if (lightningFollowsRadar()) void ensureLightningHistory()
  lightningRefreshInterval = setInterval(() => void refreshLightningStrikes(), LIGHTNING_POLL_MS)
  map.value.on('moveend', onLightningMoveEnd)
}

function disableLightning() {
  map.value?.off('moveend', onLightningMoveEnd)
  if (lightningLayerGroup) {
    lightningLayerGroup.remove()
    lightningLayerGroup = null
  }
  lightningRenderer = null
  lightningStrikes = []
  lightningHistory = []
  lightningHistoryLoaded = false
  if (lightningRefreshInterval) {
    clearInterval(lightningRefreshInterval)
    lightningRefreshInterval = null
  }
  lightningControlsVisible.value = false
  if (lightningControlsHideTimer) {
    clearTimeout(lightningControlsHideTimer)
    lightningControlsHideTimer = null
  }
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

// ── Triggering strikes (proximity alerts) ─────────────────────────────────────
// Rendered in their own pane and layer group so they are never touched by the
// live/playback rendering of the lightning layer: an alert marker stays put while
// the radar is scrubbed through time, and shows even with the layer switched off.

const triggeringStrikes = computed(() => lightningAlertsStore.triggeringStrikes)
/** Newest first — the order the alert stack is listed in. */
const alertStack = computed(() => [...triggeringStrikes.value].reverse())

let alertStrikeLayer: L.LayerGroup | null = null
const alertStrikeMarkers = new Map<string, L.Marker>()

// Auto-pan belongs to the alert, not the map, so it lives with the other lightning
// alert settings on the server and follows the operator between browsers.
const lightningAutoPan = ref(false)
const autoPanSaving = ref(false)

async function loadLightningAlertSettings() {
  const settings = await ensureSettings()
  lightningAutoPan.value = settings?.lightningAlertAutoPan ?? false
}

/** Write-through toggle so the setting can be flipped mid-storm without leaving the map. */
async function toggleAutoPan() {
  const settings = await ensureSettings()
  if (!settings || autoPanSaving.value) return
  const next = !lightningAutoPan.value
  autoPanSaving.value = true
  try {
    await updateLightningAlerts(
      settings.lightningAlertEnabled,
      settings.lightningAlertRadiusKm,
      settings.lightningAlertCooldownMinutes,
      next,
    )
    settings.lightningAlertAutoPan = next
    lightningAutoPan.value = next
  } catch (err) {
    console.error('Failed to save lightning auto-pan setting:', err)
  } finally {
    autoPanSaving.value = false
  }
}

function ensureAlertPane() {
  if (!map.value) return
  if (!map.value.getPane('lightningAlertPane')) {
    const pane = map.value.createPane('lightningAlertPane')
    pane.style.zIndex = '620' // above markerPane (600), below popupPane (700)
  }
}

function alertStrikeTitle(strike: TriggeringStrike): string {
  return `${formatDistance(strike.distanceKm)} ${cardinal(strike.bearingDegrees)}`
}

function renderAlertStrikes() {
  if (!map.value) return
  ensureAlertPane()
  if (!alertStrikeLayer) alertStrikeLayer = L.layerGroup().addTo(map.value)
  alertStrikeLayer.clearLayers()
  alertStrikeMarkers.clear()

  const strikes = triggeringStrikes.value
  const homePos = settingsCache?.homePosition
  strikes.forEach((strike, i) => {
    const isNewest = i === strikes.length - 1
    const fade = isNewest ? 1 : 0.45

    // Leader line from the home station makes "this is the strike that alerted" unambiguous.
    if (homePos) {
      alertStrikeLayer!.addLayer(
        L.polyline(
          [
            [homePos.lat, homePos.lon],
            [strike.latitude, strike.longitude],
          ],
          {
            pane: 'lightningAlertPane',
            color: '#FF3D00',
            weight: 2,
            opacity: 0.75 * fade,
            dashArray: '6 6',
            interactive: false,
          },
        ),
      )
    }

    const marker = L.marker([strike.latitude, strike.longitude], {
      pane: 'lightningAlertPane',
      icon: L.divIcon({
        className: '',
        html: `<div class="strike-alert-marker${isNewest ? ' strike-alert-marker-newest' : ''}">
                 <span class="strike-alert-pulse"></span>
                 <span class="strike-alert-bolt">⚡</span>
               </div>`,
        iconSize: [34, 34],
        iconAnchor: [17, 17],
      }),
      zIndexOffset: 1000,
    })
    marker.bindPopup(
      `<strong>Lightning alert</strong><br>${alertStrikeTitle(strike)} of station` +
        `<br>Struck: ${formatTime(strike.strikeTimeUtc)}` +
        `<br>Alerted: ${formatStrikeAge(strike.feedLagSeconds)} (feed lag)` +
        `<br>Alert radius: ${formatDistance(strike.radiusKm)}`,
    )
    if (isNewest) {
      marker.bindTooltip(alertStrikeTitle(strike), {
        permanent: true,
        direction: 'top',
        offset: [0, -16],
        className: 'strike-alert-label',
      })
    }
    alertStrikeLayer!.addLayer(marker)
    alertStrikeMarkers.set(strike.key, marker)
  })
}

/** Centres the map on a triggering strike and opens its popup. */
function focusAlertStrike(strike: TriggeringStrike) {
  if (!map.value) return
  map.value.flyTo([strike.latitude, strike.longitude], Math.max(map.value.getZoom(), 9), {
    duration: 0.6,
  })
  alertStrikeMarkers.get(strike.key)?.openPopup()
}

function dismissAlertStrike(strike: TriggeringStrike) {
  lightningAlertsStore.dismissTrigger(strike.key)
}

function clearAlertStrikes() {
  lightningAlertsStore.clearTriggers()
}

watch(
  triggeringStrikes,
  (strikes, prev) => {
    renderAlertStrikes()
    const newest = strikes[strikes.length - 1]
    if (!newest || !lightningAutoPan.value) return
    if (prev?.some((s) => s.key === newest.key)) return
    focusAlertStrike(newest)
  },
  { deep: true },
)

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
  // Do NOT call removePath(prev) or showPacketPath here — the selectedCallsign
  // watcher is the single caller for both, preventing duplicate draws.
}

function onDetailClose() {
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

/** Selectable track windows, in minutes. */
const TRACK_WINDOW_OPTIONS = [
  { title: '5 min', value: 5 },
  { title: '15 min', value: 15 },
  { title: '30 min', value: 30 },
  { title: '1 hr', value: 60 },
  { title: '2 hr', value: 120 },
  { title: '6 hr', value: 360 },
  { title: '12 hr', value: 720 },
  { title: '24 hr', value: 1440 },
]

async function fetchAndDrawTrack(callsign: string) {
  if (!map.value) return
  try {
    trackPoints.set(callsign, await getStationTrack(callsign, trackMinutes.value))
    drawTrack(callsign)
  } catch (err) {
    console.error(`Failed to fetch track for ${callsign}:`, err)
  }
}

/** Redraws a cached track, clipped to the configured window. */
function drawTrack(callsign: string) {
  if (!map.value) return
  removeTrack(callsign, { keepPoints: true })
  const cutoff = Date.now() - trackMinutes.value * 60_000
  const points = (trackPoints.get(callsign) ?? []).filter(
    (p) => new Date(p.receivedAt).getTime() >= cutoff,
  )
  if (points.length < 2) return

  const group = L.layerGroup()
  const totalPoints = points.length
  for (let i = 0; i < totalPoints - 1; i++) {
    const from = points[i]!
    const to = points[i + 1]!
    const opacity = 0.2 + 0.8 * (i / (totalPoints - 1))
    const weight = 2 + Math.round(2 * (i / (totalPoints - 1)))
    const segment = L.polyline(
      [
        [from.latitude, from.longitude],
        [to.latitude, to.longitude],
      ],
      { color: '#1976D2', weight, opacity, lineCap: 'round', lineJoin: 'round' },
    )
    group.addLayer(segment)
  }
  for (let i = 0; i < totalPoints; i++) {
    const pt = points[i]!
    const opacity = 0.3 + 0.7 * (i / Math.max(totalPoints - 1, 1))
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
}

/** Re-clips every cached track so tails expire on their own, without new beacons. */
function pruneTracks() {
  if (!showTracks.value) return
  for (const callsign of trackPoints.keys()) {
    drawTrack(callsign)
  }
}

/** Refetches every drawn track — used when the window grows past what is cached. */
async function reloadTracks() {
  if (!showTracks.value) return
  // Snapshot the keys — fetchAndDrawTrack writes back into the same map.
  const callsigns = [...trackPoints.keys()]
  for (const callsign of callsigns) {
    await fetchAndDrawTrack(callsign)
  }
}

function removeTrack(callsign: string, opts?: { keepPoints?: boolean }) {
  const existing = trackLayers.get(callsign)
  if (existing) {
    existing.remove()
    trackLayers.delete(callsign)
  }
  if (!opts?.keepPoints) trackPoints.delete(callsign)
}

function toggleTracks() {
  showTracks.value = !showTracks.value
  if (!map.value) return
  if (showTracks.value) {
    for (const [callsign, station] of stationCache) {
      if (isMobileStation(station) && station.lastLat != null && station.lastLon != null) {
        if (trackPoints.has(callsign)) {
          drawTrack(callsign)
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
      if (!trackPoints.has(callsign)) {
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
      [
        [station.lastLat, station.lastLon],
        [est.lat, est.lon],
      ],
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
  const entry = activePaths.get(callsign)
  if (!entry) {
    return
  }
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
    e.group.eachLayer((layer) => {
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
  const dLat = ((lat2 - lat1) * Math.PI) / 180
  const dLon = ((lon2 - lon1) * Math.PI) / 180
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos((lat1 * Math.PI) / 180) * Math.cos((lat2 * Math.PI) / 180) * Math.sin(dLon / 2) ** 2
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function bearingDeg(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const dLon = ((lon2 - lon1) * Math.PI) / 180
  const la1 = (lat1 * Math.PI) / 180
  const la2 = (lat2 * Math.PI) / 180
  const y = Math.sin(dLon) * Math.cos(la2)
  const x = Math.cos(la1) * Math.sin(la2) - Math.sin(la1) * Math.cos(la2) * Math.cos(dLon)
  return ((Math.atan2(y, x) * 180) / Math.PI + 360) % 360
}

function addPathArrow(
  group: L.LayerGroup,
  fromLat: number,
  fromLon: number,
  toLat: number,
  toLon: number,
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
  group.addLayer(
    L.marker([midLat, midLon], { icon: arrowIcon, interactive: false, zIndexOffset: -50 }),
  )
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
      const unknownsBetween = path.slice(lastKnownIdx + 1, i).filter((e) => !(e.known ?? false))
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
  const allGeneric = unknowns.every((e) => isGenericAlias(e.callsign))
  if (allGeneric) {
    const aliases = [...new Set(unknowns.map((e) => e.callsign))].join(', ')
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

  const totalHops = resolvedPath.length - 1 // excludes source entry (hopIndex 0)

  for (const seg of segments) {
    const isUnknown = seg.unknownsBetween.length > 0
    const color = hopSegmentColor(seg.hopIndexFrom, seg.isLastHop, isUnknown)
    const distKm = haversineKm(seg.fromLat, seg.fromLon, seg.toLat, seg.toLon)
    const distStr = formatDistance(distKm)

    const line = L.polyline(
      [
        [seg.fromLat, seg.fromLon],
        [seg.toLat, seg.toLon],
      ],
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
      midMarker.bindTooltip(label, {
        permanent: true,
        className: 'path-unknown-label',
        direction: 'top',
      })
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
  if (!map.value) return
  // Clear any existing path (auto or persistent) for this callsign
  removePath(callsign)
  try {
    const { items } = await getStationPackets(callsign, 1, 1)
    // Guard: station may have been deselected while the fetch was in flight
    if (selectionStore.selectedCallsign !== callsign) {
      return
    }
    if (items.length === 0) return
    const packet = items[0]!
    if (!packet.resolvedPath || packet.resolvedPath.length < 2) return

    const group = L.layerGroup()
    if (!drawPathLayers(group, packet.resolvedPath)) return

    group.addTo(map.value)
    activePaths.set(callsign, {
      group,
      fadeTimer: null,
      persistent: true,
      resolvedPath: packet.resolvedPath,
    })
  } catch (err) {
    console.error(`Failed to show packet path for ${callsign}:`, err)
  }
}

function onMarkerClick(callsign: string) {
  if (selectionStore.selectedCallsign === callsign) {
    selectionStore.deselect()
    removePath(callsign)
    invalidateSizeAfterTransition()
  } else {
    selectionStore.selectStation(callsign)
    // Do NOT call removePath(prev) or showPacketPath here — the selectedCallsign
    // watcher is the single caller for both, preventing duplicate draws.
    invalidateSizeAfterTransition()
  }
}

// --- Beacon Flash & Stale Decay ---

function packetTypeFlashClass(parsedType: string): string {
  switch (parsedType) {
    case 'Position':
      return 'beacon-flash-position'
    case 'Message':
      return 'beacon-flash-message'
    case 'Weather':
      return 'beacon-flash-weather'
    case 'Telemetry':
      return 'beacon-flash-telemetry'
    default:
      return 'beacon-flash-unknown'
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
    [
      [homePos.lat, homePos.lon],
      [dto.lat, dto.lon],
    ],
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
    const activeCallsigns = new Set(stations.map((s) => s.callsign))
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
    stationsLoadFailed.value = false
  } catch (err) {
    console.error('Failed to load stations:', err)
    stationsLoadFailed.value = true
  }
}

// Map-specific reactions to hub events. Store mutations (beacon stream buffer,
// radios beacon state) are registered app-level in their own stores — these
// handlers only drive the map itself, and are unregistered on unmount.
function onHubPacketReceived(packet: PacketBroadcastDto) {
  {
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
  }
}

function onHubStationsStale(callsigns: string[]) {
  {
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
  }
}

function onHubOwnBeaconReceived() {
  triggerHomeMarkerFlash()
}

function onHubDigiConfirmation(dto: DigiConfirmationBroadcastDto) {
  drawConfirmationLine(dto)
}

function registerHubHandlers() {
  packetHub.on('packetReceived', onHubPacketReceived)
  packetHub.on('stationsStale', onHubStationsStale)
  packetHub.on('ownBeaconReceived', onHubOwnBeaconReceived)
  packetHub.on('digiConfirmation', onHubDigiConfirmation)
}

function unregisterHubHandlers() {
  packetHub.off('packetReceived', onHubPacketReceived)
  packetHub.off('stationsStale', onHubStationsStale)
  packetHub.off('ownBeaconReceived', onHubOwnBeaconReceived)
  packetHub.off('digiConfirmation', onHubDigiConfirmation)
}

// Watch: ring distances — persist to localStorage and redraw if rings are showing
watch(
  ringDistances,
  (newDists) => {
    localStorage.setItem(RINGS_STORAGE_KEY, JSON.stringify(newDists))
    if (showRings.value) {
      drawRings()
    }
  },
  { deep: true },
)

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
watch(
  () => theme.global.current.value.dark,
  (dark) => {
    if (selectedProvider.value === 'cartoLight' || selectedProvider.value === 'cartoDark') {
      setTileProvider(dark ? 'cartoDark' : 'cartoLight')
    }
  },
)

// Watch: track window — shrinking only needs a re-clip, growing needs more history
watch(trackMinutes, (minutes, prev) => {
  if (minutes > prev) {
    void reloadTracks()
  } else {
    pruneTracks()
  }
})

// Watch: weather overlay opacity — apply immediately to live layers
watch(radarOpacity, (v) => radarFrameLayers[currentRadarFrame]?.setOpacity(v))
watch(windOpacity, (v) => windLayer?.setOpacity(v))
watch(lightningOpacity, () => refreshLightningView())

// Watch: when selectedCallsign changes (e.g. from BeaconStreamView navigation), open path + fly
watch(
  () => selectionStore.selectedCallsign,
  (callsign, prev) => {
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
  },
)

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
  // The drag resizes the map container without a transition, so Leaflet's cached size is
  // stale until it is told: everything on the map would otherwise zoom about the old
  // centre and slide sideways on the next zoom.
  map.value?.invalidateSize()
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
    if (selectionStore.selectedCallsign) onDetailClose()
  })
  await loadStations()
  await loadStaleStations()
  registerHubHandlers()

  // Initialise radios — beacon state stays live via the shared packet hub.
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

  // Track tails expire on their own — re-clip every 30 s even without new beacons
  trackPruneInterval = setInterval(pruneTracks, 30_000)

  // Alerts can predate this mount (they arrive on any screen), so draw what's live
  await loadLightningAlertSettings()
  lightningAlertsStore.pruneTriggers()
  renderAlertStrikes()

  // Restore persisted layer states (issue #32 item 2)
  if (showRings.value) await drawRings()
  if (showOverlays.value) await loadAndDrawOverlays()
  if (showHeatmap.value) {
    heatmapLoading.value = true
    try {
      if (!heatmapPositions) {
        const positions = await getPacketPositions()
        heatmapPositions = positions.map((p) => [p.latitude, p.longitude] as [number, number])
      }
      if (map.value) {
        heatmapLayer = L.heatLayer(heatmapPositions, {
          radius: 18,
          blur: 15,
          maxZoom: 17,
          minOpacity: 0.3,
        }).addTo(map.value)
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

onUnmounted(() => {
  unregisterHubHandlers()
  if (ghostUpdateInterval) {
    clearInterval(ghostUpdateInterval)
    ghostUpdateInterval = null
  }
  if (staleDecayInterval) {
    clearInterval(staleDecayInterval)
    staleDecayInterval = null
  }
  if (trackPruneInterval) {
    clearInterval(trackPruneInterval)
    trackPruneInterval = null
  }
  for (const group of ghostLayers.values()) {
    group.remove()
  }
  ghostLayers.clear()
  for (const group of trackLayers.values()) {
    group.remove()
  }
  trackLayers.clear()
  trackPoints.clear()
  alertStrikeLayer?.remove()
  alertStrikeLayer = null
  alertStrikeMarkers.clear()
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
        @update:show-stale="onToggleShowStale"
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

      <!-- Layer panel — one grouped control replacing the old floating buttons -->
      <div v-if="!mobile" class="layer-panel">
        <button class="layer-panel-head" @click="layerPanelCollapsed = !layerPanelCollapsed">
          <v-icon size="18">mdi-layers</v-icon>
          <span class="font-weight-bold text-body-2">Layers</span>
          <v-icon size="14" class="ml-auto">
            {{ layerPanelCollapsed ? 'mdi-chevron-down' : 'mdi-chevron-up' }}
          </v-icon>
        </button>

        <div v-show="!layerPanelCollapsed" class="layer-panel-body">
          <div class="layer-group">
            <div class="layer-group-label">Stations</div>
            <div class="layer-row">
              <v-icon size="16" :color="showTracks ? 'primary' : 'grey'"
                >mdi-map-marker-path</v-icon
              >
              <span class="layer-row-label">Movement tracks</span>
              <v-switch
                :model-value="showTracks"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Movement tracks"
                @update:model-value="toggleTracks"
              />
            </div>
            <div v-if="showTracks" class="layer-sub">
              <div class="d-flex align-center ga-2">
                <span class="layer-opacity-label">Trail length</span>
                <v-select
                  v-model="trackMinutes"
                  :items="TRACK_WINDOW_OPTIONS"
                  item-title="title"
                  item-value="value"
                  density="compact"
                  hide-details
                  class="track-window-select"
                  aria-label="Movement track length"
                />
              </div>
            </div>
            <div class="layer-row">
              <v-icon size="16" :color="showGhostMarkers ? 'primary' : 'grey'"
                >mdi-map-marker-question</v-icon
              >
              <span class="layer-row-label">Estimated positions</span>
              <v-switch
                :model-value="showGhostMarkers"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Estimated positions"
                @update:model-value="toggleGhostMarkers"
              />
            </div>
            <div class="layer-row">
              <v-icon size="16" :color="showStaleStations ? 'primary' : 'grey'"
                >mdi-clock-alert-outline</v-icon
              >
              <span class="layer-row-label">Stale stations</span>
              <v-switch
                :model-value="showStaleStations"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Stale stations"
                @update:model-value="toggleStaleStations"
              />
            </div>
          </div>

          <div class="layer-group">
            <div class="layer-group-label">Overlays</div>
            <div class="layer-row">
              <v-icon size="16" :color="showOverlays ? 'primary' : 'grey'"
                >mdi-shape-polygon-plus</v-icon
              >
              <span class="layer-row-label">Alert zones</span>
              <v-switch
                :model-value="showOverlays"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Alert zones"
                @update:model-value="toggleOverlays"
              />
            </div>
            <div class="layer-row">
              <v-icon size="16" :color="showHeatmap ? 'primary' : 'grey'">mdi-fire</v-icon>
              <span class="layer-row-label">Packet heatmap</span>
              <v-progress-circular v-if="heatmapLoading" indeterminate size="14" width="2" />
              <v-switch
                :model-value="showHeatmap"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Packet heatmap"
                @update:model-value="toggleHeatmap"
              />
            </div>
            <div class="layer-row">
              <v-icon size="16" :color="showCoverage ? 'primary' : 'grey'">mdi-grid</v-icon>
              <span class="layer-row-label">Coverage grid</span>
              <v-progress-circular v-if="coverageLoading" indeterminate size="14" width="2" />
              <v-switch
                :model-value="showCoverage"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Coverage grid"
                @update:model-value="toggleCoverage"
              />
            </div>
          </div>

          <div class="layer-group">
            <div class="layer-group-label">Weather</div>
            <div class="layer-row">
              <v-icon size="16" :color="showRadar ? 'primary' : 'grey'">mdi-weather-rainy</v-icon>
              <span class="layer-row-label">Radar</span>
              <v-progress-circular v-if="radarLoading" indeterminate size="14" width="2" />
              <v-switch
                :model-value="showRadar"
                density="compact"
                hide-details
                color="primary"
                class="layer-switch"
                aria-label="Radar"
                @update:model-value="toggleRadar"
              />
            </div>
            <div v-if="showRadar && radarFrameCount > 0" class="layer-sub">
              <div class="d-flex align-center ga-1">
                <v-btn
                  icon="mdi-skip-previous"
                  size="x-small"
                  variant="text"
                  @click="stepRadarFrame(-1)"
                />
                <v-btn
                  :icon="radarPlaying ? 'mdi-pause' : 'mdi-play'"
                  size="x-small"
                  variant="text"
                  @click="radarPlaying ? pauseRadar() : playRadar()"
                />
                <v-btn
                  icon="mdi-skip-next"
                  size="x-small"
                  variant="text"
                  @click="stepRadarFrame(1)"
                />
                <v-btn
                  :disabled="!showingHistoricalWeather"
                  size="x-small"
                  variant="tonal"
                  color="primary"
                  class="radar-latest-btn"
                  @click="showLatestWeather"
                >
                  <v-icon size="14" start>mdi-clock-fast</v-icon>Latest
                </v-btn>
                <span class="radar-timestamp">{{ radarTimestamp }}</span>
                <span class="radar-frame-dots"
                  >{{ radarCurrentIdx + 1 }}/{{ radarFrameCount }}</span
                >
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
                />
              </div>
              <div class="d-flex align-center ga-2">
                <span class="layer-opacity-label">Opacity</span>
                <v-slider
                  v-model="radarOpacity"
                  class="layer-opacity-slider"
                  min="0.1"
                  max="1"
                  step="0.05"
                  density="compact"
                  hide-details
                />
                <span class="layer-opacity-pct">{{ Math.round(radarOpacity * 100) }}%</span>
              </div>
            </div>

            <v-tooltip
              :disabled="weatherStatus?.wind.available ?? false"
              :text="
                weatherStatus?.wind.reason ??
                'OpenWeatherMap API key required — configure in Settings.'
              "
              location="right"
            >
              <template #activator="{ props: tp }">
                <div v-bind="tp" class="layer-row">
                  <v-icon size="16" :color="showWind ? 'primary' : 'grey'"
                    >mdi-weather-windy</v-icon
                  >
                  <span class="layer-row-label">Wind</span>
                  <v-switch
                    :model-value="showWind"
                    :disabled="!(weatherStatus?.wind.available ?? false)"
                    density="compact"
                    hide-details
                    color="primary"
                    class="layer-switch"
                    aria-label="Wind"
                    @update:model-value="toggleWind"
                  />
                </div>
              </template>
            </v-tooltip>
            <div v-if="showWind" class="layer-sub">
              <div class="d-flex align-center ga-2">
                <span class="layer-opacity-label">Opacity</span>
                <v-slider
                  v-model="windOpacity"
                  class="layer-opacity-slider"
                  min="0.1"
                  max="1"
                  step="0.05"
                  density="compact"
                  hide-details
                />
                <span class="layer-opacity-pct">{{ Math.round(windOpacity * 100) }}%</span>
              </div>
            </div>

            <v-tooltip
              :disabled="weatherStatus?.lightning.available ?? false"
              :text="
                weatherStatus?.lightning.reason ??
                'Lightning feed (Blitzortung.org) not connected yet.'
              "
              location="right"
            >
              <template #activator="{ props: tp }">
                <div v-bind="tp" class="layer-row">
                  <v-icon size="16" :color="showLightning ? 'primary' : 'grey'"
                    >mdi-weather-lightning</v-icon
                  >
                  <span class="layer-row-label">Lightning</span>
                  <v-switch
                    :model-value="showLightning"
                    :disabled="!(weatherStatus?.lightning.available ?? false)"
                    density="compact"
                    hide-details
                    color="primary"
                    class="layer-switch"
                    aria-label="Lightning"
                    @update:model-value="toggleLightning"
                  />
                </div>
              </template>
            </v-tooltip>
            <div v-if="showLightning" class="layer-sub">
              <div class="d-flex align-center ga-2">
                <span class="layer-opacity-label">Opacity</span>
                <v-slider
                  v-model="lightningOpacity"
                  class="layer-opacity-slider"
                  min="0.1"
                  max="1"
                  step="0.05"
                  density="compact"
                  hide-details
                />
                <span class="layer-opacity-pct">{{ Math.round(lightningOpacity * 100) }}%</span>
              </div>
              <div v-if="showingHistoricalWeather" class="d-flex align-center ga-2">
                <span class="layer-opacity-label">Showing history</span>
                <v-btn
                  size="x-small"
                  variant="tonal"
                  color="primary"
                  class="radar-latest-btn"
                  @click="showLatestWeather"
                >
                  <v-icon size="14" start>mdi-clock-fast</v-icon>Latest
                </v-btn>
              </div>
            </div>
          </div>

          <div class="layer-panel-foot">
            <v-btn size="x-small" variant="text" color="primary" @click="resetLayers">
              Reset layers
            </v-btn>
            <span class="text-caption text-medium-emphasis">saved locally</span>
          </div>
        </div>
      </div>

      <!-- Top-right control stack: one column so these can never overlap,
           regardless of map width or the detail panel opening. -->
      <div class="map-controls-tr">
        <div class="d-flex ga-2 justify-end">
          <TileProviderSwitcher
            :providers="TILE_PROVIDERS"
            :selected="selectedProvider"
            :api-keys="apiKeys"
            @update:selected="setTileProvider"
          />
          <v-btn
            v-if="homePosition"
            color="grey-darken-1"
            size="small"
            variant="elevated"
            icon="mdi-home-map-marker"
            title="Back to home station"
            @click="centerOnHome"
          />
          <v-btn
            color="grey-darken-1"
            size="small"
            variant="elevated"
            icon="mdi-open-in-new"
            :title="'Open map in new window'"
            @click="openPopOut"
          />
        </div>
        <div v-if="!mobile" class="d-flex justify-end">
          <RangeRingsPanel
            v-model:show-rings="showRings"
            v-model:distances="ringDistances"
            v-model:expanded="ringPanelOpen"
          />
        </div>

        <!-- Lightning proximity alerts — newest first, stacking as they arrive
             and dropping off as they expire. -->
        <div v-if="alertStack.length > 0" class="strike-alert-panel">
          <div class="strike-alert-panel-head">
            <v-icon size="16" color="deep-orange-accent-3">mdi-flash-alert</v-icon>
            <span class="font-weight-bold text-caption">Lightning alerts</span>
            <!-- Mirrors the alert's own "Pan map to the triggering strike" setting,
                 written straight through so a storm can be handled from the map. -->
            <v-btn
              :color="lightningAutoPan ? 'primary' : 'grey'"
              :loading="autoPanSaving"
              size="x-small"
              variant="text"
              icon="mdi-crosshairs-gps"
              class="ml-auto"
              :title="
                lightningAutoPan
                  ? 'Auto-pan to new alerts: on (saved with the alert settings)'
                  : 'Auto-pan to new alerts: off (saved with the alert settings)'
              "
              @click="toggleAutoPan"
            />
            <v-btn
              size="x-small"
              variant="text"
              icon="mdi-close-box-multiple-outline"
              title="Clear all alerts"
              @click="clearAlertStrikes"
            />
          </div>
          <div class="strike-alert-list">
            <button
              v-for="strike in alertStack"
              :key="strike.key"
              class="strike-alert-item"
              :title="`Center on this strike — struck ${formatTime(strike.strikeTimeUtc)}, alerted ${formatStrikeAge(strike.feedLagSeconds)}`"
              @click="focusAlertStrike(strike)"
            >
              <v-icon size="14" color="deep-orange-accent-3">mdi-flash</v-icon>
              <span class="strike-alert-item-dist">{{ alertStrikeTitle(strike) }}</span>
              <span class="strike-alert-item-time">{{ formatTime(strike.strikeTimeUtc) }}</span>
              <v-icon
                size="14"
                class="strike-alert-item-x"
                title="Dismiss"
                @click.stop="dismissAlertStrike(strike)"
                >mdi-close</v-icon
              >
            </button>
          </div>
        </div>
      </div>

      <!-- Mobile: layer menu button (⋮) -->
      <v-menu
        v-if="mobile"
        v-model="mobileLayerMenuOpen"
        location="bottom start"
        :close-on-content-click="false"
      >
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
            <template #prepend
              ><v-icon :color="showTracks ? 'primary' : 'grey'"
                >mdi-map-marker-path</v-icon
              ></template
            >
            <v-list-item-title>{{ showTracks ? 'Hide Tracks' : 'Show Tracks' }}</v-list-item-title>
          </v-list-item>
          <div v-if="showTracks" class="mobile-layer-sub">
            <div class="d-flex align-center ga-2">
              <span class="layer-opacity-label">Trail length</span>
              <v-select
                v-model="trackMinutes"
                :items="TRACK_WINDOW_OPTIONS"
                item-title="title"
                item-value="value"
                density="compact"
                hide-details
                class="track-window-select"
                aria-label="Movement track length"
                @click.stop
              />
            </div>
          </div>
          <v-list-item @click="toggleGhostMarkers">
            <template #prepend
              ><v-icon :color="showGhostMarkers ? 'indigo' : 'grey'"
                >mdi-map-marker-question</v-icon
              ></template
            >
            <v-list-item-title>{{
              showGhostMarkers ? 'Hide Est. Positions' : 'Show Est. Positions'
            }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleStaleStations">
            <template #prepend
              ><v-icon :color="showStaleStations ? 'brown-lighten-1' : 'grey'"
                >mdi-clock-alert-outline</v-icon
              ></template
            >
            <v-list-item-title>{{
              showStaleStations ? 'Hide Stale' : 'Show Stale'
            }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleOverlays">
            <template #prepend
              ><v-icon :color="showOverlays ? 'teal-darken-1' : 'grey'"
                >mdi-layers</v-icon
              ></template
            >
            <v-list-item-title>{{ showOverlays ? 'Hide Zones' : 'Show Zones' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleHeatmap">
            <template #prepend
              ><v-icon :color="showHeatmap ? 'deep-orange-darken-1' : 'grey'"
                >mdi-fire</v-icon
              ></template
            >
            <v-list-item-title>{{ showHeatmap ? 'Hide Heatmap' : 'Heatmap' }}</v-list-item-title>
          </v-list-item>
          <v-list-item @click="toggleCoverage">
            <template #prepend
              ><v-icon :color="showCoverage ? 'green-darken-2' : 'grey'">mdi-grid</v-icon></template
            >
            <v-list-item-title>{{
              showCoverage ? 'Hide Coverage' : 'Coverage Grid'
            }}</v-list-item-title>
          </v-list-item>
          <v-divider class="my-1" />
          <v-list-item @click="showRings = !showRings">
            <template #prepend
              ><v-icon :color="showRings ? 'blue-darken-1' : 'grey'"
                >mdi-circle-double</v-icon
              ></template
            >
            <v-list-item-title>{{
              showRings ? 'Hide Range Rings' : 'Show Range Rings'
            }}</v-list-item-title>
          </v-list-item>
          <v-divider class="my-1" />
          <v-list-item @click="toggleRadar">
            <template #prepend
              ><v-icon :color="showRadar ? 'blue-darken-2' : 'grey'"
                >mdi-weather-rainy</v-icon
              ></template
            >
            <v-list-item-title>{{ showRadar ? 'Hide Radar' : 'Radar' }}</v-list-item-title>
          </v-list-item>
          <!-- Radar scrubber + opacity — previously desktop-only -->
          <div v-if="showRadar && radarFrameCount > 0" class="mobile-layer-sub">
            <div class="d-flex align-center ga-1">
              <v-btn
                icon="mdi-skip-previous"
                size="x-small"
                variant="text"
                @click.stop="stepRadarFrame(-1)"
              />
              <v-btn
                :icon="radarPlaying ? 'mdi-pause' : 'mdi-play'"
                size="x-small"
                variant="text"
                @click.stop="radarPlaying ? pauseRadar() : playRadar()"
              />
              <v-btn
                icon="mdi-skip-next"
                size="x-small"
                variant="text"
                @click.stop="stepRadarFrame(1)"
              />
              <v-btn
                :disabled="!showingHistoricalWeather"
                size="x-small"
                variant="tonal"
                color="primary"
                class="radar-latest-btn"
                @click.stop="showLatestWeather"
              >
                <v-icon size="14" start>mdi-clock-fast</v-icon>Latest
              </v-btn>
              <span class="radar-timestamp">{{ radarTimestamp }}</span>
              <span class="radar-frame-dots">{{ radarCurrentIdx + 1 }}/{{ radarFrameCount }}</span>
            </div>
            <div class="d-flex align-center ga-2">
              <span class="layer-opacity-label">Opacity</span>
              <v-slider
                v-model="radarOpacity"
                class="layer-opacity-slider"
                min="0.1"
                max="1"
                step="0.05"
                density="compact"
                hide-details
              />
              <span class="layer-opacity-pct">{{ Math.round(radarOpacity * 100) }}%</span>
            </div>
          </div>
          <v-list-item :disabled="!(weatherStatus?.wind.available ?? false)" @click="toggleWind">
            <template #prepend
              ><v-icon :color="showWind ? 'cyan-darken-1' : 'grey'"
                >mdi-weather-windy</v-icon
              ></template
            >
            <v-list-item-title>{{ showWind ? 'Hide Wind' : 'Wind' }}</v-list-item-title>
          </v-list-item>
          <v-list-item
            :disabled="!(weatherStatus?.lightning.available ?? false)"
            @click="toggleLightning"
          >
            <template #prepend
              ><v-icon :color="showLightning ? 'yellow-darken-2' : 'grey'"
                >mdi-weather-lightning</v-icon
              ></template
            >
            <v-list-item-title>{{
              showLightning ? 'Hide Lightning' : 'Lightning'
            }}</v-list-item-title>
          </v-list-item>
          <div v-if="showWind || showLightning" class="mobile-layer-sub">
            <div v-if="showWind" class="d-flex align-center ga-2">
              <span class="layer-opacity-label">Wind</span>
              <v-slider
                v-model="windOpacity"
                class="layer-opacity-slider"
                min="0.1"
                max="1"
                step="0.05"
                density="compact"
                hide-details
              />
              <span class="layer-opacity-pct">{{ Math.round(windOpacity * 100) }}%</span>
            </div>
            <div v-if="showLightning" class="d-flex align-center ga-2">
              <span class="layer-opacity-label">Lightning</span>
              <v-slider
                v-model="lightningOpacity"
                class="layer-opacity-slider"
                min="0.1"
                max="1"
                step="0.05"
                density="compact"
                hide-details
              />
              <span class="layer-opacity-pct">{{ Math.round(lightningOpacity * 100) }}%</span>
            </div>
          </div>
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
        show-page-link
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
        <v-btn
          icon="mdi-close"
          size="small"
          variant="text"
          @click="mobileStationSheetOpen = false"
        />
      </v-card-title>
      <v-divider />
      <div style="overflow-y: auto; max-height: calc(70vh - 60px)">
        <StationListSidebar
          :stations="stationsList"
          :packet-counts="sessionPacketCounts"
          :selected-callsign="selectionStore.selectedCallsign"
          :stale-stations="staleStationsList"
          :show-stale="showStaleStations"
          @select-station="
            (cs) => {
              onSidebarSelectStation(cs)
              mobileStationSheetOpen = false
            }
          "
          @update:show-stale="onToggleShowStale"
        />
      </div>
    </v-card>
  </v-bottom-sheet>

  <!-- Mobile: station detail bottom sheet -->
  <v-bottom-sheet
    v-if="mobile"
    :model-value="!!selectionStore.selectedCallsign"
    max-height="65vh"
    @update:model-value="(v) => !v && onDetailClose()"
  >
    <v-card style="height: 65vh; display: flex; flex-direction: column">
      <div style="overflow-y: auto; flex: 1">
        <StationDetailPanel
          show-page-link
          :callsign="selectionStore.selectedCallsign"
          :refresh-key="detailRefreshKey"
          @close="onDetailClose"
          @highlight-position="onHighlightPosition"
        />
      </div>
    </v-card>
  </v-bottom-sheet>

  <!-- Tile provider fallback notification -->
  <v-snackbar v-model="providerFallbackSnackbar" :timeout="5000" color="warning" location="bottom">
    {{ providerFallbackMessage }}
  </v-snackbar>

  <!-- Station load failure — persistent until a load succeeds -->
  <v-snackbar v-model="stationsLoadFailed" :timeout="-1" color="error" location="bottom">
    Couldn't load stations — the map may be empty or out of date.
    <template #actions>
      <v-btn variant="text" @click="loadStations()">Retry</v-btn>
      <v-btn icon="mdi-close" size="small" variant="text" @click="stationsLoadFailed = false" />
    </template>
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

/* Leaflet's zoom control shares the top-left corner with the sidebar toggle —
   push it down so the two never stack. */
:deep(.leaflet-top.leaflet-left .leaflet-control-zoom) {
  margin-top: 52px;
}

/* The grouped layer panel — anchored under the sidebar toggle, never overlaps
   other controls regardless of viewport width. */
.layer-panel {
  position: absolute;
  top: 10px;
  left: 60px;
  z-index: 1000;
  width: 262px;
  background: rgb(var(--v-theme-surface));
  border-radius: 10px;
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.35);
  overflow: hidden;
}

.layer-panel-head {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  cursor: pointer;
  background: none;
  border: none;
  color: inherit;
  font: inherit;
  text-align: left;
}

.layer-panel-body {
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  max-height: calc(100vh - 220px);
  overflow-y: auto;
  /* Never scroll sideways — wide sub-rows (radar transport) wrap instead. */
  overflow-x: hidden;
}

.layer-group {
  padding: 6px 12px 8px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}

.layer-group-label {
  font-size: 10.5px;
  text-transform: uppercase;
  letter-spacing: 0.09em;
  font-weight: 700;
  opacity: 0.55;
  margin: 4px 0 2px;
}

.layer-row {
  display: flex;
  align-items: center;
  gap: 9px;
  min-height: 32px;
}

.layer-row-label {
  font-size: 13px;
  flex: 1;
  min-width: 0;
}

.layer-switch {
  flex: none;
}

.layer-sub {
  margin: 0 0 6px 12px;
  padding: 6px 8px;
  background: rgba(var(--v-theme-on-surface), 0.05);
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

/* Transport/opacity rows wrap rather than force the panel wider. */
.layer-sub > div {
  flex-wrap: wrap;
  min-width: 0;
}

.layer-panel-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 8px 6px 4px;
}

/* One stacked column for every top-right control — overlap-proof. */
.map-controls-tr {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1000;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 8px;
  pointer-events: none;
}

.map-controls-tr > * {
  pointer-events: auto;
}

.map-controls-tr :deep(.tile-switcher) {
  position: static;
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

.radar-speed-select {
  width: 72px;
  flex-shrink: 0;
}

.radar-latest-btn {
  flex-shrink: 0;
  min-width: 0;
  padding: 0 6px;
  font-size: 10px;
}

.track-window-select {
  width: 104px;
  flex-shrink: 0;
}

/* ── Lightning proximity alerts ─────────────────────────────────────────────── */

.strike-alert-panel {
  width: 232px;
  background: rgb(var(--v-theme-surface));
  border-radius: 10px;
  border: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  border-left: 3px solid #ff3d00;
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.25);
  overflow: hidden;
}

.strike-alert-panel-head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 4px 4px 8px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}

.strike-alert-list {
  max-height: 172px;
  overflow-y: auto;
}

.strike-alert-item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 4px 6px 4px 8px;
  font-size: 11px;
  text-align: left;
  cursor: pointer;
  background: transparent;
  border: none;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
}

.strike-alert-item:hover {
  background: rgba(var(--v-theme-on-surface), 0.07);
}

/* Newest alert reads first — the one the toast just announced. */
.strike-alert-item:first-child .strike-alert-item-dist {
  font-weight: 700;
}

.strike-alert-item-dist {
  white-space: nowrap;
}

.strike-alert-item-time {
  margin-left: auto;
  opacity: 0.65;
  white-space: nowrap;
}

.strike-alert-item-x {
  opacity: 0.45;
}

.strike-alert-item-x:hover {
  opacity: 1;
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

.mobile-layer-sub {
  padding: 4px 14px 8px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
</style>

<style>
/* Radar animation cross-fades whole layers by swapping container opacity; Leaflet's
   independent per-tile fade-in (~200 ms, driven by inline styles) would leave freshly
   loaded tiles semi-transparent when a frame is revealed at fast playback speeds.
   Forcing tile opacity only affects the fade — layer opacity lives on the container. */
.leaflet-weatherPane-pane .leaflet-tile {
  opacity: 1 !important;
}

.aprs-icon-container {
  background: transparent !important;
  border: none !important;
}

/* ── Alert-triggering lightning strike ──────────────────────────────────────
   Deliberately louder than the ordinary lightning dots, and drawn in its own pane
   so radar playback never clears or dims it. */

.strike-alert-marker {
  position: relative;
  width: 34px;
  height: 34px;
  cursor: pointer;
}

/* Static ring — the strike's actual position. */
.strike-alert-marker::after {
  content: '';
  position: absolute;
  inset: 8px;
  border-radius: 50%;
  border: 2px solid #ff3d00;
  background: rgba(255, 61, 0, 0.25);
}

/* Expanding halo, newest alert only. */
.strike-alert-pulse {
  position: absolute;
  inset: 8px;
  border-radius: 50%;
  border: 2px solid #ff3d00;
}

.strike-alert-marker-newest .strike-alert-pulse {
  animation: strike-alert-pulse 1.6s ease-out infinite;
}

.strike-alert-bolt {
  position: absolute;
  inset: 0;
  z-index: 2;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 16px;
  line-height: 1;
  filter: drop-shadow(0 0 3px rgba(0, 0, 0, 0.85));
}

@keyframes strike-alert-pulse {
  0% {
    transform: scale(0.8);
    opacity: 0.9;
  }
  100% {
    transform: scale(2);
    opacity: 0;
  }
}

.strike-alert-label {
  background: #ff3d00 !important;
  color: #fff !important;
  border: none !important;
  border-radius: 3px !important;
  font-size: 10px !important;
  font-weight: 700 !important;
  padding: 1px 5px !important;
  white-space: nowrap !important;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.4) !important;
}

.strike-alert-label::before {
  border-top-color: #ff3d00 !important;
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
  color: #00bcd4;
  text-shadow: 0 0 3px rgba(0, 0, 0, 0.9);
  pointer-events: none;
  z-index: 1;
}

@keyframes wx-pulse {
  0% {
    transform: scale(0.6);
    opacity: 1;
  }
  70% {
    transform: scale(1.6);
    opacity: 0;
  }
  100% {
    transform: scale(1.6);
    opacity: 0;
  }
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

.beacon-flash-position {
  border-color: rgba(33, 150, 243, 0.9);
}
.beacon-flash-message {
  border-color: rgba(76, 175, 80, 0.9);
}
.beacon-flash-weather {
  border-color: rgba(0, 150, 136, 0.9);
}
.beacon-flash-telemetry {
  border-color: rgba(156, 39, 176, 0.9);
}
.beacon-flash-unknown {
  border-color: rgba(158, 158, 158, 0.9);
}
.beacon-flash-ownbeacon {
  border-color: rgba(255, 215, 0, 0.9);
}

@keyframes beacon-expand {
  0% {
    transform: scale(0.5);
    opacity: 1;
  }
  100% {
    transform: scale(2.4);
    opacity: 0;
  }
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
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.4);
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

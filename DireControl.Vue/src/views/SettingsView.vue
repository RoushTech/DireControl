<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed, watch, nextTick } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import L from 'leaflet'
import {
  getGeofences,
  createGeofence,
  deleteGeofence,
  getProximityRules,
  createProximityRule,
  deleteProximityRule,
} from '@/api/alertsApi'
import type { GeofenceDto, ProximityRuleDto } from '@/types/alert'
import {
  getRadios,
  createRadio,
  updateRadio,
  deleteRadio,
  toggleRadioActive,
  beaconNow,
} from '@/api/radiosApi'
import { defaultRadioModemConfig, type RadioDto, type RadioModemConfig } from '@/types/radio'
import {
  getSettings,
  updateOutboundPath,
  updateAprsIsSettings,
  updateStationIdentity,
  updateWeatherApiKeys,
  RadarProvider,
} from '@/api/stationsApi'
import {
  getModemDevices,
  updateRfServices,
  updateExternalTnc,
  PttMethods,
  type ModemDevicesDto,
} from '@/api/modemApi'
import { getWeatherStatus } from '@/api/weatherApi'
import {
  getMaintenanceStatus,
  updateRetention,
  runCleanup,
  getReprocessStatus,
  startReprocess,
  type CleanupResult,
  type ReprocessStatusDto,
} from '@/api/maintenanceApi'
import type { SettingsDto } from '@/types/station'
import { useUnits } from '@/composables/useUnits'
import { getSymbolStyle } from '@/utils/aprsIcon'
import AprsSymbolPicker from '@/components/AprsSymbolPicker.vue'

// ─── Units ────────────────────────────────────────────────────────────────────
const { distanceUnit, formatDistance, setDistanceUnit } = useUnits()

// ─── Tab navigation (deep-linked via ?tab=) ───────────────────────────────────
const route = useRoute()
const router = useRouter()
const TAB_VALUES = ['station', 'radios', 'rf', 'aprsis', 'map', 'zones', 'maintenance']
const activeTab = ref<string>(
  TAB_VALUES.includes(route.query.tab as string) ? (route.query.tab as string) : 'station',
)
watch(activeTab, (tab) => {
  router.replace({ query: { ...route.query, tab } })
})

// ─── Retry settings (read-only display) ──────────────────────────────────────
const retrySettings = ref<Pick<
  SettingsDto,
  'maxRetryAttempts' | 'initialRetryDelaySeconds'
> | null>(null)

// ─── Messaging settings ───────────────────────────────────────────────────────
const outboundPath = ref('')
const outboundPathSaving = ref(false)
const outboundPathSaveError = ref('')

const PATH_REGEX = /^[A-Za-z0-9-]+(,[A-Za-z0-9-]+)*$/

const outboundPathError = computed(() => {
  const p = outboundPath.value.trim()
  if (!p) return ''
  return PATH_REGEX.test(p) ? '' : 'Use comma-separated callsigns, e.g. WIDE1-1,WIDE2-1'
})

let pathSaveTimer: ReturnType<typeof setTimeout> | null = null

/** Sets a common outbound path preset and saves it. */
function applyPathPreset(path: string) {
  outboundPath.value = path
  schedulePathSave()
}

function schedulePathSave() {
  if (pathSaveTimer) clearTimeout(pathSaveTimer)
  pathSaveTimer = setTimeout(async () => {
    if (outboundPathError.value) return
    outboundPathSaving.value = true
    outboundPathSaveError.value = ''
    try {
      await updateOutboundPath(outboundPath.value.trim())
    } catch {
      outboundPathSaveError.value = 'Failed to save outbound path.'
    } finally {
      outboundPathSaving.value = false
    }
  }, 600)
}

// ─── APRS-IS settings ─────────────────────────────────────────────────────────
const aprsIsEnabled = ref(false)
const aprsIsHost = ref('rotate.aprs2.net')
const aprsIsPort = ref(14580)
const aprsIsPasscodeOverride = ref<number | null>(null)
const aprsIsPasscodeComputed = ref(0)
const aprsIsFilter = ref('r/39.0/-98.0/500 t/m')
const deduplicationWindowSeconds = ref(60)
const aprsIsSaving = ref(false)
const aprsIsSaveError = ref('')
const aprsIsSaveSuccess = ref(false)

async function saveAprsIsSettings() {
  aprsIsSaving.value = true
  aprsIsSaveError.value = ''
  aprsIsSaveSuccess.value = false
  try {
    await updateAprsIsSettings({
      aprsIsEnabled: aprsIsEnabled.value,
      aprsIsHost: aprsIsHost.value.trim(),
      aprsIsPort: aprsIsPort.value,
      aprsIsPasscodeOverride: aprsIsPasscodeOverride.value,
      aprsIsFilter: aprsIsFilter.value.trim(),
      deduplicationWindowSeconds: deduplicationWindowSeconds.value,
    })
    aprsIsSaveSuccess.value = true
    setTimeout(() => {
      aprsIsSaveSuccess.value = false
    }, 3000)
  } catch {
    aprsIsSaveError.value = 'Failed to save APRS-IS settings.'
  } finally {
    aprsIsSaving.value = false
  }
}

// ─── Modem device lists (for the per-radio dialog) ───────────────────────────
const modemDevices = ref<ModemDevicesDto>({ audio: [], serialPorts: [], hidDevices: [] })

const pttMethodItems = [
  { title: 'None (VOX)', value: PttMethods.None },
  { title: 'Serial RTS/DTR', value: PttMethods.SerialRtsDtr },
  { title: 'CM108 USB HID (DigiRig)', value: PttMethods.Cm108 },
  { title: 'Linux GPIO', value: PttMethods.Gpio },
  { title: 'Hamlib rigctld', value: PttMethods.Rigctld },
]

const modemCaptureDeviceItems = computed(() =>
  modemDevices.value.audio
    .filter((d) => d.supportsCapture)
    .map((d) => ({ title: `${d.name} — ${d.description}`, value: d.name })),
)

const modemPlaybackDeviceItems = computed(() =>
  modemDevices.value.audio
    .filter((d) => d.supportsPlayback)
    .map((d) => ({ title: `${d.name} — ${d.description}`, value: d.name })),
)

const modemHidDeviceItems = computed(() =>
  modemDevices.value.hidDevices.map((d) => ({ title: `${d.path} — ${d.name}`, value: d.path })),
)

// ─── RF services (digipeater / KISS server / iGate) ──────────────────────────
const digipeaterEnabled = ref(false)
const digipeaterMaxWideN = ref(2)
const digipeaterFillInOnly = ref(false)
const kissServerEnabled = ref(false)
const kissServerPort = ref(8010)
const rfToIsGatingEnabled = ref(false)
const isToRfGatingEnabled = ref(false)
const isToRfPath = ref('')
const isToRfRecentHeardMinutes = ref(30)
const rfServicesSaving = ref(false)
const rfServicesSaveError = ref('')
const rfServicesSaveSuccess = ref(false)

function loadRfServicesSettings(s: SettingsDto) {
  digipeaterEnabled.value = s.digipeaterEnabled
  digipeaterMaxWideN.value = s.digipeaterMaxWideN
  digipeaterFillInOnly.value = s.digipeaterFillInOnly
  kissServerEnabled.value = s.kissServerEnabled
  kissServerPort.value = s.kissServerPort
  rfToIsGatingEnabled.value = s.rfToIsGatingEnabled
  isToRfGatingEnabled.value = s.isToRfGatingEnabled
  isToRfPath.value = s.isToRfPath
  isToRfRecentHeardMinutes.value = s.isToRfRecentHeardMinutes
}

// ─── External TNC (KISS TCP client, e.g. Direwolf) ───────────────────────────
const direwolfEnabled = ref(false)
const direwolfHost = ref('localhost')
const direwolfPort = ref(8001)
const direwolfReconnectDelaySeconds = ref(5)
const externalTncSaving = ref(false)
const externalTncSaveError = ref('')
const externalTncSaveSuccess = ref(false)

function loadExternalTncSettings(s: SettingsDto) {
  direwolfEnabled.value = s.direwolfEnabled
  direwolfHost.value = s.direwolfHost
  direwolfPort.value = s.direwolfPort
  direwolfReconnectDelaySeconds.value = s.direwolfReconnectDelaySeconds
}

async function saveExternalTnc() {
  externalTncSaving.value = true
  externalTncSaveError.value = ''
  externalTncSaveSuccess.value = false
  try {
    await updateExternalTnc({
      direwolfEnabled: direwolfEnabled.value,
      direwolfHost: direwolfHost.value.trim(),
      direwolfPort: direwolfPort.value,
      direwolfReconnectDelaySeconds: direwolfReconnectDelaySeconds.value,
    })
    externalTncSaveSuccess.value = true
    setTimeout(() => {
      externalTncSaveSuccess.value = false
    }, 3000)
  } catch (e: unknown) {
    const detail = (e as { response?: { data?: unknown } })?.response?.data
    externalTncSaveError.value =
      typeof detail === 'string' && detail ? detail : 'Failed to save external TNC settings.'
  } finally {
    externalTncSaving.value = false
  }
}

async function saveRfServices() {
  rfServicesSaving.value = true
  rfServicesSaveError.value = ''
  rfServicesSaveSuccess.value = false
  try {
    await updateRfServices({
      digipeaterEnabled: digipeaterEnabled.value,
      digipeaterMaxWideN: digipeaterMaxWideN.value,
      digipeaterFillInOnly: digipeaterFillInOnly.value,
      kissServerEnabled: kissServerEnabled.value,
      kissServerPort: kissServerPort.value,
      rfToIsGatingEnabled: rfToIsGatingEnabled.value,
      isToRfGatingEnabled: isToRfGatingEnabled.value,
      isToRfPath: isToRfPath.value.trim(),
      isToRfRecentHeardMinutes: isToRfRecentHeardMinutes.value,
    })
    rfServicesSaveSuccess.value = true
    setTimeout(() => {
      rfServicesSaveSuccess.value = false
    }, 3000)
  } catch (e: unknown) {
    const detail = (e as { response?: { data?: unknown } })?.response?.data
    rfServicesSaveError.value =
      typeof detail === 'string' && detail ? detail : 'Failed to save RF services settings.'
  } finally {
    rfServicesSaving.value = false
  }
}

// ─── Radios ───────────────────────────────────────────────────────────────────
const radios = ref<RadioDto[]>([])
const radioDialogOpen = ref(false)
const editingRadioId = ref<string | null>(null)
const radioSaving = ref(false)
const radioSaveError = ref('')
const radioFormDirty = ref(false)
const showTxTiming = ref(false)
let suppressRadioDirty = false

const rName = ref('')
const rCallsign = ref('')
const rSsid = ref('')
const rChannel = ref(0)
const rExpectedInterval = ref(600)
const rAutoBeaconEnabled = ref(false)
const rAutoBeaconInterval = ref(1800)
const rNotes = ref('')
const rBeaconPath = ref('')
const rBeaconSymbol = ref('')
const rBeaconComment = ref('')
const rFrequencyMhz = ref<number | null>(null)
const rMode = ref('')
const rModem = ref<RadioModemConfig>(defaultRadioModemConfig())

// Any edit flips the dirty flag so the save bar can say "unsaved changes";
// suppressed while openAdd/openEdit seed the form.
watch(
  [
    rName,
    rCallsign,
    rSsid,
    rChannel,
    rExpectedInterval,
    rAutoBeaconEnabled,
    rAutoBeaconInterval,
    rNotes,
    rBeaconPath,
    rBeaconSymbol,
    rBeaconComment,
    rFrequencyMhz,
    rMode,
    rModem,
  ],
  () => {
    if (!suppressRadioDirty) radioFormDirty.value = true
  },
  { deep: true },
)

function seedRadioFormDone() {
  radioSaveError.value = ''
  radioFormDirty.value = false
  showTxTiming.value = false
  radioDialogOpen.value = true
  void nextTick(() => {
    suppressRadioDirty = false
    radioFormDirty.value = false
  })
}

const autoBeaconError = computed(() =>
  rAutoBeaconEnabled.value && rAutoBeaconInterval.value < 60
    ? 'Auto-beacon interval must be at least 60 seconds'
    : '',
)

const radioFormValid = computed(
  () =>
    rName.value.trim().length > 0 &&
    /^[A-Z0-9]{3,6}$/i.test(rCallsign.value.trim()) &&
    !autoBeaconError.value,
)

const computedFullCallsign = computed(() => {
  const cs = rCallsign.value.trim().toUpperCase()
  const ssid = rSsid.value.trim()
  return ssid ? `${cs}-${ssid}` : cs
})

const duplicateRadio = computed(() => {
  if (!radioFormValid.value) return null
  return (
    radios.value.find(
      (r) => r.fullCallsign === computedFullCallsign.value && r.id !== editingRadioId.value,
    ) ?? null
  )
})

const ssidError = computed(() => {
  const s = rSsid.value.trim()
  if (s === '') return ''
  const n = parseInt(s, 10)
  if (isNaN(n) || n < 0 || n > 15) return 'SSID must be 0–15'
  return ''
})

async function loadRadios() {
  try {
    radios.value = await getRadios()
  } catch {
    /* */
  }
}

function openAddRadio() {
  suppressRadioDirty = true
  editingRadioId.value = null
  rName.value = ''
  rCallsign.value = ''
  rSsid.value = ''
  rChannel.value = 0
  rExpectedInterval.value = 600
  rAutoBeaconEnabled.value = false
  rAutoBeaconInterval.value = 1800
  rNotes.value = ''
  rBeaconPath.value = ''
  rBeaconSymbol.value = ''
  rBeaconComment.value = ''
  rFrequencyMhz.value = null
  rMode.value = ''
  rModem.value = defaultRadioModemConfig()
  seedRadioFormDone()
}

function openEditRadio(radio: RadioDto) {
  suppressRadioDirty = true
  editingRadioId.value = radio.id
  rName.value = radio.name
  rCallsign.value = radio.callsign
  rSsid.value = radio.ssid ?? ''
  rChannel.value = radio.channelNumber
  rExpectedInterval.value = radio.expectedIntervalSeconds
  rAutoBeaconEnabled.value = radio.autoBeaconEnabled
  rAutoBeaconInterval.value = radio.autoBeaconIntervalSeconds
  rNotes.value = radio.notes ?? ''
  rBeaconPath.value = radio.beaconPath ?? ''
  rBeaconSymbol.value = radio.beaconSymbol ?? ''
  rBeaconComment.value = radio.beaconComment ?? ''
  rFrequencyMhz.value = radio.frequencyMhz
  rMode.value = radio.mode ?? ''
  rModem.value = { ...radio.modem }
  seedRadioFormDone()
}

async function saveRadio() {
  if (!radioFormValid.value || ssidError.value) return
  radioSaving.value = true
  radioSaveError.value = ''
  const payload = {
    name: rName.value.trim(),
    callsign: rCallsign.value.trim().toUpperCase(),
    ssid: rSsid.value.trim() || null,
    channelNumber: rChannel.value,
    notes: rNotes.value.trim() || null,
    beaconPath: rBeaconPath.value.trim() || null,
    beaconSymbol: rBeaconSymbol.value.trim() || null,
    beaconComment: rBeaconComment.value.trim() || null,
    expectedIntervalSeconds: rExpectedInterval.value,
    autoBeaconEnabled: rAutoBeaconEnabled.value,
    autoBeaconIntervalSeconds: rAutoBeaconInterval.value,
    frequencyMhz: rFrequencyMhz.value,
    mode: rMode.value.trim() || null,
    modem: {
      ...rModem.value,
      modemCaptureDevice: rModem.value.modemCaptureDevice.trim() || 'default',
      modemPlaybackDevice: rModem.value.modemPlaybackDevice.trim() || 'default',
      pttSerialPort: rModem.value.pttSerialPort?.trim() || null,
      pttHidDevice: rModem.value.pttHidDevice?.trim() || null,
    },
  }
  try {
    if (editingRadioId.value) {
      const updated = await updateRadio(editingRadioId.value, payload)
      const idx = radios.value.findIndex((r) => r.id === editingRadioId.value)
      if (idx !== -1) radios.value[idx] = updated
    } else {
      const created = await createRadio(payload)
      radios.value.push(created)
    }
    radioDialogOpen.value = false
  } catch (e: unknown) {
    // Keep the dialog open and say why — a silent failure looks like a frozen save.
    const detail = (e as { response?: { data?: unknown } })?.response?.data
    radioSaveError.value =
      typeof detail === 'string' && detail
        ? detail
        : 'Failed to save the radio — check the values and that the backend is reachable.'
  } finally {
    radioSaving.value = false
  }
}

async function toggleActive(id: string) {
  try {
    const updated = await toggleRadioActive(id)
    const idx = radios.value.findIndex((r) => r.id === id)
    if (idx !== -1) radios.value[idx] = updated
  } catch {
    const radio = radios.value.find((r) => r.id === id)
    showToast(
      `Couldn't ${radio?.isActive ? 'deactivate' : 'activate'} ${radio?.fullCallsign ?? 'radio'} — the backend may be unreachable.`,
      'error',
    )
  }
}

// ─── Toast (shared across tabs) ───────────────────────────────────────────────
const beaconToast = ref(false)
const beaconToastText = ref('')
const beaconToastColor = ref<'success' | 'error'>('success')

function showToast(text: string, color: 'success' | 'error') {
  beaconToastColor.value = color
  beaconToastText.value = text
  beaconToast.value = true
}

// ─── Beacon now ───────────────────────────────────────────────────────────────
const beaconing = ref<Record<string, boolean>>({})

async function doBeaconNow(radio: RadioDto) {
  beaconing.value[radio.id] = true
  try {
    await beaconNow(radio.id)
    beaconToastColor.value = 'success'
    beaconToastText.value = `Beacon sent for ${radio.fullCallsign}.`
  } catch {
    beaconToastColor.value = 'error'
    beaconToastText.value = `Beacon failed for ${radio.fullCallsign}. Check that a TX modem is connected and home position is set.`
  } finally {
    beaconing.value[radio.id] = false
    beaconToast.value = true
  }
}

// ─── Delete (shared confirm dialog handles radios too) ────────────────────────
function promptDeleteRadio(radio: RadioDto) {
  const historyNote =
    radio.beaconCount > 0
      ? ` This radio has ${radio.beaconCount} beacon records. Deleting will remove all history.`
      : ''
  deleteConfirmMessage.value = `Delete radio "${radio.name}"?${historyNote} This cannot be undone.`
  deleteConfirmAction = async () => {
    await deleteRadio(radio.id)
    radios.value = radios.value.filter((r) => r.id !== radio.id)
  }
  deleteConfirmOpen.value = true
}

// ─── API Keys ────────────────────────────────────────────────────────────────
const API_KEYS_STORAGE_KEY = 'direcontrol-api-keys'

function readApiKeys(): Record<string, string> {
  try {
    const raw = localStorage.getItem(API_KEYS_STORAGE_KEY)
    if (raw) return JSON.parse(raw) as Record<string, string>
  } catch {
    /* ignore */
  }
  return {}
}

const jawgApiKey = ref(readApiKeys()['jawg'] ?? '')
const showJawgKey = ref(false)
const apiKeySaved = ref(false)

function saveApiKeys() {
  const keys = readApiKeys()
  if (jawgApiKey.value.trim()) {
    keys['jawg'] = jawgApiKey.value.trim()
  } else {
    delete keys['jawg']
  }
  localStorage.setItem(API_KEYS_STORAGE_KEY, JSON.stringify(keys))
  apiKeySaved.value = true
  setTimeout(() => {
    apiKeySaved.value = false
  }, 2500)
}

// ─── Weather overlay API keys ─────────────────────────────────────────────────
const owmApiKey = ref('')
const tomorrowIoApiKey = ref('')
const owmKeyConfigured = ref(false)
const tomorrowKeyConfigured = ref(false)
const weatherKeysSaving = ref(false)
const weatherKeysSaveError = ref('')
const weatherKeysSaveSuccess = ref(false)
const showOwmKey = ref(false)
const showTomorrowKey = ref(false)
const selectedRadarProvider = ref<RadarProvider>(RadarProvider.IemNexrad)
const rainViewerProApiKey = ref('')
const rvProKeyConfigured = ref(false)
const showRainViewerProKey = ref(false)

async function saveWeatherApiKeys() {
  weatherKeysSaving.value = true
  weatherKeysSaveError.value = ''
  weatherKeysSaveSuccess.value = false
  const owmValue = owmApiKey.value.trim() || null
  const tomorrowValue = tomorrowIoApiKey.value.trim() || null
  const rvProValue = rainViewerProApiKey.value.trim() || null
  try {
    await updateWeatherApiKeys(owmValue, tomorrowValue, selectedRadarProvider.value, rvProValue)
    weatherKeysSaveSuccess.value = true
    // Update configured flags based on what was saved
    if (owmValue !== null) owmKeyConfigured.value = true
    if (tomorrowValue !== null) tomorrowKeyConfigured.value = true
    if (owmValue === null) owmKeyConfigured.value = false
    if (tomorrowValue === null) tomorrowKeyConfigured.value = false
    rvProKeyConfigured.value =
      selectedRadarProvider.value === RadarProvider.RainViewerPro
        ? rvProValue !== null
          ? true
          : rvProKeyConfigured.value
        : false
    // Clear the fields after saving — values are secrets
    owmApiKey.value = ''
    tomorrowIoApiKey.value = ''
    rainViewerProApiKey.value = ''
    setTimeout(() => {
      weatherKeysSaveSuccess.value = false
    }, 3000)
  } catch {
    weatherKeysSaveError.value = 'Failed to save weather API keys.'
  } finally {
    weatherKeysSaving.value = false
  }
}

// ---- Geofences ----
const geofences = ref<GeofenceDto[]>([])
const showAddGeofence = ref(false)
const gfName = ref('')
const gfLat = ref<number | null>(null)
const gfLon = ref<number | null>(null)
const gfRadius = ref<number>(500)
const gfAlertOnEnter = ref(true)
const gfAlertOnExit = ref(true)
const gfSaving = ref(false)

// ---- Proximity Rules ----
const rules = ref<ProximityRuleDto[]>([])
const showAddRule = ref(false)
const prName = ref('')
const prCallsign = ref('')
const prLat = ref<number | null>(null)
const prLon = ref<number | null>(null)
const prRadius = ref<number>(1000)
const prSaving = ref(false)

// ---- Leaflet map for picking coordinates ----
let map: L.Map | null = null
let mapMarker: L.Marker | null = null
let mapCircle: L.Circle | null = null
let pickingFor: 'geofence' | 'rule' | null = null

// ─── Station identity (callsign + home position) ──────────────────────────────
const stationCallsign = ref('')
const homeLatText = ref('')
const homeLonText = ref('')
const stationSaving = ref(false)
const stationSaved = ref(false)
const stationSaveError = ref('')

let stationHomeMap: L.Map | null = null
let stationHomeMarker: L.Marker | null = null

const CALLSIGN_REGEX = /^[A-Z0-9]{1,6}(-(\d|1[0-5]))?$/

const stationCallsignError = computed(() => {
  const cs = stationCallsign.value.trim().toUpperCase()
  if (!cs) return 'Callsign is required'
  return CALLSIGN_REGEX.test(cs) ? '' : 'Use BASE or BASE-SSID, e.g. W3UWU or W3UWU-10'
})

function parsedHome(): { lat: number; lon: number } | null {
  const lat = Number.parseFloat(homeLatText.value)
  const lon = Number.parseFloat(homeLonText.value)
  if (Number.isNaN(lat) || Number.isNaN(lon)) return null
  if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null
  return { lat, lon }
}

function setHomeFromMap(lat: number, lon: number) {
  homeLatText.value = lat.toFixed(6)
  homeLonText.value = lon.toFixed(6)
  syncHomeMarker()
}

function syncHomeMarker() {
  if (!stationHomeMap) return
  const pos = parsedHome()
  if (!pos) return
  if (stationHomeMarker) {
    stationHomeMarker.setLatLng([pos.lat, pos.lon])
  } else {
    // Same home pin the map uses — Leaflet's default marker PNGs aren't
    // bundled by Vite and render as a broken-image box.
    const icon = L.divIcon({
      html: `<div style="background:#1976D2;border-radius:50%;width:32px;height:32px;display:flex;align-items:center;justify-content:center;border:3px solid white;box-shadow:0 2px 6px rgba(0,0,0,0.5);"><span class='mdi mdi-home' style='color:white;font-size:18px;line-height:1;'></span></div>`,
      className: '',
      iconSize: [32, 32],
      iconAnchor: [16, 16],
    })
    stationHomeMarker = L.marker([pos.lat, pos.lon], { icon }).addTo(stationHomeMap)
  }
  stationHomeMap.panTo([pos.lat, pos.lon])
}

function initStationHomeMap() {
  const el = document.getElementById('station-home-map')
  if (!el) return
  if (stationHomeMap) {
    stationHomeMap.invalidateSize()
    return
  }
  const pos = parsedHome()
  stationHomeMap = L.map(el).setView(pos ? [pos.lat, pos.lon] : [39.0, -98.0], pos ? 11 : 4)
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap contributors',
    maxZoom: 19,
  }).addTo(stationHomeMap)
  stationHomeMap.on('click', (e: L.LeafletMouseEvent) => {
    setHomeFromMap(e.latlng.lat, e.latlng.lng)
  })
  if (pos) syncHomeMarker()
}

watch(
  activeTab,
  (tab) => {
    if (tab === 'station') {
      // The map container only exists once the tab's DOM is rendered.
      setTimeout(initStationHomeMap, 50)
    }
  },
  { immediate: true },
)

async function saveStationIdentity() {
  if (stationCallsignError.value) return
  const hasLatText = homeLatText.value.trim() !== '' || homeLonText.value.trim() !== ''
  const pos = parsedHome()
  if (hasLatText && !pos) {
    stationSaveError.value =
      'Home position must be a valid latitude (−90…90) and longitude (−180…180) pair.'
    return
  }
  stationSaving.value = true
  stationSaveError.value = ''
  try {
    await updateStationIdentity({
      callsign: stationCallsign.value.trim().toUpperCase(),
      homeLat: pos?.lat ?? null,
      homeLon: pos?.lon ?? null,
    })
    stationSaved.value = true
    setTimeout(() => (stationSaved.value = false), 2500)
    syncHomeMarker()
  } catch {
    stationSaveError.value = 'Save failed — check the values and that the backend is reachable.'
  } finally {
    stationSaving.value = false
  }
}

// ─── Database maintenance ─────────────────────────────────────────────────────
const dbSizeBytes = ref(0)
const cleanupIntervalHours = ref(0)
const vacuumOnCleanup = ref(true)
const lastCleanup = ref<CleanupResult | null>(null)
const cleanupRunning = ref(false)
const retentionRfDays = ref(0)
const retentionAprsIsDays = ref(14)
const retentionOwnDays = ref(0)
const retentionSaving = ref(false)
const retentionSaveError = ref('')
let cleanupPollTimer: ReturnType<typeof setInterval> | null = null

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let u = 0
  while (size >= 1024 && u < units.length - 1) {
    size /= 1024
    u++
  }
  return `${size.toFixed(1)} ${units[u]}`
}

async function loadMaintenance() {
  try {
    const s = await getMaintenanceStatus()
    dbSizeBytes.value = s.databaseSizeBytes
    cleanupIntervalHours.value = s.cleanupIntervalHours
    vacuumOnCleanup.value = s.vacuumOnCleanup
    lastCleanup.value = s.lastResult
    cleanupRunning.value = s.isRunning
    retentionRfDays.value = s.retention.rfDays
    retentionAprsIsDays.value = s.retention.aprsIsDays
    retentionOwnDays.value = s.retention.ownDays
    if (s.isRunning) startCleanupPolling()
  } catch {
    /* ignore */
  }
}

async function saveRetention() {
  retentionSaving.value = true
  retentionSaveError.value = ''
  try {
    await updateRetention({
      rfDays: Math.max(0, Math.floor(retentionRfDays.value || 0)),
      aprsIsDays: Math.max(0, Math.floor(retentionAprsIsDays.value || 0)),
      ownDays: Math.max(0, Math.floor(retentionOwnDays.value || 0)),
    })
  } catch {
    retentionSaveError.value = 'Failed to save retention settings.'
  } finally {
    retentionSaving.value = false
  }
}

function startCleanupPolling() {
  if (cleanupPollTimer) return
  cleanupPollTimer = setInterval(async () => {
    try {
      const s = await getMaintenanceStatus()
      cleanupRunning.value = s.isRunning
      dbSizeBytes.value = s.databaseSizeBytes
      lastCleanup.value = s.lastResult
      if (!s.isRunning) stopCleanupPolling()
    } catch {
      /* keep polling */
    }
  }, 1500)
}

function stopCleanupPolling() {
  if (cleanupPollTimer) {
    clearInterval(cleanupPollTimer)
    cleanupPollTimer = null
  }
}

// ─── Packet reprocessing ──────────────────────────────────────────────────────
const reprocessStatus = ref<ReprocessStatusDto | null>(null)
const reprocessStarting = ref(false)
const reprocessForce = ref(false)
let reprocessPollTimer: ReturnType<typeof setInterval> | null = null

async function loadReprocess() {
  try {
    reprocessStatus.value = await getReprocessStatus()
    if (reprocessStatus.value.isRunning) startReprocessPolling()
    else stopReprocessPolling()
  } catch {
    /* card shows last known state; maintenance load error is surfaced elsewhere */
  }
}

function startReprocessPolling() {
  if (reprocessPollTimer) return
  reprocessPollTimer = setInterval(loadReprocess, 1500)
}

function stopReprocessPolling() {
  if (reprocessPollTimer) {
    clearInterval(reprocessPollTimer)
    reprocessPollTimer = null
  }
}

const reprocessProgressPercent = computed(() => {
  const s = reprocessStatus.value
  if (!s || !s.isRunning || s.total === 0) return null
  return Math.min(100, (s.processed / s.total) * 100)
})

async function startReprocessNow() {
  reprocessStarting.value = true
  try {
    await startReprocess({ force: reprocessForce.value })
    showToast('Reprocess started — packets are being re-parsed in the background.', 'success')
    startReprocessPolling()
    await loadReprocess()
  } catch {
    showToast('Couldn’t start the reprocess — one may already be running.', 'error')
    await loadReprocess()
  } finally {
    reprocessStarting.value = false
  }
}

const cleanupConfirmOpen = ref(false)

/** Human summary of the retention windows, for the confirmation dialog. */
function retentionLabel(days: number): string {
  return days === 0 ? 'kept forever' : `deleted after ${days} day${days === 1 ? '' : 's'}`
}

async function runCleanupNow() {
  cleanupConfirmOpen.value = false
  cleanupRunning.value = true
  try {
    await runCleanup()
    startCleanupPolling()
  } catch {
    cleanupRunning.value = false
    showToast('Cleanup failed to start — the backend may be unreachable.', 'error')
  }
}

onMounted(async () => {
  try {
    const s = await getSettings()
    retrySettings.value = s
    stationCallsign.value = s.ourCallsign
    if (s.homePosition) {
      homeLatText.value = s.homePosition.lat.toFixed(6)
      homeLonText.value = s.homePosition.lon.toFixed(6)
    }
    outboundPath.value = s.outboundPath
    aprsIsEnabled.value = s.aprsIsEnabled
    aprsIsHost.value = s.aprsIsHost
    aprsIsPort.value = s.aprsIsPort
    aprsIsPasscodeOverride.value = s.aprsIsPasscodeOverride
    aprsIsPasscodeComputed.value = s.aprsIsPasscodeComputed
    aprsIsFilter.value = s.aprsIsFilter
    deduplicationWindowSeconds.value = s.deduplicationWindowSeconds
    loadRfServicesSettings(s)
    loadExternalTncSettings(s)
  } catch {
    /* ignore */
  }
  try {
    modemDevices.value = await getModemDevices()
  } catch {
    /* ignore */
  }
  try {
    const status = await getWeatherStatus()
    owmKeyConfigured.value = status.wind.available
    tomorrowKeyConfigured.value = status.lightning.available
    selectedRadarProvider.value = status.radarProvider as RadarProvider
    rvProKeyConfigured.value = status.rainViewerProKeyConfigured
  } catch {
    /* ignore */
  }
  await Promise.all([
    loadRadios(),
    loadGeofences(),
    loadRules(),
    loadMaintenance(),
    loadReprocess(),
  ])
})

onUnmounted(() => {
  stopCleanupPolling()
  stopReprocessPolling()
  if (map) {
    map.remove()
    map = null
  }
  if (stationHomeMap) {
    stationHomeMap.remove()
    stationHomeMap = null
    stationHomeMarker = null
  }
})

async function loadGeofences() {
  try {
    geofences.value = await getGeofences()
  } catch {
    /* */
  }
}
async function loadRules() {
  try {
    rules.value = await getProximityRules()
  } catch {
    /* */
  }
}

function initMap(
  containerId: string,
  forType: 'geofence' | 'rule',
  defaultLat: number,
  defaultLon: number,
) {
  if (map) {
    map.remove()
    map = null
    mapMarker = null
    mapCircle = null
  }
  pickingFor = forType
  const el = document.getElementById(containerId)
  if (!el) return

  map = L.map(el).setView([defaultLat, defaultLon], 11)
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap',
  }).addTo(map)

  map.on('click', (e: L.LeafletMouseEvent) => {
    const { lat, lng } = e.latlng
    if (pickingFor === 'geofence') {
      gfLat.value = Math.round(lat * 1000000) / 1000000
      gfLon.value = Math.round(lng * 1000000) / 1000000
    } else if (pickingFor === 'rule') {
      prLat.value = Math.round(lat * 1000000) / 1000000
      prLon.value = Math.round(lng * 1000000) / 1000000
    }
    if (mapMarker) map!.removeLayer(mapMarker)
    mapMarker = L.marker([lat, lng]).addTo(map!)
    if (mapCircle) map!.removeLayer(mapCircle)
    const r = pickingFor === 'geofence' ? gfRadius.value || 500 : prRadius.value || 1000
    mapCircle = L.circle([lat, lng], { radius: r, color: '#2196f3', fillOpacity: 0.12 }).addTo(map!)
  })
}

function openAddGeofence() {
  showAddGeofence.value = true
  gfName.value = ''
  gfLat.value = null
  gfLon.value = null
  gfRadius.value = 500
  gfAlertOnEnter.value = true
  gfAlertOnExit.value = true
  setTimeout(() => initMap('gf-map', 'geofence', 39.0, -98.0), 50)
}

function openAddRule() {
  showAddRule.value = true
  prName.value = ''
  prCallsign.value = ''
  prLat.value = null
  prLon.value = null
  prRadius.value = 1000
  setTimeout(() => initMap('pr-map', 'rule', 39.0, -98.0), 50)
}

async function saveGeofence() {
  if (!gfName.value || gfLat.value == null || gfLon.value == null) return
  gfSaving.value = true
  try {
    const gf = await createGeofence({
      name: gfName.value,
      centerLat: gfLat.value,
      centerLon: gfLon.value,
      radiusMeters: gfRadius.value,
      alertOnEnter: gfAlertOnEnter.value,
      alertOnExit: gfAlertOnExit.value,
    })
    geofences.value.push(gf)
    showAddGeofence.value = false
    if (map) {
      map.remove()
      map = null
    }
  } finally {
    gfSaving.value = false
  }
}

async function removeGeofence(id: number) {
  await deleteGeofence(id)
  geofences.value = geofences.value.filter((f) => f.id !== id)
}

async function saveRule() {
  if (!prName.value || prLat.value == null || prLon.value == null) return
  prSaving.value = true
  try {
    const rule = await createProximityRule({
      name: prName.value,
      targetCallsign: prCallsign.value.trim() || null,
      centerLat: prLat.value,
      centerLon: prLon.value,
      radiusMetres: prRadius.value,
    })
    rules.value.push(rule)
    showAddRule.value = false
    if (map) {
      map.remove()
      map = null
    }
  } finally {
    prSaving.value = false
  }
}

async function removeRule(id: number) {
  await deleteProximityRule(id)
  rules.value = rules.value.filter((r) => r.id !== id)
}

// ─── Confirm delete dialog ────────────────────────────────────────────────────
const deleteConfirmOpen = ref(false)
const deleteConfirmMessage = ref('')
let deleteConfirmAction: (() => Promise<void>) | null = null

function promptDeleteGeofence(id: number, name: string) {
  deleteConfirmMessage.value = `Delete geofence "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeGeofence(id)
  deleteConfirmOpen.value = true
}

function promptDeleteRule(id: number, name: string) {
  deleteConfirmMessage.value = `Delete proximity rule "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeRule(id)
  deleteConfirmOpen.value = true
}

async function confirmDelete() {
  if (deleteConfirmAction) await deleteConfirmAction()
  deleteConfirmOpen.value = false
  deleteConfirmAction = null
}
</script>

<template>
  <div class="settings-view">
    <v-tabs
      v-model="activeTab"
      direction="vertical"
      color="primary"
      density="comfortable"
      class="settings-nav"
    >
      <v-tab value="station" prepend-icon="mdi-account">Station</v-tab>
      <v-tab value="radios" prepend-icon="mdi-radio">Radios</v-tab>
      <v-tab value="rf" prepend-icon="mdi-radio-tower">RF Services</v-tab>
      <v-tab value="aprsis" prepend-icon="mdi-web">APRS-IS</v-tab>
      <v-tab value="map" prepend-icon="mdi-map">Map &amp; Weather</v-tab>
      <v-tab value="zones" prepend-icon="mdi-map-marker-radius">Alert Zones</v-tab>
      <v-tab value="maintenance" prepend-icon="mdi-database">Maintenance</v-tab>
    </v-tabs>

    <v-tabs-window v-model="activeTab" class="settings-content">
      <v-tabs-window-item value="station" class="pa-4">
        <div class="settings-grid">
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Station identity -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Station Identity</span>
              <v-fade-transition>
                <v-icon v-if="stationSaved" color="success" size="18" class="ml-2">
                  mdi-check-circle
                </v-icon>
              </v-fade-transition>
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <div class="text-subtitle-2 font-weight-medium mb-1">Callsign</div>
              <div class="text-caption text-medium-emphasis mb-2">
                Stamped on every transmitted packet and used for the APRS-IS login.
              </div>
              <v-text-field
                v-model="stationCallsign"
                density="compact"
                variant="outlined"
                placeholder="e.g. W3UWU-10"
                :error-messages="stationCallsignError || undefined"
                hide-details="auto"
                class="mb-4"
                style="max-width: 240px"
              />

              <div class="text-subtitle-2 font-weight-medium mb-1">Home position</div>
              <div class="text-caption text-medium-emphasis mb-2">
                Where your beacons say you are — also anchors range rings and distance columns.
                Click the map or type coordinates.
              </div>
              <div class="d-flex ga-4 flex-wrap mb-3">
                <div id="station-home-map" class="station-home-map" />
                <div class="d-flex flex-column ga-3" style="min-width: 200px">
                  <v-text-field
                    v-model="homeLatText"
                    label="Latitude"
                    density="compact"
                    variant="outlined"
                    hide-details
                    @change="syncHomeMarker"
                  />
                  <v-text-field
                    v-model="homeLonText"
                    label="Longitude"
                    density="compact"
                    variant="outlined"
                    hide-details
                    @change="syncHomeMarker"
                  />
                </div>
              </div>

              <v-alert
                v-if="stationSaveError"
                type="error"
                variant="tonal"
                density="compact"
                class="mb-3"
              >
                {{ stationSaveError }}
              </v-alert>

              <v-btn
                color="primary"
                variant="tonal"
                prepend-icon="mdi-content-save"
                :loading="stationSaving"
                :disabled="!!stationCallsignError"
                @click="saveStationIdentity"
              >
                Save station
              </v-btn>
            </v-card>

            <!-- ================================================================ -->
            <!-- Messaging -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Messaging</span>
              <v-progress-circular
                v-if="outboundPathSaving"
                indeterminate
                size="16"
                width="2"
                class="ml-3"
              />
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <div class="text-subtitle-2 font-weight-medium mb-1">Default outbound path</div>
              <v-text-field
                v-model="outboundPath"
                density="compact"
                variant="outlined"
                clearable
                :error-messages="outboundPathError"
                hide-details="auto"
                placeholder="e.g. WIDE1-1,WIDE2-1"
                class="mb-2"
                style="max-width: 360px"
                @update:model-value="schedulePathSave"
              />
              <div class="d-flex align-center flex-wrap gap-1 mb-3">
                <span class="text-caption text-medium-emphasis mr-1">Common paths:</span>
                <v-btn size="x-small" variant="tonal" @click="applyPathPreset('WIDE1-1,WIDE2-1')"
                  >WIDE1-1,WIDE2-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="applyPathPreset('WIDE2-1')"
                  >WIDE2-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="applyPathPreset('WIDE1-1')"
                  >WIDE1-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="applyPathPreset('')"
                  >Direct (no path)</v-btn
                >
              </div>
              <div class="text-body-2 text-medium-emphasis">
                Added to all outbound messages. <code>WIDE1-1,WIDE2-1</code> is recommended for most
                fixed and mobile stations. Leave blank to transmit direct with no digipeating.
              </div>
              <v-alert v-if="outboundPathSaveError" type="error" density="compact" class="mt-3">
                {{ outboundPathSaveError }}
              </v-alert>
            </v-card>
          </div>
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Message Retry -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Message Retry</span>
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <div class="text-body-2 text-medium-emphasis mb-3">
                Configure via <code>appsettings.json</code> or
                <code>appsettings.local.json</code> under the <code>DireControl</code> section.
              </div>
              <v-table density="compact">
                <tbody>
                  <tr>
                    <td class="text-body-2 font-weight-medium" style="width: 260px">
                      Max retry attempts
                    </td>
                    <td class="text-body-2">{{ retrySettings?.maxRetryAttempts ?? '—' }}</td>
                  </tr>
                  <tr>
                    <td class="text-body-2 font-weight-medium">Initial retry delay</td>
                    <td class="text-body-2">
                      {{
                        retrySettings != null
                          ? `${retrySettings.initialRetryDelaySeconds} seconds`
                          : '—'
                      }}
                    </td>
                  </tr>
                </tbody>
              </v-table>
            </v-card>
          </div>
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Units -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Units</span>
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <div class="d-flex align-center ga-6">
                <span class="text-body-2">Distance units</span>
                <v-radio-group
                  :model-value="distanceUnit"
                  inline
                  hide-details
                  density="compact"
                  @update:model-value="(v) => setDistanceUnit(v as 'km' | 'mi')"
                >
                  <v-radio label="Kilometres" value="km" />
                  <v-radio label="Miles" value="mi" />
                </v-radio-group>
              </div>
            </v-card>
          </div>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="radios" class="pa-4">
        <div class="settings-single">
          <!-- Radios -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">Radios</span>
            <v-spacer />
            <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddRadio">
              Add Radio
            </v-btn>
          </div>
          <v-card variant="outlined" class="mb-6">
            <v-table v-if="radios.length > 0" density="comfortable">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Callsign</th>
                  <th>Ch</th>
                  <th>Frequency</th>
                  <th>Modem</th>
                  <th>Beacons</th>
                  <th>Status</th>
                  <th class="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="radio in radios" :key="radio.id">
                  <td>
                    <div class="d-flex align-center ga-2">
                      <div
                        v-if="radio.beaconSymbol && radio.beaconSymbol.length >= 2"
                        :style="getSymbolStyle(radio.beaconSymbol[0]!, radio.beaconSymbol[1]!)"
                        class="radio-symbol-icon"
                      />
                      {{ radio.name }}
                    </div>
                  </td>
                  <td class="font-weight-medium">{{ radio.fullCallsign }}</td>
                  <td>{{ radio.channelNumber }}</td>
                  <td>
                    <template v-if="radio.frequencyMhz">
                      {{ radio.frequencyMhz.toFixed(3) }} MHz<span
                        v-if="radio.mode"
                        class="text-medium-emphasis"
                      >
                        {{ radio.mode }}</span
                      >
                    </template>
                    <span v-else class="text-medium-emphasis">—</span>
                  </td>
                  <td>
                    <v-chip
                      v-if="radio.modem.modemEnabled"
                      size="x-small"
                      variant="tonal"
                      color="green"
                    >
                      {{ radio.modem.txEnabled ? 'RX/TX' : 'RX' }}
                    </v-chip>
                    <span v-else class="text-medium-emphasis">—</span>
                  </td>
                  <td>{{ radio.beaconCount }}</td>
                  <td>
                    <v-chip
                      :color="radio.isActive ? 'green' : 'grey'"
                      size="x-small"
                      variant="tonal"
                      class="cursor-pointer"
                      @click="toggleActive(radio.id)"
                    >
                      {{ radio.isActive ? 'Active' : 'Inactive' }}
                    </v-chip>
                  </td>
                  <td class="text-right" style="white-space: nowrap">
                    <v-btn
                      icon="mdi-access-point"
                      size="x-small"
                      variant="text"
                      color="primary"
                      title="Beacon now"
                      :loading="beaconing[radio.id]"
                      :disabled="!radio.isActive"
                      @click="doBeaconNow(radio)"
                    />
                    <v-btn
                      icon="mdi-pencil"
                      size="x-small"
                      variant="text"
                      @click="openEditRadio(radio)"
                    />
                    <v-btn
                      icon="mdi-close"
                      size="x-small"
                      variant="text"
                      color="error"
                      @click="promptDeleteRadio(radio)"
                    />
                  </td>
                </tr>
              </tbody>
            </v-table>
            <div v-else class="text-center text-medium-emphasis py-4">No radios configured</div>
          </v-card>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="rf" class="pa-4">
        <div class="settings-single">
          <!-- ================================================================ -->
          <!-- RF Services -->
          <!-- ================================================================ -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">RF Services</span>
          </div>

          <v-card variant="outlined" class="mb-6 pa-4">
            <!-- Digipeater -->
            <div class="text-body-2 font-weight-medium mb-1">Digipeater</div>
            <v-switch
              v-model="digipeaterEnabled"
              label="Enable WIDEn-N digipeater"
              hide-details
              density="compact"
            />
            <div v-if="digipeaterEnabled" class="d-flex ga-4 align-center flex-wrap mb-2 mt-1">
              <v-text-field
                v-model.number="digipeaterMaxWideN"
                label="Max WIDEn"
                density="compact"
                type="number"
                style="max-width: 140px"
                hint="Larger n is trapped"
                persistent-hint
              />
              <v-checkbox
                v-model="digipeaterFillInOnly"
                label="Fill-in only (WIDE1-1)"
                hide-details
                density="compact"
              />
            </div>

            <v-divider class="my-4" />

            <!-- KISS server -->
            <div class="text-body-2 font-weight-medium mb-1">KISS TCP Server</div>
            <div class="text-caption text-medium-emphasis mb-1">
              Lets other applications use DireControl as their TNC.
            </div>
            <div class="d-flex ga-4 align-center flex-wrap">
              <v-switch
                v-model="kissServerEnabled"
                label="Enable KISS server"
                hide-details
                density="compact"
              />
              <v-text-field
                v-if="kissServerEnabled"
                v-model.number="kissServerPort"
                label="Port"
                density="compact"
                type="number"
                style="max-width: 140px"
              />
            </div>

            <v-divider class="my-4" />

            <!-- iGate -->
            <div class="text-body-2 font-weight-medium mb-1">iGate</div>
            <div class="text-caption text-medium-emphasis mb-1">
              Requires APRS-IS to be enabled with a valid passcode.
            </div>
            <v-switch
              v-model="rfToIsGatingEnabled"
              label="Gate RF → APRS-IS (qAR)"
              hide-details
              density="compact"
            />
            <v-switch
              v-model="isToRfGatingEnabled"
              label="Gate APRS-IS messages → RF (third-party format)"
              hide-details
              density="compact"
            />
            <div v-if="isToRfGatingEnabled" class="d-flex ga-4 align-center flex-wrap mb-2 mt-1">
              <v-text-field
                v-model="isToRfPath"
                label="IS→RF path"
                density="compact"
                style="max-width: 240px"
                hint="Empty = direct"
                persistent-hint
              />
              <v-text-field
                v-model.number="isToRfRecentHeardMinutes"
                label="Heard-on-RF window (min)"
                density="compact"
                type="number"
                style="max-width: 200px"
              />
            </div>

            <div class="d-flex align-center ga-3 mt-4">
              <v-btn
                size="small"
                color="primary"
                prepend-icon="mdi-content-save"
                :loading="rfServicesSaving"
                @click="saveRfServices"
              >
                Save RF Services
              </v-btn>
              <v-fade-transition>
                <span v-if="rfServicesSaveSuccess" class="text-caption text-success">
                  <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
                </span>
              </v-fade-transition>
            </div>
            <v-alert v-if="rfServicesSaveError" type="error" density="compact" class="mt-3">
              {{ rfServicesSaveError }}
            </v-alert>
          </v-card>

          <!-- ================================================================ -->
          <!-- External TNC (KISS TCP client, e.g. Direwolf) -->
          <!-- ================================================================ -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">External TNC</span>
          </div>

          <v-card variant="outlined" class="mb-6 pa-4">
            <v-switch
              v-model="direwolfEnabled"
              label="Connect to an external KISS TNC (e.g. Direwolf) over TCP"
              hide-details
              density="compact"
            />
            <div class="text-caption text-medium-emphasis mb-2">
              Leave off when using the native sound modem — otherwise DireControl keeps trying to
              reach a TNC that isn't there.
            </div>
            <div v-if="direwolfEnabled" class="d-flex ga-4 align-center flex-wrap mt-1">
              <v-text-field
                v-model="direwolfHost"
                label="Host"
                density="compact"
                placeholder="localhost"
                style="max-width: 220px"
              />
              <v-text-field
                v-model.number="direwolfPort"
                label="Port"
                density="compact"
                type="number"
                style="max-width: 140px"
              />
              <v-text-field
                v-model.number="direwolfReconnectDelaySeconds"
                label="Reconnect delay (s)"
                density="compact"
                type="number"
                style="max-width: 180px"
              />
            </div>

            <div class="d-flex align-center ga-3 mt-4">
              <v-btn
                size="small"
                color="primary"
                prepend-icon="mdi-content-save"
                :loading="externalTncSaving"
                @click="saveExternalTnc"
              >
                Save External TNC
              </v-btn>
              <v-fade-transition>
                <span v-if="externalTncSaveSuccess" class="text-caption text-success">
                  <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
                </span>
              </v-fade-transition>
            </div>
            <v-alert v-if="externalTncSaveError" type="error" density="compact" class="mt-3">
              {{ externalTncSaveError }}
            </v-alert>
          </v-card>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="aprsis" class="pa-4">
        <div class="settings-single">
          <!-- ================================================================ -->
          <!-- APRS-IS -->
          <!-- ================================================================ -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">APRS-IS</span>
          </div>

          <v-card variant="outlined" class="mb-6 pa-4">
            <v-switch
              v-model="aprsIsEnabled"
              label="Enable APRS-IS connection"
              hide-details
              density="compact"
              class="mb-4"
            />

            <div class="d-flex ga-2 mb-2">
              <v-text-field v-model="aprsIsHost" label="Server" density="compact" style="flex: 3" />
              <v-text-field
                v-model.number="aprsIsPort"
                label="Port"
                density="compact"
                type="number"
                style="flex: 1"
              />
            </div>

            <div class="text-body-2 text-medium-emphasis mb-1">
              Passcode (auto-computed: <strong>{{ aprsIsPasscodeComputed }}</strong
              >)
            </div>
            <v-text-field
              v-model.number="aprsIsPasscodeOverride"
              label="Passcode override (leave blank to use auto-computed)"
              density="compact"
              type="number"
              clearable
              class="mb-2"
              style="max-width: 360px"
            />

            <v-text-field
              v-model="aprsIsFilter"
              label="Server-side filter"
              density="compact"
              class="mb-2"
              hint="e.g. r/39.0/-98.0/500 t/m — restricts what packets the server sends to you"
              persistent-hint
            />

            <v-text-field
              v-model.number="deduplicationWindowSeconds"
              label="Deduplication window (seconds)"
              density="compact"
              type="number"
              class="mb-3 mt-2"
              style="max-width: 240px"
              hint="Packets with the same callsign and info field within this window are counted as duplicates"
              persistent-hint
            />

            <div class="d-flex align-center ga-3 mt-4">
              <v-btn
                size="small"
                color="primary"
                prepend-icon="mdi-content-save"
                :loading="aprsIsSaving"
                @click="saveAprsIsSettings"
              >
                Save APRS-IS Settings
              </v-btn>
              <v-fade-transition>
                <span v-if="aprsIsSaveSuccess" class="text-caption text-success">
                  <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
                </span>
              </v-fade-transition>
            </div>
            <v-alert v-if="aprsIsSaveError" type="error" density="compact" class="mt-3">
              {{ aprsIsSaveError }}
            </v-alert>
          </v-card>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="map" class="pa-4">
        <div class="settings-grid">
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- API Keys -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Map API Keys</span>
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <div class="text-body-2 text-medium-emphasis mb-4">
                API keys are stored only in your browser's local storage and are never sent to the
                server.
              </div>
              <v-text-field
                v-model="jawgApiKey"
                label="Jawg Maps API key"
                density="compact"
                :type="showJawgKey ? 'text' : 'password'"
                :append-inner-icon="showJawgKey ? 'mdi-eye-off' : 'mdi-eye'"
                hint="Required for the Jawg Dark tile provider. Get a free key at jawg.io."
                persistent-hint
                class="mb-3"
                @click:append-inner="showJawgKey = !showJawgKey"
              />
              <div class="d-flex align-center ga-3 mt-2">
                <v-btn
                  size="small"
                  color="primary"
                  prepend-icon="mdi-content-save"
                  @click="saveApiKeys"
                >
                  Save Keys
                </v-btn>
                <v-fade-transition>
                  <span v-if="apiKeySaved" class="text-caption text-success">
                    <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
                  </span>
                </v-fade-transition>
              </div>
            </v-card>
          </div>
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Weather Overlays -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Weather Overlays</span>
            </div>

            <v-card variant="outlined" class="mb-6 pa-4">
              <!-- Radar provider selector -->
              <div class="text-body-2 font-weight-medium mb-2">Rainfall Radar Provider</div>
              <v-select
                v-model="selectedRadarProvider"
                :items="[
                  { title: 'IEM NEXRAD — Free · US · zoom 8 · 5-min updates', value: 0 },
                  { title: 'RainViewer — Free · Global · zoom 7 · 10-min updates', value: 1 },
                  {
                    title: 'RainViewer Pro — $40/yr · Global · zoom 12 · 10-min updates',
                    value: 2,
                  },
                ]"
                item-title="title"
                item-value="value"
                density="compact"
                class="mb-3"
              />

              <!-- RainViewer Pro API key — shown only when Pro is selected -->
              <template v-if="selectedRadarProvider === 2">
                <div class="text-body-2 font-weight-medium mb-2">RainViewer Pro API Key</div>
                <v-text-field
                  v-model="rainViewerProApiKey"
                  label="RainViewer Pro API Key"
                  density="compact"
                  :type="showRainViewerProKey ? 'text' : 'password'"
                  :append-inner-icon="showRainViewerProKey ? 'mdi-eye-off' : 'mdi-eye'"
                  :placeholder="
                    rvProKeyConfigured ? 'Key saved — enter a new value to replace' : ''
                  "
                  class="mb-1"
                  :prepend-inner-icon="
                    rainViewerProApiKey.trim() || rvProKeyConfigured
                      ? 'mdi-check-circle'
                      : 'mdi-alert-circle-outline'
                  "
                  :color="rainViewerProApiKey.trim() || rvProKeyConfigured ? 'success' : 'warning'"
                  @click:append-inner="showRainViewerProKey = !showRainViewerProKey"
                />
                <div class="text-caption text-medium-emphasis mb-4">
                  Get a key at
                  <a href="https://www.rainviewer.com" target="_blank" rel="noopener"
                    >rainviewer.com</a
                  >
                </div>
              </template>

              <v-divider class="mb-4" />

              <!-- OpenWeatherMap wind key -->
              <div class="text-body-2 font-weight-medium mb-2">Wind Layer (OpenWeatherMap)</div>
              <v-text-field
                v-model="owmApiKey"
                label="OpenWeatherMap API Key"
                density="compact"
                :type="showOwmKey ? 'text' : 'password'"
                :append-inner-icon="showOwmKey ? 'mdi-eye-off' : 'mdi-eye'"
                :placeholder="owmKeyConfigured ? 'Key saved — enter a new value to replace' : ''"
                class="mb-1"
                :prepend-inner-icon="
                  owmApiKey.trim() || owmKeyConfigured
                    ? 'mdi-check-circle'
                    : 'mdi-alert-circle-outline'
                "
                :color="owmApiKey.trim() || owmKeyConfigured ? 'success' : 'warning'"
                @click:append-inner="showOwmKey = !showOwmKey"
              />
              <div class="text-caption text-medium-emphasis mb-4">
                Get a free key at
                <a href="https://openweathermap.org/api" target="_blank" rel="noopener"
                  >openweathermap.org</a
                >
              </div>

              <!-- Tomorrow.io lightning key -->
              <div class="text-body-2 font-weight-medium mb-2">Lightning (Tomorrow.io)</div>
              <v-text-field
                v-model="tomorrowIoApiKey"
                label="Tomorrow.io API Key"
                density="compact"
                :type="showTomorrowKey ? 'text' : 'password'"
                :append-inner-icon="showTomorrowKey ? 'mdi-eye-off' : 'mdi-eye'"
                :placeholder="
                  tomorrowKeyConfigured ? 'Key saved — enter a new value to replace' : ''
                "
                class="mb-1"
                :prepend-inner-icon="
                  tomorrowIoApiKey.trim() || tomorrowKeyConfigured
                    ? 'mdi-check-circle'
                    : 'mdi-alert-circle-outline'
                "
                :color="tomorrowIoApiKey.trim() || tomorrowKeyConfigured ? 'success' : 'warning'"
                @click:append-inner="showTomorrowKey = !showTomorrowKey"
              />
              <div class="text-caption text-medium-emphasis mb-4">
                Get a free key at
                <a href="https://www.tomorrow.io" target="_blank" rel="noopener">tomorrow.io</a>
              </div>

              <v-alert v-if="weatherKeysSaveError" type="error" density="compact" class="mb-3">
                {{ weatherKeysSaveError }}
              </v-alert>

              <div class="d-flex align-center ga-3">
                <v-btn
                  size="small"
                  color="primary"
                  prepend-icon="mdi-content-save"
                  :loading="weatherKeysSaving"
                  @click="saveWeatherApiKeys"
                >
                  Save Keys
                </v-btn>
                <v-fade-transition>
                  <span v-if="weatherKeysSaveSuccess" class="text-caption text-success">
                    <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
                  </span>
                </v-fade-transition>
              </div>
            </v-card>
          </div>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="zones" class="pa-4">
        <div class="settings-grid">
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Geofences -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Geofences</span>
              <v-spacer />
              <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddGeofence">
                Add Geofence
              </v-btn>
            </div>

            <v-card variant="outlined" class="mb-6">
              <div v-if="geofences.length === 0" class="text-center text-medium-emphasis py-4">
                No geofences defined
              </div>
              <v-list v-else density="compact">
                <v-list-item v-for="gf in geofences" :key="gf.id">
                  <template #prepend>
                    <v-icon :color="gf.isActive ? 'green' : 'grey'" size="20"
                      >mdi-map-marker-radius</v-icon
                    >
                  </template>
                  <v-list-item-title>{{ gf.name }}</v-list-item-title>
                  <v-list-item-subtitle>
                    {{ gf.centerLat.toFixed(5) }}, {{ gf.centerLon.toFixed(5) }} —
                    {{ formatDistance(gf.radiusMeters / 1000) }}
                    <span v-if="gf.alertOnEnter" class="ml-1 text-caption">↓Enter</span>
                    <span v-if="gf.alertOnExit" class="ml-1 text-caption">↑Exit</span>
                  </v-list-item-subtitle>
                  <template #append>
                    <v-btn
                      icon="mdi-delete"
                      size="x-small"
                      variant="text"
                      color="error"
                      @click="promptDeleteGeofence(gf.id, gf.name)"
                    />
                  </template>
                </v-list-item>
              </v-list>
            </v-card>
          </div>
          <div class="settings-section">
            <!-- ================================================================ -->
            <!-- Proximity Rules -->
            <!-- ================================================================ -->
            <div class="section-header d-flex align-center mb-2">
              <span class="text-h6">Proximity Rules</span>
              <v-spacer />
              <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddRule">
                Add Rule
              </v-btn>
            </div>

            <v-card variant="outlined">
              <div v-if="rules.length === 0" class="text-center text-medium-emphasis py-4">
                No proximity rules defined
              </div>
              <v-list v-else density="compact">
                <v-list-item v-for="rule in rules" :key="rule.id">
                  <template #prepend>
                    <v-icon :color="rule.isActive ? 'blue' : 'grey'" size="20">mdi-radar</v-icon>
                  </template>
                  <v-list-item-title>{{ rule.name }}</v-list-item-title>
                  <v-list-item-subtitle>
                    <span v-if="rule.targetCallsign">{{ rule.targetCallsign }} — </span>
                    {{ rule.centerLat.toFixed(5) }}, {{ rule.centerLon.toFixed(5) }} —
                    {{ formatDistance(rule.radiusMetres / 1000) }}
                  </v-list-item-subtitle>
                  <template #append>
                    <v-btn
                      icon="mdi-delete"
                      size="x-small"
                      variant="text"
                      color="error"
                      @click="promptDeleteRule(rule.id, rule.name)"
                    />
                  </template>
                </v-list-item>
              </v-list>
            </v-card>
          </div>
        </div>
      </v-tabs-window-item>

      <v-tabs-window-item value="maintenance" class="pa-4">
        <div class="settings-single">
          <!-- ================================================================ -->
          <!-- Database maintenance -->
          <!-- ================================================================ -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">Database Maintenance</span>
          </div>

          <v-card variant="outlined" class="mb-6 pa-4">
            <div class="d-flex align-center mb-4">
              <div>
                <div class="text-caption text-medium-emphasis">Database size</div>
                <div class="text-h6">{{ formatBytes(dbSizeBytes) }}</div>
              </div>
              <v-spacer />
              <v-btn
                color="primary"
                :loading="cleanupRunning"
                :disabled="cleanupRunning"
                prepend-icon="mdi-broom"
                @click="cleanupConfirmOpen = true"
              >
                {{ cleanupRunning ? 'Cleaning…' : 'Run Cleanup Now' }}
              </v-btn>
            </div>

            <div class="text-body-2 mb-2">
              Packet retention — how long to keep received packets before pruning.
              <strong>0 = keep forever.</strong>
            </div>

            <div class="d-flex flex-wrap ga-4 mb-1">
              <v-text-field
                v-model.number="retentionRfDays"
                type="number"
                min="0"
                label="RF (days)"
                density="compact"
                variant="outlined"
                hide-details
                style="max-width: 160px"
              />
              <v-text-field
                v-model.number="retentionAprsIsDays"
                type="number"
                min="0"
                label="APRS-IS (days)"
                density="compact"
                variant="outlined"
                hide-details
                style="max-width: 160px"
              />
              <v-text-field
                v-model.number="retentionOwnDays"
                type="number"
                min="0"
                label="Own (days)"
                density="compact"
                variant="outlined"
                hide-details
                style="max-width: 160px"
              />
              <v-btn
                variant="tonal"
                color="primary"
                :loading="retentionSaving"
                prepend-icon="mdi-content-save"
                @click="saveRetention"
              >
                Save
              </v-btn>
            </div>

            <v-alert
              v-if="retentionSaveError"
              type="error"
              variant="tonal"
              density="compact"
              class="mt-2"
            >
              {{ retentionSaveError }}
            </v-alert>

            <div class="text-caption text-medium-emphasis mt-3">
              Cleanup runs automatically
              <template v-if="cleanupIntervalHours > 0">every {{ cleanupIntervalHours }}h</template>
              <template v-else>only when triggered manually</template>, and
              {{ vacuumOnCleanup ? 'reclaims freed space (VACUUM)' : 'does not VACUUM' }} after
              pruning.
            </div>

            <v-divider class="my-3" />

            <div class="text-caption text-medium-emphasis mb-1">Last cleanup</div>
            <div v-if="!lastCleanup" class="text-body-2 text-medium-emphasis">
              No cleanup has run yet.
            </div>
            <div v-else-if="lastCleanup.error" class="text-body-2 text-error">
              Failed: {{ lastCleanup.error }}
            </div>
            <div v-else class="text-body-2">
              Deleted
              <strong>{{
                (
                  lastCleanup.rfDeleted +
                  lastCleanup.aprsIsDeleted +
                  lastCleanup.ownDeleted
                ).toLocaleString()
              }}</strong>
              packets ({{ lastCleanup.aprsIsDeleted.toLocaleString() }} APRS-IS,
              {{ lastCleanup.rfDeleted.toLocaleString() }} RF,
              {{ lastCleanup.ownDeleted.toLocaleString() }} Own) ·
              {{ formatBytes(lastCleanup.sizeBeforeBytes) }} →
              {{ formatBytes(lastCleanup.sizeAfterBytes) }}
              <span v-if="lastCleanup.vacuumed" class="text-success">· vacuumed</span>
              <span v-else-if="lastCleanup.vacuumError" class="text-warning"
                >· VACUUM skipped (busy)</span
              >
              <span class="text-medium-emphasis">
                · {{ new Date(lastCleanup.completedAt).toLocaleString() }}</span
              >
            </div>
          </v-card>

          <!-- ================================================================ -->
          <!-- Packet reprocessing -->
          <!-- ================================================================ -->
          <div class="section-header d-flex align-center mb-2">
            <span class="text-h6">Packet Reprocessing</span>
          </div>

          <v-card variant="outlined" class="mb-6 pa-4">
            <div class="text-body-2 mb-3">
              Re-derives positions, weather, telemetry, and resolved paths from the stored raw
              packets — useful after a parser fix. Runs in the background; parser version
              <strong>{{ reprocessStatus?.currentParserVersion ?? '…' }}</strong
              >.
            </div>

            <template v-if="reprocessStatus?.isRunning">
              <v-progress-linear
                :model-value="reprocessProgressPercent ?? 0"
                :indeterminate="reprocessProgressPercent === null"
                color="primary"
                height="8"
                rounded
                class="mb-2"
              />
              <div class="text-caption text-medium-emphasis mb-2">
                {{ reprocessStatus.processed.toLocaleString() }}
                <template v-if="reprocessStatus.total > 0">
                  of {{ reprocessStatus.total.toLocaleString() }}
                </template>
                packets reprocessed…
              </div>
            </template>

            <div class="d-flex align-center ga-4 flex-wrap">
              <v-switch
                v-model="reprocessForce"
                label="Force — re-parse every packet, not just outdated ones"
                density="compact"
                hide-details
                color="primary"
                :disabled="reprocessStatus?.isRunning"
              />
              <v-btn
                color="primary"
                variant="tonal"
                prepend-icon="mdi-cog-refresh"
                :loading="reprocessStarting"
                :disabled="reprocessStatus?.isRunning"
                @click="startReprocessNow"
              >
                {{ reprocessStatus?.isRunning ? 'Running…' : 'Start Reprocess' }}
              </v-btn>
            </div>

            <template v-if="reprocessStatus?.lastResult">
              <v-divider class="my-3" />
              <div class="text-caption text-medium-emphasis mb-1">Last run</div>
              <div v-if="reprocessStatus.lastResult.error" class="text-body-2 text-error">
                Failed: {{ reprocessStatus.lastResult.error }}
              </div>
              <div v-else class="text-body-2">
                Reprocessed
                <strong>{{ reprocessStatus.lastResult.processed.toLocaleString() }}</strong>
                packets ({{ reprocessStatus.lastResult.failed.toLocaleString() }} failed<template
                  v-if="reprocessStatus.lastResult.orphanStationsDeleted > 0"
                >
                  · {{ reprocessStatus.lastResult.orphanStationsDeleted.toLocaleString() }} orphan
                  stations removed</template
                >)
                <span class="text-medium-emphasis">
                  · {{ new Date(reprocessStatus.lastResult.completedAt).toLocaleString() }}</span
                >
              </div>
            </template>
          </v-card>
        </div>
      </v-tabs-window-item>
    </v-tabs-window>

    <!-- ── Dialogs ── -->
    <!-- Add / Edit Radio dialog -->
    <v-dialog v-model="radioDialogOpen" max-width="880" scrollable>
      <v-card>
        <v-card-title class="d-flex align-center ga-2">
          {{ editingRadioId ? 'Edit Radio' : 'Add Radio' }}
          <span v-if="computedFullCallsign" class="text-caption text-medium-emphasis">
            {{ computedFullCallsign }}
          </span>
        </v-card-title>
        <v-divider />
        <v-card-text>
          <!-- Duplicate warning -->
          <v-alert
            v-if="duplicateRadio"
            type="warning"
            variant="tonal"
            density="compact"
            class="mb-3"
          >
            {{ computedFullCallsign }} is already configured as "{{ duplicateRadio.name }}". Are you
            sure?
          </v-alert>

          <!-- ── Identity ── -->
          <div class="radio-form-section">
            <div class="text-subtitle-2 font-weight-medium">Identity</div>
            <div class="text-caption text-medium-emphasis mb-2">
              How this radio appears in the app and on the air.
            </div>
            <div class="radio-form-row">
              <v-text-field
                v-model="rName"
                label="Name *"
                density="compact"
                :rules="[(v: string) => v.trim().length > 0 || 'Required']"
              />
              <v-text-field
                v-model="rCallsign"
                label="Callsign *"
                density="compact"
                :rules="[(v: string) => /^[A-Z0-9]{3,6}$/i.test(v.trim()) || '3–6 letters/digits']"
              />
              <v-text-field
                v-model="rSsid"
                label="SSID"
                density="compact"
                :error-messages="ssidError || undefined"
                placeholder="0–15"
              />
              <v-text-field
                v-model.number="rChannel"
                label="KISS channel"
                density="compact"
                type="number"
                :rules="[(v: number) => (v >= 0 && v <= 15) || '0–15']"
                hint="Most single-radio setups use 0"
                persistent-hint
              />
            </div>
          </div>

          <!-- ── Frequency ── -->
          <div class="radio-form-section">
            <div class="text-subtitle-2 font-weight-medium">Frequency</div>
            <div class="text-caption text-medium-emphasis mb-2">
              Used for labels and the frequencies table — not rig control.
            </div>
            <div class="radio-form-row">
              <v-text-field
                v-model.number="rFrequencyMhz"
                label="Frequency (MHz)"
                density="compact"
                type="number"
                step="0.005"
                placeholder="e.g. 144.390"
              />
              <v-text-field v-model="rMode" label="Mode" density="compact" placeholder="e.g. FM" />
              <v-text-field
                v-model.number="rExpectedInterval"
                label="Expected beacon interval (s)"
                density="compact"
                type="number"
              />
              <v-text-field v-model="rNotes" label="Notes" density="compact" />
            </div>
          </div>

          <!-- ── Beaconing ── -->
          <div class="radio-form-section">
            <div class="text-subtitle-2 font-weight-medium">Beaconing</div>
            <div class="text-caption text-medium-emphasis mb-2">
              Position beacons transmitted as this radio.
            </div>
            <div class="radio-form-row">
              <v-text-field
                v-model="rBeaconPath"
                label="Beacon path"
                density="compact"
                placeholder="e.g. WIDE1-1,WIDE2-1"
                hint="Leave blank for direct (no digipeating)"
                persistent-hint
              />
              <v-text-field v-model="rBeaconComment" label="Comment" density="compact" />
            </div>
            <div class="d-flex align-start ga-4 flex-wrap mt-1">
              <div style="flex: 1 1 260px; min-width: 240px">
                <AprsSymbolPicker v-model="rBeaconSymbol" />
              </div>
              <div style="flex: 1 1 260px; min-width: 240px">
                <v-switch
                  v-model="rAutoBeaconEnabled"
                  label="Automatically beacon on a schedule"
                  hide-details
                  density="compact"
                  color="primary"
                  class="mb-1"
                />
                <v-text-field
                  v-if="rAutoBeaconEnabled"
                  v-model.number="rAutoBeaconInterval"
                  label="Auto-beacon interval (seconds)"
                  density="compact"
                  type="number"
                  :rules="[(v: number) => v >= 60 || 'Minimum 60 seconds']"
                  hint="Requires home position and a TX-enabled modem"
                  persistent-hint
                />
              </div>
            </div>
          </div>

          <!-- ── Sound modem ── -->
          <div class="radio-form-section">
            <div class="text-subtitle-2 font-weight-medium">Sound Modem</div>
            <div class="text-caption text-medium-emphasis mb-2">
              Native AFSK decode/transmit over this radio's audio feed.
            </div>
            <v-switch
              v-model="rModem.modemEnabled"
              label="Decode RF for this radio with the native modem"
              hide-details
              density="compact"
              color="primary"
              class="mb-2"
            />
            <template v-if="rModem.modemEnabled">
              <div class="radio-form-row">
                <v-combobox
                  v-model="rModem.modemCaptureDevice"
                  :items="modemCaptureDeviceItems"
                  label="Capture device"
                  density="compact"
                  :return-object="false"
                />
              </div>
              <v-switch
                v-model="rModem.txEnabled"
                label="Transmit (beacons, messages, digipeats)"
                hide-details
                density="compact"
                color="primary"
                class="mb-2"
              />
              <template v-if="rModem.txEnabled">
                <div class="radio-form-row">
                  <v-combobox
                    v-model="rModem.modemPlaybackDevice"
                    :items="modemPlaybackDeviceItems"
                    label="Playback device"
                    density="compact"
                    :return-object="false"
                  />
                  <v-text-field
                    v-model.number="rModem.txAudioLevelPct"
                    label="TX level (%)"
                    density="compact"
                    type="number"
                  />
                </div>
              </template>
            </template>
          </div>

          <!-- ── PTT ── -->
          <div v-if="rModem.modemEnabled && rModem.txEnabled" class="radio-form-section">
            <div class="text-subtitle-2 font-weight-medium">PTT</div>
            <div class="text-caption text-medium-emphasis mb-2">
              How transmit is keyed — the fields follow the chosen method.
            </div>
            <div class="radio-form-row">
              <v-select
                v-model="rModem.pttMethod"
                :items="pttMethodItems"
                label="PTT method"
                density="compact"
              />
              <template v-if="rModem.pttMethod === PttMethods.SerialRtsDtr">
                <v-combobox
                  v-model="rModem.pttSerialPort"
                  :items="modemDevices.serialPorts"
                  label="Serial port"
                  density="compact"
                  :return-object="false"
                />
                <div class="d-flex align-center ga-2">
                  <v-checkbox
                    v-model="rModem.pttSerialUseRts"
                    label="RTS"
                    hide-details
                    density="compact"
                  />
                  <v-checkbox
                    v-model="rModem.pttSerialUseDtr"
                    label="DTR"
                    hide-details
                    density="compact"
                  />
                </div>
              </template>
              <template v-else-if="rModem.pttMethod === PttMethods.Cm108">
                <v-combobox
                  v-model="rModem.pttHidDevice"
                  :items="modemHidDeviceItems"
                  label="HID device"
                  density="compact"
                  :return-object="false"
                />
                <v-text-field
                  v-model.number="rModem.pttHidPin"
                  label="Pin"
                  density="compact"
                  type="number"
                />
              </template>
              <template v-else-if="rModem.pttMethod === PttMethods.Gpio">
                <v-text-field
                  v-model.number="rModem.pttGpioChip"
                  label="Chip"
                  density="compact"
                  type="number"
                />
                <v-text-field
                  v-model.number="rModem.pttGpioLine"
                  label="Line"
                  density="compact"
                  type="number"
                />
                <v-checkbox
                  v-model="rModem.pttGpioActiveLow"
                  label="Active low"
                  hide-details
                  density="compact"
                />
              </template>
              <template v-else-if="rModem.pttMethod === PttMethods.Rigctld">
                <v-text-field
                  v-model="rModem.pttRigctldHost"
                  label="rigctld host"
                  density="compact"
                />
                <v-text-field
                  v-model.number="rModem.pttRigctldPort"
                  label="Port"
                  density="compact"
                  type="number"
                />
              </template>
            </div>

            <!-- Advanced TX timing — defaults suit most rigs -->
            <v-btn
              size="x-small"
              variant="text"
              color="primary"
              class="px-1"
              @click="showTxTiming = !showTxTiming"
            >
              {{ showTxTiming ? 'Hide advanced TX timing ▴' : 'Show advanced TX timing ▾' }}
            </v-btn>
            <div v-if="showTxTiming" class="radio-form-row mt-2">
              <v-text-field
                v-model.number="rModem.txDelayMs"
                label="TX delay (ms)"
                density="compact"
                type="number"
                hint="Keyed carrier before data"
                persistent-hint
              />
              <v-text-field
                v-model.number="rModem.txTailMs"
                label="Tail (ms)"
                density="compact"
                type="number"
                hint="Carrier after data"
                persistent-hint
              />
              <v-text-field
                v-model.number="rModem.txPersistence"
                label="Persistence"
                density="compact"
                type="number"
                hint="p-persistence CSMA (0–255)"
                persistent-hint
              />
              <v-text-field
                v-model.number="rModem.txSlotTimeMs"
                label="Slot (ms)"
                density="compact"
                type="number"
                hint="CSMA slot time"
                persistent-hint
              />
            </div>
          </div>
        </v-card-text>
        <v-divider />
        <v-card-actions class="flex-wrap">
          <v-alert
            v-if="radioSaveError"
            type="error"
            variant="tonal"
            density="compact"
            class="flex-1-1-100 mb-2"
          >
            {{ radioSaveError }}
          </v-alert>
          <span v-if="radioFormDirty" class="text-caption text-warning ml-2">
            ● Unsaved changes
          </span>
          <v-spacer />
          <v-btn variant="text" @click="radioDialogOpen = false">Cancel</v-btn>
          <v-btn
            color="primary"
            variant="tonal"
            :disabled="!radioFormValid || !!ssidError"
            :loading="radioSaving"
            @click="saveRadio"
          >
            Save Radio
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Add Geofence dialog -->
    <v-dialog v-model="showAddGeofence" max-width="600">
      <v-card>
        <v-card-title>Add Geofence</v-card-title>
        <v-card-text>
          <v-text-field v-model="gfName" label="Name" density="compact" class="mb-2" />
          <div class="d-flex ga-2 mb-2">
            <v-text-field v-model.number="gfLat" label="Latitude" density="compact" type="number" />
            <v-text-field
              v-model.number="gfLon"
              label="Longitude"
              density="compact"
              type="number"
            />
          </div>
          <v-text-field
            v-model.number="gfRadius"
            label="Radius (metres)"
            density="compact"
            type="number"
            class="mb-2"
          />
          <div class="d-flex ga-4 mb-2">
            <v-checkbox
              v-model="gfAlertOnEnter"
              label="Alert on enter"
              density="compact"
              hide-details
            />
            <v-checkbox
              v-model="gfAlertOnExit"
              label="Alert on exit"
              density="compact"
              hide-details
            />
          </div>
          <div class="text-caption text-medium-emphasis mb-1">
            Click on the map to set the centre point
          </div>
          <div id="gf-map" style="height: 280px; border-radius: 4px" />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="showAddGeofence = false">Cancel</v-btn>
          <v-btn
            color="primary"
            :disabled="!gfName || gfLat == null || gfLon == null"
            :loading="gfSaving"
            @click="saveGeofence"
          >
            Save
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Add Proximity Rule dialog -->
    <v-dialog v-model="showAddRule" max-width="600">
      <v-card>
        <v-card-title>Add Proximity Rule</v-card-title>
        <v-card-text>
          <v-text-field v-model="prName" label="Name" density="compact" class="mb-2" />
          <v-text-field
            v-model="prCallsign"
            label="Target callsign (optional — leave blank for any station)"
            density="compact"
            class="mb-2"
          />
          <div class="d-flex ga-2 mb-2">
            <v-text-field v-model.number="prLat" label="Latitude" density="compact" type="number" />
            <v-text-field
              v-model.number="prLon"
              label="Longitude"
              density="compact"
              type="number"
            />
          </div>
          <v-text-field
            v-model.number="prRadius"
            label="Radius (metres)"
            density="compact"
            type="number"
            class="mb-2"
          />
          <div class="text-caption text-medium-emphasis mb-1">
            Click on the map to set the centre point
          </div>
          <div id="pr-map" style="height: 280px; border-radius: 4px" />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="showAddRule = false">Cancel</v-btn>
          <v-btn
            color="primary"
            :disabled="!prName || prLat == null || prLon == null"
            :loading="prSaving"
            @click="saveRule"
          >
            Save
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Shared toast (used by beacon-now, radio toggle, and maintenance) -->
    <v-snackbar
      v-model="beaconToast"
      :color="beaconToastColor"
      :timeout="5000"
      location="bottom right"
    >
      {{ beaconToastText }}
      <template #actions>
        <v-btn variant="text" @click="beaconToast = false">Dismiss</v-btn>
      </template>
    </v-snackbar>

    <!-- Cleanup confirmation dialog -->
    <v-dialog v-model="cleanupConfirmOpen" max-width="460">
      <v-card>
        <v-card-title>
          <v-icon color="error" class="mr-2">mdi-broom</v-icon>
          Run cleanup now?
        </v-card-title>
        <v-card-text>
          This permanently deletes packets outside the retention windows — RF packets
          {{ retentionLabel(retentionRfDays) }}, APRS-IS packets
          {{ retentionLabel(retentionAprsIsDays) }}, own beacons
          {{ retentionLabel(retentionOwnDays) }}. This cannot be undone.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="cleanupConfirmOpen = false">Cancel</v-btn>
          <v-btn color="error" variant="tonal" @click="runCleanupNow">Delete old packets</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Delete confirmation dialog -->
    <v-dialog v-model="deleteConfirmOpen" max-width="400">
      <v-card>
        <v-card-title>
          <v-icon color="error" class="mr-2">mdi-alert-circle-outline</v-icon>
          Confirm Delete
        </v-card-title>
        <v-card-text>{{ deleteConfirmMessage }}</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="deleteConfirmOpen = false">Cancel</v-btn>
          <v-btn color="error" variant="tonal" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.radio-form-section {
  margin-bottom: 18px;
}

.radio-form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0 12px;
  align-items: start;
}

.station-home-map {
  width: 340px;
  max-width: 100%;
  height: 220px;
  border-radius: 8px;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.settings-view {
  height: 100%;
  display: flex;
  overflow: hidden;
}

.settings-nav {
  flex-shrink: 0;
  min-width: 176px;
  border-right: 1px solid rgba(128, 128, 128, 0.2);
  padding-top: 8px;
}

.settings-nav :deep(.v-tab) {
  justify-content: flex-start;
}

.settings-content {
  flex: 1;
  height: 100%;
  overflow-y: auto;
}

.settings-single {
  max-width: 900px;
}

.settings-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
  gap: 0 24px;
  align-items: start;
  max-width: 1280px;
}

.settings-section {
  min-width: 0;
}

.section-header {
  margin-top: 8px;
}

@media (max-width: 768px) {
  .settings-view {
    max-width: 100%;
    width: 100%;
  }
}

.radio-symbol-icon {
  image-rendering: pixelated;
  flex-shrink: 0;
}
</style>

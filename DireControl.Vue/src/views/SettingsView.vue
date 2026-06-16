<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import L from 'leaflet'
import { getGeofences, createGeofence, deleteGeofence, getProximityRules, createProximityRule, deleteProximityRule } from '@/api/alertsApi'
import type { GeofenceDto, ProximityRuleDto } from '@/types/alert'
import { getRadios, createRadio, updateRadio, deleteRadio, toggleRadioActive } from '@/api/radiosApi'
import type { RadioDto } from '@/types/radio'
import { getSettings, updateOutboundPath, updateAprsIsSettings, updateWeatherApiKeys, updateStationSettings, updateQrzCredentials, updateCleanupSettings, RadarProvider } from '@/api/stationsApi'
import { getWeatherStatus } from '@/api/weatherApi'
import { getMaintenanceStatus, updateRetention, runCleanup, type CleanupResult } from '@/api/maintenanceApi'
import { useUnits } from '@/composables/useUnits'
import { getSymbolStyle } from '@/utils/aprsIcon'
import { apiErrorText } from '@/utils/apiError'
import AprsSymbolPicker from '@/components/AprsSymbolPicker.vue'

// Units
const { distanceUnit, formatDistance, setDistanceUnit } = useUnits()

// Station settings
const stationCallsign = ref('')
const stationHomeLat = ref<number | null>(null)
const stationHomeLon = ref<number | null>(null)
const stationMaxRetries = ref(5)
const stationRetryDelay = ref(30)
const stationSaving = ref(false)
const stationSaveError = ref('')
const stationSaveSuccess = ref(false)
const settingsLoadFailed = ref(false)

async function saveStationSettings() {
  stationSaving.value = true
  stationSaveError.value = ''
  stationSaveSuccess.value = false
  try {
    await updateStationSettings({
      ourCallsign: stationCallsign.value.trim().toUpperCase(),
      homeLat: typeof stationHomeLat.value === 'number' ? stationHomeLat.value : null,
      homeLon: typeof stationHomeLon.value === 'number' ? stationHomeLon.value : null,
      maxRetryAttempts: stationMaxRetries.value,
      initialRetryDelaySeconds: stationRetryDelay.value,
    })
    stationSaveSuccess.value = true
    setTimeout(() => { stationSaveSuccess.value = false }, 3000)
  } catch (e) {
    stationSaveError.value = apiErrorText(e, 'Failed to save station settings — check callsign and coordinate values.')
  } finally {
    stationSaving.value = false
  }
}

// QRZ lookup credentials
const qrzUsername = ref('')
const qrzPassword = ref('')
const qrzPasswordConfigured = ref(false)
const clearQrzPassword = ref(false)
const showQrzPassword = ref(false)
const qrzSaving = ref(false)
const qrzSaveError = ref('')
const qrzSaveSuccess = ref(false)

async function saveQrzCredentials() {
  qrzSaving.value = true
  qrzSaveError.value = ''
  qrzSaveSuccess.value = false
  try {
    await updateQrzCredentials({
      username: qrzUsername.value.trim() || null,
      password: clearQrzPassword.value ? null : (qrzPassword.value || null),
      clearPassword: clearQrzPassword.value,
    })
    if (clearQrzPassword.value) qrzPasswordConfigured.value = false
    else if (qrzPassword.value) qrzPasswordConfigured.value = true
    qrzPassword.value = ''
    clearQrzPassword.value = false
    qrzSaveSuccess.value = true
    setTimeout(() => { qrzSaveSuccess.value = false }, 3000)
  } catch {
    qrzSaveError.value = 'Failed to save QRZ credentials.'
  } finally {
    qrzSaving.value = false
  }
}

// Cleanup schedule
const cleanupScheduleSaving = ref(false)
const cleanupScheduleError = ref('')
const cleanupScheduleSaveSuccess = ref(false)

async function saveCleanupSettings() {
  cleanupScheduleSaving.value = true
  cleanupScheduleError.value = ''
  cleanupScheduleSaveSuccess.value = false
  try {
    await updateCleanupSettings({
      databaseCleanupIntervalHours: cleanupIntervalHours.value,
      vacuumOnCleanup: vacuumOnCleanup.value,
    })
    cleanupScheduleSaveSuccess.value = true
    setTimeout(() => { cleanupScheduleSaveSuccess.value = false }, 3000)
  } catch {
    cleanupScheduleError.value = 'Failed to save cleanup schedule.'
  } finally {
    cleanupScheduleSaving.value = false
  }
}

// Messaging settings
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

function schedulePathSave() {
  if (pathSaveTimer) clearTimeout(pathSaveTimer)
  pathSaveTimer = setTimeout(async () => {
    if (outboundPathError.value) return
    outboundPathSaving.value = true
    outboundPathSaveError.value = ''
    try {
      await updateOutboundPath(outboundPath.value.trim())
    } catch (e) {
      outboundPathSaveError.value = apiErrorText(e, 'Failed to save outbound path.')
    } finally {
      outboundPathSaving.value = false
    }
  }, 600)
}

// APRS-IS settings
const aprsIsEnabled = ref(false)
const aprsIsHost = ref('rotate.aprs2.net')
const aprsIsPasscodeOverride = ref<number | null>(null)
const aprsIsPasscodeOverrideConfigured = ref(false)
const clearPasscodeOverride = ref(false)
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
      aprsIsPasscodeOverride:
        !clearPasscodeOverride.value && typeof aprsIsPasscodeOverride.value === 'number'
          ? aprsIsPasscodeOverride.value
          : null,
      clearAprsIsPasscodeOverride: clearPasscodeOverride.value,
      aprsIsFilter: aprsIsFilter.value.trim(),
      deduplicationWindowSeconds: deduplicationWindowSeconds.value,
    })
    aprsIsSaveSuccess.value = true
    // The override is write-only: track configured state locally, clear the field.
    if (clearPasscodeOverride.value) aprsIsPasscodeOverrideConfigured.value = false
    else if (aprsIsPasscodeOverride.value != null) aprsIsPasscodeOverrideConfigured.value = true
    aprsIsPasscodeOverride.value = null
    clearPasscodeOverride.value = false
    setTimeout(() => { aprsIsSaveSuccess.value = false }, 3000)
  } catch (e) {
    aprsIsSaveError.value = apiErrorText(e, 'Failed to save APRS-IS settings.')
  } finally {
    aprsIsSaving.value = false
  }
}

// Radios
const radios = ref<RadioDto[]>([])
const radioDialogOpen = ref(false)
const editingRadioId = ref<string | null>(null)
const radioSaving = ref(false)
const radioSaveError = ref('')
const radioListError = ref('')
const radiosLoadFailed = ref(false)

const rName = ref('')
const rCallsign = ref('')
const rSsid = ref('')
const rChannel = ref(0)
const rExpectedInterval = ref(600)
const rNotes = ref('')
const rBeaconPath = ref('')
const rBeaconSymbol = ref('')
const rBeaconComment = ref('')

const radioFormValid = computed(() =>
  rName.value.trim().length > 0 &&
  /^[A-Z0-9]{3,6}$/i.test(rCallsign.value.trim())
)

const computedFullCallsign = computed(() => {
  const cs = rCallsign.value.trim().toUpperCase()
  const ssid = rSsid.value.trim()
  return ssid ? `${cs}-${ssid}` : cs
})

const duplicateRadio = computed(() => {
  if (!radioFormValid.value) return null
  return radios.value.find(
    (r) => r.fullCallsign === computedFullCallsign.value && r.id !== editingRadioId.value
  ) ?? null
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
    radiosLoadFailed.value = false
  } catch {
    radiosLoadFailed.value = true
  }
}

function openAddRadio() {
  radioSaveError.value = ''
  editingRadioId.value = null
  rName.value = ''
  rCallsign.value = ''
  rSsid.value = ''
  rChannel.value = 0
  rExpectedInterval.value = 600
  rNotes.value = ''
  rBeaconPath.value = ''
  rBeaconSymbol.value = ''
  rBeaconComment.value = ''
  radioDialogOpen.value = true
}

function openEditRadio(radio: RadioDto) {
  radioSaveError.value = ''
  editingRadioId.value = radio.id
  rName.value = radio.name
  rCallsign.value = radio.callsign
  rSsid.value = radio.ssid ?? ''
  rChannel.value = radio.channelNumber
  rExpectedInterval.value = radio.expectedIntervalSeconds
  rNotes.value = radio.notes ?? ''
  rBeaconPath.value = radio.beaconPath ?? ''
  rBeaconSymbol.value = radio.beaconSymbol ?? ''
  rBeaconComment.value = radio.beaconComment ?? ''
  radioDialogOpen.value = true
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
  } catch (e) {
    radioSaveError.value = apiErrorText(e, 'Failed to save radio.')
  } finally {
    radioSaving.value = false
  }
}

async function toggleActive(id: string) {
  try {
    const updated = await toggleRadioActive(id)
    const idx = radios.value.findIndex((r) => r.id === id)
    if (idx !== -1) radios.value[idx] = updated
  } catch (e) {
    radioListError.value = apiErrorText(e, 'Failed to toggle radio active state.')
    setTimeout(() => { radioListError.value = '' }, 4000)
  }
}

// Delete (shared confirm dialog handles radios too)
function promptDeleteRadio(radio: RadioDto) {
  deleteError.value = ''
  const historyNote = radio.beaconCount > 0
    ? ` This radio has ${radio.beaconCount} beacon records. Deleting will remove all history.`
    : ''
  deleteConfirmMessage.value = `Delete radio "${radio.name}"?${historyNote} This cannot be undone.`
  deleteConfirmAction = async () => {
    await deleteRadio(radio.id)
    radios.value = radios.value.filter((r) => r.id !== radio.id)
  }
  deleteConfirmOpen.value = true
}

// API Keys
const API_KEYS_STORAGE_KEY = 'direcontrol-api-keys'

function readApiKeys(): Record<string, string> {
  try {
    const raw = localStorage.getItem(API_KEYS_STORAGE_KEY)
    if (raw) return JSON.parse(raw) as Record<string, string>
  } catch { /* ignore */ }
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
  setTimeout(() => { apiKeySaved.value = false }, 2500)
}

// Weather overlay API keys
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
    rvProKeyConfigured.value = selectedRadarProvider.value === RadarProvider.RainViewerPro
      ? (rvProValue !== null ? true : rvProKeyConfigured.value)
      : false
    // Clear the fields after saving — values are secrets
    owmApiKey.value = ''
    tomorrowIoApiKey.value = ''
    rainViewerProApiKey.value = ''
    setTimeout(() => { weatherKeysSaveSuccess.value = false }, 3000)
  } catch {
    weatherKeysSaveError.value = 'Failed to save weather API keys.'
  } finally {
    weatherKeysSaving.value = false
  }
}

// Geofences
const geofences = ref<GeofenceDto[]>([])
const showAddGeofence = ref(false)
const gfName = ref('')
const gfLat = ref<number | null>(null)
const gfLon = ref<number | null>(null)
const gfRadius = ref<number>(500)
const gfAlertOnEnter = ref(true)
const gfAlertOnExit = ref(true)
const gfSaving = ref(false)
const geofenceSaveError = ref('')
const geofencesLoadFailed = ref(false)

// Proximity Rules
const rules = ref<ProximityRuleDto[]>([])
const showAddRule = ref(false)
const prName = ref('')
const prCallsign = ref('')
const prLat = ref<number | null>(null)
const prLon = ref<number | null>(null)
const prRadius = ref<number>(1000)
const prSaving = ref(false)
const ruleSaveError = ref('')
const rulesLoadFailed = ref(false)

// Leaflet map for picking coordinates
let map: L.Map | null = null
let mapMarker: L.Marker | null = null
let mapCircle: L.Circle | null = null
let pickingFor: 'geofence' | 'rule' | null = null

// Database maintenance
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
const retentionSaveSuccess = ref(false)
const maintenanceLoadFailed = ref(false)
const cleanupRunError = ref('')
let cleanupPollTimer: ReturnType<typeof setInterval> | null = null

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let u = 0
  while (size >= 1024 && u < units.length - 1) { size /= 1024; u++ }
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
    maintenanceLoadFailed.value = false
  } catch {
    maintenanceLoadFailed.value = true
  }
}

async function saveRetention() {
  retentionSaving.value = true
  retentionSaveError.value = ''
  retentionSaveSuccess.value = false
  try {
    await updateRetention({
      rfDays: Math.max(0, Math.floor(retentionRfDays.value || 0)),
      aprsIsDays: Math.max(0, Math.floor(retentionAprsIsDays.value || 0)),
      ownDays: Math.max(0, Math.floor(retentionOwnDays.value || 0)),
    })
    retentionSaveSuccess.value = true
    setTimeout(() => { retentionSaveSuccess.value = false }, 3000)
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
    } catch { /* keep polling */ }
  }, 1500)
}

function stopCleanupPolling() {
  if (cleanupPollTimer) { clearInterval(cleanupPollTimer); cleanupPollTimer = null }
}

async function runCleanupNow() {
  cleanupRunning.value = true
  cleanupRunError.value = ''
  try {
    await runCleanup()
    startCleanupPolling()
  } catch (e) {
    cleanupRunning.value = false
    cleanupRunError.value = apiErrorText(e, 'Failed to start cleanup.')
  }
}

onMounted(async () => {
  try {
    const s = await getSettings()
    stationCallsign.value = s.ourCallsign
    stationHomeLat.value = s.homeLat
    stationHomeLon.value = s.homeLon
    stationMaxRetries.value = s.maxRetryAttempts
    stationRetryDelay.value = s.initialRetryDelaySeconds
    qrzUsername.value = s.qrzUsername ?? ''
    qrzPasswordConfigured.value = s.qrzPasswordConfigured
    outboundPath.value = s.outboundPath
    aprsIsEnabled.value = s.aprsIsEnabled
    aprsIsHost.value = s.aprsIsHost
    aprsIsPasscodeOverrideConfigured.value = s.aprsIsPasscodeOverrideConfigured
    aprsIsPasscodeComputed.value = s.aprsIsPasscodeComputed
    aprsIsFilter.value = s.aprsIsFilter
    deduplicationWindowSeconds.value = s.deduplicationWindowSeconds
  } catch {
    settingsLoadFailed.value = true
  }
  try {
    const status = await getWeatherStatus()
    owmKeyConfigured.value = status.wind.available
    tomorrowKeyConfigured.value = status.lightning.available
    selectedRadarProvider.value = status.radarProvider as RadarProvider
    rvProKeyConfigured.value = status.rainViewerProKeyConfigured
  } catch { /* ignore */ }
  await Promise.all([loadRadios(), loadGeofences(), loadRules(), loadMaintenance()])
})

onUnmounted(() => {
  stopCleanupPolling()
  if (map) {
    map.remove()
    map = null
  }
})

async function loadGeofences() {
  try {
    geofences.value = await getGeofences()
    geofencesLoadFailed.value = false
  } catch {
    geofencesLoadFailed.value = true
  }
}
async function loadRules() {
  try {
    rules.value = await getProximityRules()
    rulesLoadFailed.value = false
  } catch {
    rulesLoadFailed.value = true
  }
}

function initMap(containerId: string, forType: 'geofence' | 'rule', defaultLat: number, defaultLon: number) {
  if (map) { map.remove(); map = null; mapMarker = null; mapCircle = null }
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
    const r = pickingFor === 'geofence' ? (gfRadius.value || 500) : (prRadius.value || 1000)
    mapCircle = L.circle([lat, lng], { radius: r, color: '#2196f3', fillOpacity: 0.12 }).addTo(map!)
  })
}

function openAddGeofence() {
  showAddGeofence.value = true
  geofenceSaveError.value = ''
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
  ruleSaveError.value = ''
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
  geofenceSaveError.value = ''
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
    if (map) { map.remove(); map = null }
  } catch (e) {
    geofenceSaveError.value = apiErrorText(e, 'Failed to save geofence.')
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
  ruleSaveError.value = ''
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
    if (map) { map.remove(); map = null }
  } catch (e) {
    ruleSaveError.value = apiErrorText(e, 'Failed to save proximity rule.')
  } finally {
    prSaving.value = false
  }
}

async function removeRule(id: number) {
  await deleteProximityRule(id)
  rules.value = rules.value.filter((r) => r.id !== id)
}

// Confirm delete dialog
const deleteConfirmOpen = ref(false)
const deleteConfirmMessage = ref('')
const deleteError = ref('')
const deleting = ref(false)
let deleteConfirmAction: (() => Promise<void>) | null = null

function promptDeleteGeofence(id: number, name: string) {
  deleteError.value = ''
  deleteConfirmMessage.value = `Delete geofence "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeGeofence(id)
  deleteConfirmOpen.value = true
}

function promptDeleteRule(id: number, name: string) {
  deleteError.value = ''
  deleteConfirmMessage.value = `Delete proximity rule "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeRule(id)
  deleteConfirmOpen.value = true
}

async function confirmDelete() {
  if (!deleteConfirmAction) {
    deleteConfirmOpen.value = false
    return
  }
  deleting.value = true
  deleteError.value = ''
  try {
    await deleteConfirmAction()
    deleteConfirmOpen.value = false
    deleteConfirmAction = null
  } catch (e) {
    deleteError.value = apiErrorText(e, 'Delete failed.')
  } finally {
    deleting.value = false
  }
}
</script>

<template>
  <div class="settings-view pa-4">
    <h1 class="text-h5 font-weight-bold mb-4 mt-0">Settings</h1>

    <!-- Radios -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Radios</h2>
      <v-spacer />
      <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddRadio">
        Add Radio
      </v-btn>
    </div>

    <v-alert v-if="radioListError" type="error" variant="tonal" density="compact" class="mb-2">
      {{ radioListError }}
    </v-alert>
    <v-card variant="outlined" class="mb-6">
      <v-alert v-if="radiosLoadFailed" type="error" variant="tonal" density="compact" class="ma-2">
        Failed to load radios — reload the page to retry.
      </v-alert>
      <div v-else-if="radios.length === 0" class="text-center text-medium-emphasis py-4">
        No radios configured
      </div>
      <template v-else>
        <v-card
          v-for="radio in radios"
          :key="radio.id"
          variant="flat"
          class="radio-card pa-3 ma-2"
          rounded="sm"
          border
        >
          <div class="d-flex align-center justify-space-between">
            <div class="d-flex align-center ga-2">
              <div
                v-if="radio.beaconSymbol && radio.beaconSymbol.length >= 2"
                :style="getSymbolStyle(radio.beaconSymbol[0]!, radio.beaconSymbol[1]!)"
                class="radio-symbol-icon"
              />
              <div class="text-body-1 font-weight-medium">{{ radio.name }}</div>
            </div>
            <div class="d-flex align-center ga-1">
              <v-btn
                icon="mdi-pencil"
                size="x-small"
                variant="text"
                aria-label="Edit radio"
                @click="openEditRadio(radio)"
              />
              <v-btn
                icon="mdi-close"
                size="x-small"
                variant="text"
                color="error"
                aria-label="Delete radio"
                @click="promptDeleteRadio(radio)"
              />
            </div>
          </div>
          <div class="d-flex align-center ga-2 mt-1">
            <span class="text-caption font-weight-bold">{{ radio.fullCallsign }}</span>
            <span class="text-caption text-medium-emphasis">·</span>
            <span class="text-caption text-medium-emphasis">Channel {{ radio.channelNumber }}</span>
            <span class="text-caption text-medium-emphasis">·</span>
            <v-chip
              :color="radio.isActive ? 'green' : 'grey'"
              size="x-small"
              variant="tonal"
              class="cursor-pointer"
              @click="toggleActive(radio.id)"
            >
              {{ radio.isActive ? 'Active' : 'Inactive' }}
            </v-chip>
          </div>
          <div v-if="radio.notes" class="text-caption text-medium-emphasis mt-1">
            {{ radio.notes }}
          </div>
        </v-card>
      </template>
    </v-card>

    <!-- Add / Edit Radio dialog -->
    <v-dialog v-model="radioDialogOpen" max-width="480">
      <v-card>
        <v-card-title>{{ editingRadioId ? 'Edit Radio' : 'Add Radio' }}</v-card-title>
        <v-card-text>
          <!-- Duplicate warning -->
          <v-alert
            v-if="duplicateRadio"
            type="warning"
            variant="tonal"
            density="compact"
            class="mb-3"
          >
            {{ computedFullCallsign }} is already configured as "{{ duplicateRadio.name }}". Are you sure?
          </v-alert>

          <v-text-field
            v-model="rName"
            label="Name *"
            density="compact"
            class="mb-2"
            :rules="[(v: string) => v.trim().length > 0 || 'Required']"
          />
          <div class="d-flex ga-2 mb-2">
            <v-text-field
              v-model="rCallsign"
              label="Callsign *"
              density="compact"
              :rules="[(v: string) => /^[A-Z0-9]{3,6}$/i.test(v.trim()) || '3–6 letters/digits']"
              style="flex: 2"
            />
            <v-text-field
              v-model="rSsid"
              label="SSID"
              density="compact"
              :error-messages="ssidError || undefined"
              placeholder="0–15"
              style="flex: 1"
            />
          </div>
          <v-text-field
            v-model.number="rChannel"
            label="Channel"
            density="compact"
            type="number"
            :rules="[(v: number) => (v >= 0 && v <= 15) || '0–15']"
            hint="Matches the CHANNEL number in your direwolf.conf (0-based). Most single-radio setups use channel 0."
            persistent-hint
            class="mb-3"
          />
          <v-text-field
            v-model.number="rExpectedInterval"
            label="Expected beacon interval (seconds)"
            density="compact"
            type="number"
            class="mb-2"
          />
          <v-text-field
            v-model="rNotes"
            label="Notes"
            density="compact"
            class="mb-3"
          />
          <div class="text-subtitle-2 font-weight-medium mb-2">Beacon Config (optional)</div>
          <v-text-field
            v-model="rBeaconPath"
            label="Beacon path"
            density="compact"
            class="mb-2"
            placeholder="e.g. WIDE1-1,WIDE2-1"
            hint="Leave blank for direct (no digipeating)"
            persistent-hint
          />
          <div class="d-flex align-start ga-2">
            <div style="flex: 1; min-width: 0">
              <AprsSymbolPicker v-model="rBeaconSymbol" />
            </div>
            <v-text-field
              v-model="rBeaconComment"
              label="Comment"
              density="compact"
              style="flex: 2; min-width: 0"
            />
          </div>
        </v-card-text>
        <v-alert v-if="radioSaveError" type="error" variant="tonal" density="compact" class="mx-4 mb-2">
          {{ radioSaveError }}
        </v-alert>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="radioDialogOpen = false">Cancel</v-btn>
          <v-btn
            color="primary"
            :disabled="!radioFormValid || !!ssidError"
            :loading="radioSaving"
            @click="saveRadio"
          >
            Save Radio
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- API Keys -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Map API Keys</h2>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-body-2 text-medium-emphasis mb-4">
        API keys are stored only in your browser's local storage and are never sent to the server.
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

    <!-- Weather Overlays -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Weather Overlays</h2>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <!-- Radar provider selector -->
      <div class="text-body-2 font-weight-medium mb-2">Rainfall Radar Provider</div>
      <v-select
        v-model="selectedRadarProvider"
        :items="[
          { title: 'IEM NEXRAD — Free · US · zoom 8 · 5-min updates', value: 0 },
          { title: 'RainViewer — Free · Global · zoom 7 · 10-min updates', value: 1 },
          { title: 'RainViewer Pro — $40/yr · Global · zoom 12 · 10-min updates', value: 2 },
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
          :placeholder="rvProKeyConfigured ? 'Key saved — enter a new value to replace' : ''"
          class="mb-1"
          :prepend-inner-icon="(rainViewerProApiKey.trim() || rvProKeyConfigured) ? 'mdi-check-circle' : 'mdi-alert-circle-outline'"
          :color="(rainViewerProApiKey.trim() || rvProKeyConfigured) ? 'success' : 'warning'"
          @click:append-inner="showRainViewerProKey = !showRainViewerProKey"
        />
        <div class="text-caption text-medium-emphasis mb-4">
          Get a key at <a href="https://www.rainviewer.com" target="_blank" rel="noopener">rainviewer.com</a>
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
        :prepend-inner-icon="(owmApiKey.trim() || owmKeyConfigured) ? 'mdi-check-circle' : 'mdi-alert-circle-outline'"
        :color="(owmApiKey.trim() || owmKeyConfigured) ? 'success' : 'warning'"
        @click:append-inner="showOwmKey = !showOwmKey"
      />
      <div class="text-caption text-medium-emphasis mb-4">
        Get a free key at
        <a href="https://openweathermap.org/api" target="_blank" rel="noopener">openweathermap.org</a>
      </div>

      <!-- Tomorrow.io lightning key -->
      <div class="text-body-2 font-weight-medium mb-2">Lightning (Tomorrow.io)</div>
      <v-text-field
        v-model="tomorrowIoApiKey"
        label="Tomorrow.io API Key"
        density="compact"
        :type="showTomorrowKey ? 'text' : 'password'"
        :append-inner-icon="showTomorrowKey ? 'mdi-eye-off' : 'mdi-eye'"
        :placeholder="tomorrowKeyConfigured ? 'Key saved — enter a new value to replace' : ''"
        class="mb-1"
        :prepend-inner-icon="(tomorrowIoApiKey.trim() || tomorrowKeyConfigured) ? 'mdi-check-circle' : 'mdi-alert-circle-outline'"
        :color="(tomorrowIoApiKey.trim() || tomorrowKeyConfigured) ? 'success' : 'warning'"
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

    <!-- Units -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Units</h2>
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

    <!-- Geofences -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Geofences</h2>
      <v-spacer />
      <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddGeofence">
        Add Geofence
      </v-btn>
    </div>

    <v-card variant="outlined" class="mb-6">
      <v-alert v-if="geofencesLoadFailed" type="error" variant="tonal" density="compact" class="ma-2">
        Failed to load geofences — reload the page to retry.
      </v-alert>
      <div v-else-if="geofences.length === 0" class="text-center text-medium-emphasis py-4">
        No geofences defined
      </div>
      <v-list v-else density="compact">
        <v-list-item
          v-for="gf in geofences"
          :key="gf.id"
        >
          <template #prepend>
            <v-icon :color="gf.isActive ? 'green' : 'grey'" size="20">mdi-map-marker-radius</v-icon>
          </template>
          <v-list-item-title>{{ gf.name }}</v-list-item-title>
          <v-list-item-subtitle>
            {{ gf.centerLat.toFixed(5) }}, {{ gf.centerLon.toFixed(5) }} —
            {{ formatDistance(gf.radiusMeters / 1000) }}
            <span v-if="gf.alertOnEnter" class="ml-1 text-caption">↓Enter</span>
            <span v-if="gf.alertOnExit" class="ml-1 text-caption">↑Exit</span>
          </v-list-item-subtitle>
          <template #append>
            <v-btn icon="mdi-delete" size="x-small" variant="text" color="error" aria-label="Delete geofence" @click="promptDeleteGeofence(gf.id, gf.name)" />
          </template>
        </v-list-item>
      </v-list>
    </v-card>

    <!-- Add Geofence dialog -->
    <v-dialog v-model="showAddGeofence" max-width="600">
      <v-card>
        <v-card-title>Add Geofence</v-card-title>
        <v-card-text>
          <v-text-field v-model="gfName" label="Name" density="compact" class="mb-2" />
          <div class="d-flex ga-2 mb-2">
            <v-text-field v-model.number="gfLat" label="Latitude" density="compact" type="number" />
            <v-text-field v-model.number="gfLon" label="Longitude" density="compact" type="number" />
          </div>
          <v-text-field
            v-model.number="gfRadius"
            label="Radius (metres)"
            density="compact"
            type="number"
            class="mb-2"
          />
          <div class="d-flex ga-4 mb-2">
            <v-checkbox v-model="gfAlertOnEnter" label="Alert on enter" density="compact" hide-details />
            <v-checkbox v-model="gfAlertOnExit" label="Alert on exit" density="compact" hide-details />
          </div>
          <div class="text-caption text-medium-emphasis mb-1">Click on the map to set the centre point</div>
          <div id="gf-map" style="height: 280px; border-radius: 4px" />
        </v-card-text>
        <v-alert v-if="geofenceSaveError" type="error" variant="tonal" density="compact" class="mx-4 mb-2">
          {{ geofenceSaveError }}
        </v-alert>
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

    <!-- Proximity Rules -->
    <div class="section-header d-flex align-center mb-2">
      <h2 class="text-h6 ma-0">Proximity Rules</h2>
      <v-spacer />
      <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddRule">
        Add Rule
      </v-btn>
    </div>

    <v-card variant="outlined">
      <v-alert v-if="rulesLoadFailed" type="error" variant="tonal" density="compact" class="ma-2">
        Failed to load proximity rules — reload the page to retry.
      </v-alert>
      <div v-else-if="rules.length === 0" class="text-center text-medium-emphasis py-4">
        No proximity rules defined
      </div>
      <v-list v-else density="compact">
        <v-list-item
          v-for="rule in rules"
          :key="rule.id"
        >
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
            <v-btn icon="mdi-delete" size="x-small" variant="text" color="error" aria-label="Delete rule" @click="promptDeleteRule(rule.id, rule.name)" />
          </template>
        </v-list-item>
      </v-list>
    </v-card>

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
            <v-text-field v-model.number="prLon" label="Longitude" density="compact" type="number" />
          </div>
          <v-text-field
            v-model.number="prRadius"
            label="Radius (metres)"
            density="compact"
            type="number"
            class="mb-2"
          />
          <div class="text-caption text-medium-emphasis mb-1">Click on the map to set the centre point</div>
          <div id="pr-map" style="height: 280px; border-radius: 4px" />
        </v-card-text>
        <v-alert v-if="ruleSaveError" type="error" variant="tonal" density="compact" class="mx-4 mb-2">
          {{ ruleSaveError }}
        </v-alert>
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

    <!-- Station -->
    <div class="section-header d-flex align-center mb-2 mt-6">
      <h2 class="text-h6 ma-0">Station</h2>
      <v-progress-circular v-if="stationSaving" indeterminate size="16" width="2" class="ml-3" />
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <v-alert v-if="settingsLoadFailed" type="warning" variant="tonal" density="compact" class="mb-3">
        Settings failed to load — the values below may be stale, and saving may overwrite your stored settings. Reload the page to retry.
      </v-alert>
      <v-text-field
        v-model="stationCallsign"
        label="Callsign (with optional SSID)"
        density="compact"
        class="mb-2"
        style="max-width: 360px"
        hint="Used for APRS-IS login and message identity — saving reconnects APRS-IS"
        persistent-hint
      />
      <div class="d-flex ga-2 mb-2 mt-3">
        <v-text-field
          v-model.number="stationHomeLat"
          label="Home latitude"
          type="number"
          density="compact"
          style="max-width: 200px"
        />
        <v-text-field
          v-model.number="stationHomeLon"
          label="Home longitude"
          type="number"
          density="compact"
          style="max-width: 200px"
        />
      </div>
      <div class="d-flex ga-2 mb-2">
        <v-text-field
          v-model.number="stationMaxRetries"
          label="Max retry attempts"
          type="number"
          density="compact"
          style="max-width: 200px"
        />
        <v-text-field
          v-model.number="stationRetryDelay"
          label="Initial retry delay (s)"
          type="number"
          density="compact"
          style="max-width: 200px"
        />
      </div>
      <v-btn color="primary" size="small" variant="tonal" :loading="stationSaving" :disabled="settingsLoadFailed" @click="saveStationSettings">
        Save
      </v-btn>
      <v-alert v-if="stationSaveError" type="error" variant="tonal" density="compact" class="mt-2">
        {{ stationSaveError }}
      </v-alert>
      <v-alert v-if="stationSaveSuccess" type="success" variant="tonal" density="compact" class="mt-2">
        Station settings saved.
      </v-alert>
      <div class="text-caption text-medium-emphasis mt-3">
        Values from <code>appsettings</code> apply until saved here; once saved, these take precedence.
      </div>
    </v-card>

    <!-- QRZ Lookups -->
    <div class="section-header d-flex align-center mb-2 mt-6">
      <h2 class="text-h6 ma-0">QRZ Lookups</h2>
      <v-progress-circular v-if="qrzSaving" indeterminate size="16" width="2" class="ml-3" />
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-body-2 text-medium-emphasis mb-3">
        Optional QRZ.com credentials for callsign lookups when HamDB has no result.
      </div>
      <v-text-field
        v-model="qrzUsername"
        label="QRZ username"
        density="compact"
        class="mb-2"
        style="max-width: 360px"
      />
      <v-text-field
        v-model="qrzPassword"
        :label="qrzPasswordConfigured ? 'QRZ password (configured — leave blank to keep)' : 'QRZ password'"
        :type="showQrzPassword ? 'text' : 'password'"
        :append-inner-icon="showQrzPassword ? 'mdi-eye-off' : 'mdi-eye'"
        density="compact"
        class="mb-1"
        style="max-width: 360px"
        @click:append-inner="showQrzPassword = !showQrzPassword"
      />
      <v-checkbox
        v-if="qrzPasswordConfigured"
        v-model="clearQrzPassword"
        label="Clear stored password"
        density="compact"
        hide-details
        class="mb-2"
      />
      <v-btn color="primary" size="small" variant="tonal" :loading="qrzSaving" :disabled="settingsLoadFailed" @click="saveQrzCredentials">
        Save
      </v-btn>
      <v-alert v-if="qrzSaveError" type="error" variant="tonal" density="compact" class="mt-2">
        {{ qrzSaveError }}
      </v-alert>
      <v-alert v-if="qrzSaveSuccess" type="success" variant="tonal" density="compact" class="mt-2">
        QRZ credentials saved.
      </v-alert>
    </v-card>

    <!-- Messaging -->
    <div class="section-header d-flex align-center mb-2 mt-6">
      <h2 class="text-h6 ma-0">Messaging</h2>
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
        <v-btn size="x-small" variant="tonal" @click="outboundPath = 'WIDE1-1,WIDE2-1'; schedulePathSave()">WIDE1-1,WIDE2-1</v-btn>
        <v-btn size="x-small" variant="tonal" @click="outboundPath = 'WIDE2-1'; schedulePathSave()">WIDE2-1</v-btn>
        <v-btn size="x-small" variant="tonal" @click="outboundPath = 'WIDE1-1'; schedulePathSave()">WIDE1-1</v-btn>
        <v-btn size="x-small" variant="tonal" @click="outboundPath = ''; schedulePathSave()">Direct (no path)</v-btn>
      </div>
      <div class="text-body-2 text-medium-emphasis">
        Added to all outbound messages. <code>WIDE1-1,WIDE2-1</code> is recommended for most fixed and mobile stations.
        Leave blank to transmit direct with no digipeating.
      </div>
      <v-alert
        v-if="outboundPathSaveError"
        type="error"
        density="compact"
        class="mt-3"
      >
        {{ outboundPathSaveError }}
      </v-alert>
    </v-card>

    <!-- APRS-IS -->
    <div class="section-header d-flex align-center mb-2 mt-6">
      <h2 class="text-h6 ma-0">APRS-IS</h2>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <v-switch
        v-model="aprsIsEnabled"
        label="Enable APRS-IS connection"
        hide-details
        density="compact"
        class="mb-4"
      />

      <v-text-field
        v-model="aprsIsHost"
        label="Server"
        density="compact"
        class="mb-2"
      />

      <div class="text-body-2 text-medium-emphasis mb-1">
        Passcode (auto-computed: <strong>{{ aprsIsPasscodeComputed }}</strong>)
      </div>
      <v-text-field
        v-model.number="aprsIsPasscodeOverride"
        :label="aprsIsPasscodeOverrideConfigured
          ? 'Passcode override (configured — leave blank to keep)'
          : 'Passcode override (leave blank to use auto-computed)'"
        density="compact"
        type="number"
        clearable
        class="mb-1"
        style="max-width: 360px"
      />
      <v-checkbox
        v-if="aprsIsPasscodeOverrideConfigured"
        v-model="clearPasscodeOverride"
        label="Clear stored override (use auto-computed)"
        density="compact"
        hide-details
        class="mb-2"
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
      <v-alert
        v-if="aprsIsSaveError"
        type="error"
        density="compact"
        class="mt-3"
      >
        {{ aprsIsSaveError }}
      </v-alert>
    </v-card>

    <!-- Database maintenance -->
    <div class="section-header d-flex align-center mb-2 mt-6">
      <h2 class="text-h6 ma-0">Database Maintenance</h2>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <v-alert v-if="maintenanceLoadFailed" type="error" variant="tonal" density="compact" class="mb-3">
        Failed to load maintenance status — reload the page to retry.
      </v-alert>
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
          @click="runCleanupNow"
        >
          {{ cleanupRunning ? 'Cleaning…' : 'Run Cleanup Now' }}
        </v-btn>
      </div>

      <v-alert v-if="cleanupRunError" type="error" variant="tonal" density="compact" class="mb-3">
        {{ cleanupRunError }}
      </v-alert>

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

      <v-alert v-if="retentionSaveError" type="error" variant="tonal" density="compact" class="mt-2">
        {{ retentionSaveError }}
      </v-alert>
      <v-alert v-if="retentionSaveSuccess" type="success" variant="tonal" density="compact" class="mt-2">
        Retention settings saved.
      </v-alert>

      <div class="d-flex ga-3 align-center mt-3 flex-wrap">
        <v-text-field
          v-model.number="cleanupIntervalHours"
          label="Cleanup interval (hours, 0 = manual only)"
          type="number"
          density="compact"
          hide-details
          style="max-width: 280px"
        />
        <v-switch
          v-model="vacuumOnCleanup"
          label="VACUUM after pruning"
          density="compact"
          hide-details
          color="primary"
        />
        <v-btn size="small" variant="tonal" color="primary" :loading="cleanupScheduleSaving" @click="saveCleanupSettings">
          Save schedule
        </v-btn>
      </div>
      <v-alert v-if="cleanupScheduleError" type="error" variant="tonal" density="compact" class="mt-2">
        {{ cleanupScheduleError }}
      </v-alert>
      <v-alert v-if="cleanupScheduleSaveSuccess" type="success" variant="tonal" density="compact" class="mt-2">
        Cleanup schedule saved.
      </v-alert>
      <div class="text-caption text-medium-emphasis mt-2">
        Cleanup runs automatically
        <template v-if="cleanupIntervalHours > 0">every {{ cleanupIntervalHours }}h</template>
        <template v-else>only when triggered manually</template>,
        and {{ vacuumOnCleanup ? 'reclaims freed space (VACUUM)' : 'does not VACUUM' }} after pruning.
      </div>

      <v-divider class="my-3" />

      <div class="text-caption text-medium-emphasis mb-1">Last cleanup</div>
      <div v-if="!lastCleanup" class="text-body-2 text-medium-emphasis">No cleanup has run yet.</div>
      <div v-else-if="lastCleanup.error" class="text-body-2 text-error">
        Failed: {{ lastCleanup.error }}
      </div>
      <div v-else class="text-body-2">
        Deleted
        <strong>{{ (lastCleanup.rfDeleted + lastCleanup.aprsIsDeleted + lastCleanup.ownDeleted).toLocaleString() }}</strong>
        packets ({{ lastCleanup.aprsIsDeleted.toLocaleString() }} APRS-IS,
        {{ lastCleanup.rfDeleted.toLocaleString() }} RF,
        {{ lastCleanup.ownDeleted.toLocaleString() }} Own) ·
        {{ formatBytes(lastCleanup.sizeBeforeBytes) }} → {{ formatBytes(lastCleanup.sizeAfterBytes) }}
        <span v-if="lastCleanup.vacuumed" class="text-success">· vacuumed</span>
        <span v-else-if="lastCleanup.vacuumError" class="text-warning">· VACUUM skipped (busy)</span>
        <span class="text-medium-emphasis"> · {{ new Date(lastCleanup.completedAt).toLocaleString() }}</span>
      </div>
    </v-card>

    <!-- Delete confirmation dialog -->
    <v-dialog v-model="deleteConfirmOpen" max-width="400">
      <v-card>
        <v-card-title>
          <v-icon color="error" class="mr-2">mdi-alert-circle-outline</v-icon>
          Confirm Delete
        </v-card-title>
        <v-card-text>{{ deleteConfirmMessage }}</v-card-text>
        <v-alert v-if="deleteError" type="error" variant="tonal" density="compact" class="mx-4 mb-2">
          {{ deleteError }}
        </v-alert>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="deleteConfirmOpen = false">Cancel</v-btn>
          <v-btn color="error" variant="tonal" :loading="deleting" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.settings-view {
  height: 100%;
  overflow-y: auto;
  max-width: 800px;
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

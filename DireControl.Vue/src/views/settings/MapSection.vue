<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  getSettings,
  updateWeatherApiKeys,
  updateLightningAlerts,
  RadarProvider,
} from '@/api/stationsApi'
import { getWeatherStatus } from '@/api/weatherApi'
import { apiErrorDetail } from '@/api/axios'
import type { SettingsDto } from '@/types/station'
import { useUnits } from '@/composables/useUnits'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('map')
const toastStore = useToastStore()
const { distanceUnit } = useUnits()

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
const cartoApiKey = ref(readApiKeys()['carto'] ?? '')
const showCartoKey = ref(false)

function setApiKey(keys: Record<string, string>, name: string, value: string) {
  const trimmed = value.trim()
  if (trimmed) {
    keys[name] = trimmed
  } else {
    delete keys[name]
  }
}

function saveApiKeys() {
  const keys = readApiKeys()
  setApiKey(keys, 'jawg', jawgApiKey.value)
  setApiKey(keys, 'carto', cartoApiKey.value)
  localStorage.setItem(API_KEYS_STORAGE_KEY, JSON.stringify(keys))
}

// ─── Weather overlay API keys ─────────────────────────────────────────────────
const owmApiKey = ref('')
const owmKeyConfigured = ref(false)
const weatherKeysSaving = ref(false)
const weatherKeysSaveError = ref('')
const showOwmKey = ref(false)
const selectedRadarProvider = ref<RadarProvider>(RadarProvider.IemNexrad)
const rainViewerProApiKey = ref('')
const rvProKeyConfigured = ref(false)
const showRainViewerProKey = ref(false)

async function saveWeatherApiKeys(): Promise<boolean> {
  weatherKeysSaving.value = true
  weatherKeysSaveError.value = ''
  const owmValue = owmApiKey.value.trim() || null
  const rvProValue = rainViewerProApiKey.value.trim() || null
  try {
    await updateWeatherApiKeys(owmValue, selectedRadarProvider.value, rvProValue)
    // Update configured flags based on what was saved
    owmKeyConfigured.value = owmValue !== null
    rvProKeyConfigured.value =
      selectedRadarProvider.value === RadarProvider.RainViewerPro
        ? rvProValue !== null
          ? true
          : rvProKeyConfigured.value
        : false
    // Clear the fields after saving — values are secrets
    owmApiKey.value = ''
    rainViewerProApiKey.value = ''
    return true
  } catch (e) {
    weatherKeysSaveError.value = `Failed to save weather API keys: ${apiErrorDetail(e)}`
    return false
  } finally {
    weatherKeysSaving.value = false
  }
}

// ─── Lightning proximity alerts ───────────────────────────────────────────────
const lightningAlertEnabled = ref(false)
// Stored in km like all internal distances; edited in the user's display unit.
const lightningAlertRadiusKm = ref(30)
const lightningAlertRadiusText = ref('')
const lightningAlertCooldownMinutes = ref(5)
const lightningAlertAutoPan = ref(false)
const lightningAlertsSaving = ref(false)
const lightningAlertsSaveError = ref('')

function radiusToDisplay(km: number): string {
  const value = distanceUnit.value === 'mi' ? km * 0.621371 : km
  return String(Math.round(value * 10) / 10)
}

function loadLightningAlertSettings(s: SettingsDto) {
  lightningAlertEnabled.value = s.lightningAlertEnabled
  lightningAlertRadiusKm.value = s.lightningAlertRadiusKm
  lightningAlertRadiusText.value = radiusToDisplay(s.lightningAlertRadiusKm)
  lightningAlertCooldownMinutes.value = s.lightningAlertCooldownMinutes
  lightningAlertAutoPan.value = s.lightningAlertAutoPan
}

watch(distanceUnit, () => {
  lightningAlertRadiusText.value = radiusToDisplay(lightningAlertRadiusKm.value)
})

async function saveLightningAlerts(): Promise<boolean> {
  lightningAlertsSaveError.value = ''

  const displayValue = parseFloat(lightningAlertRadiusText.value)
  if (isNaN(displayValue) || displayValue <= 0) {
    lightningAlertsSaveError.value = 'Enter a valid alert radius.'
    return false
  }
  const radiusKm = distanceUnit.value === 'mi' ? displayValue / 0.621371 : displayValue
  if (radiusKm < 1 || radiusKm > 500) {
    lightningAlertsSaveError.value =
      'Alert radius must be between 1 and 500 km (roughly 0.6–310 mi).'
    return false
  }
  const cooldown = Math.round(Number(lightningAlertCooldownMinutes.value))
  if (isNaN(cooldown) || cooldown < 0 || cooldown > 120) {
    lightningAlertsSaveError.value = 'Cooldown must be between 0 and 120 minutes.'
    return false
  }

  lightningAlertsSaving.value = true
  try {
    await updateLightningAlerts(
      lightningAlertEnabled.value,
      radiusKm,
      cooldown,
      lightningAlertAutoPan.value,
    )
    lightningAlertRadiusKm.value = radiusKm
    lightningAlertCooldownMinutes.value = cooldown
    lightningAlertRadiusText.value = radiusToDisplay(radiusKm)
    return true
  } catch (e) {
    lightningAlertsSaveError.value = `Failed to save lightning alert settings: ${apiErrorDetail(e)}`
    return false
  } finally {
    lightningAlertsSaving.value = false
  }
}
// ─── One save bar for the section ─────────────────────────────────────────────
function radiusKmFromText(): number | string {
  const v = parseFloat(lightningAlertRadiusText.value)
  if (isNaN(v)) return lightningAlertRadiusText.value
  return Math.round((distanceUnit.value === 'mi' ? v / 0.621371 : v) * 10) / 10
}

const apiKeys = useFormSnapshot(() => [jawgApiKey.value.trim(), cartoApiKey.value.trim()])
const weatherKeys = useFormSnapshot(() => ({
  provider: selectedRadarProvider.value,
  owm: owmApiKey.value.trim(),
  rv: rainViewerProApiKey.value.trim(),
}))
const lightning = useFormSnapshot(() => ({
  enabled: lightningAlertEnabled.value,
  radius: radiusKmFromText(),
  cooldown: Number(lightningAlertCooldownMinutes.value),
  autoPan: lightningAlertAutoPan.value,
}))
const dirty = computed(
  () => apiKeys.dirty.value || weatherKeys.dirty.value || lightning.dirty.value,
)
const saving = computed(() => weatherKeysSaving.value || lightningAlertsSaving.value)

async function save() {
  let ok = true
  if (apiKeys.dirty.value) {
    saveApiKeys()
    apiKeys.markClean()
  }
  if (weatherKeys.dirty.value) {
    if (await saveWeatherApiKeys()) weatherKeys.markClean()
    else ok = false
  }
  if (lightning.dirty.value) {
    if (await saveLightningAlerts()) lightning.markClean()
    else ok = false
  }
  if (ok) toastStore.toast('Map & weather settings saved', 'success', 3000)
}

onMounted(async () => {
  apiKeys.markClean()
  try {
    const s = await getSettings()
    loadLightningAlertSettings(s)
    lightning.markClean()
  } catch {
    /* lightning fields keep their defaults; the save reports any error */
  }
  try {
    const status = await getWeatherStatus()
    owmKeyConfigured.value = status.wind.available
    selectedRadarProvider.value = status.radarProvider as RadarProvider
    rvProKeyConfigured.value = status.rainViewerProKeyConfigured
  } catch {
    /* ignore */
  }
  weatherKeys.markClean()
})
</script>

<template>
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
        <v-text-field
          v-model="cartoApiKey"
          label="CARTO API key"
          density="compact"
          :type="showCartoKey ? 'text' : 'password'"
          :append-inner-icon="showCartoKey ? 'mdi-eye-off' : 'mdi-eye'"
          hint="Required for the Carto Light / Carto Dark Matter tile providers. CARTO's
                  basemaps now stamp unkeyed tiles. Get a key at carto.com."
          persistent-hint
          class="mb-3"
          @click:append-inner="showCartoKey = !showCartoKey"
        />
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
            { title: 'IEM NEXRAD: Free · US · zoom 8 · 5-min updates', value: 0 },
            { title: 'RainViewer: Free · Global · zoom 7 · 10-min updates', value: 1 },
            {
              title: 'RainViewer Pro: $40/yr · Global · zoom 12 · 10-min updates',
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
            :placeholder="rvProKeyConfigured ? 'Key saved. Enter a new value to replace it.' : ''"
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
            <a href="https://www.rainviewer.com" target="_blank" rel="noopener">rainviewer.com</a>
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
          :placeholder="owmKeyConfigured ? 'Key saved. Enter a new value to replace it.' : ''"
          class="mb-1"
          :prepend-inner-icon="
            owmApiKey.trim() || owmKeyConfigured ? 'mdi-check-circle' : 'mdi-alert-circle-outline'
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

        <v-alert v-if="weatherKeysSaveError" type="error" density="compact" class="mb-3">
          {{ weatherKeysSaveError }}
        </v-alert>
      </v-card>

      <!-- ================================================================ -->
      <!-- Lightning Alerts -->
      <!-- ================================================================ -->
      <div class="section-header d-flex align-center mb-2">
        <span class="text-h6">Lightning Alerts</span>
      </div>

      <v-card variant="outlined" class="mb-6 pa-4">
        <div class="text-body-2 text-medium-emphasis mb-4">
          Plays an alert sound and shows a notification when a lightning strike lands within the
          alert radius of your home position.
        </div>
        <v-switch
          v-model="lightningAlertEnabled"
          label="Enable lightning alerts"
          color="primary"
          density="compact"
          hide-details
          class="mb-3"
        />
        <v-text-field
          v-model="lightningAlertRadiusText"
          label="Alert radius"
          density="compact"
          :suffix="distanceUnit"
          :disabled="!lightningAlertEnabled"
          hint="Alert when a strike lands within this distance of home."
          persistent-hint
          class="mb-3"
        />
        <v-text-field
          v-model.number="lightningAlertCooldownMinutes"
          label="Cooldown (minutes)"
          type="number"
          density="compact"
          :disabled="!lightningAlertEnabled"
          hint="Minimum time between alerts while a storm is active."
          persistent-hint
          class="mb-3"
        />
        <v-switch
          v-model="lightningAlertAutoPan"
          label="Pan map to the triggering strike"
          color="primary"
          density="compact"
          :disabled="!lightningAlertEnabled"
          hint="Centres the map on each strike that raises an alert. The strike stays marked either way."
          persistent-hint
          class="mb-3"
        />

        <v-alert v-if="lightningAlertsSaveError" type="error" density="compact" class="mb-3">
          {{ lightningAlertsSaveError }}
        </v-alert>
      </v-card>
    </div>
  </div>
  <div class="settings-single">
    <SettingsSaveBar
      :restart="meta.restart"
      :dirty="dirty"
      :saving="saving"
      :disabled="!dirty"
      save-label="Save map & weather"
      @save="save"
    />
  </div>
</template>

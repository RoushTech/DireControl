<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  getSettings,
  updateMessageRetry,
  updateOutboundPath,
  updateStationIdentity,
} from '@/api/stationsApi'
import { apiErrorDetail } from '@/api/axios'
import { useHealthStore } from '@/stores/health'
import { useToastStore } from '@/stores/toastStore'
import HomePicker, { type HomePosition } from '@/components/HomePicker.vue'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('station')
const toastStore = useToastStore()
const healthStore = useHealthStore()

// ─── Station identity (callsign + home position) ──────────────────────────────
const stationCallsign = ref('')
const savedCallsign = ref('')
const home = ref<HomePosition | null>(null)
const homeValid = ref(true)

const CALLSIGN_REGEX = /^[A-Z0-9]{3,6}(-([0-9]|1[0-5]))?$/

function callsignRule(v: string): true | string {
  const cs = v.trim().toUpperCase()
  if (!cs) return 'Callsign is required'
  if (cs.startsWith('N0CALL')) return 'N0CALL is a placeholder, not a callsign'
  return CALLSIGN_REGEX.test(cs) || 'Callsign with optional -SSID (0–15), e.g. W3UWU or W3UWU-10'
}

const callsignError = computed(() => {
  const r = callsignRule(stationCallsign.value)
  return r === true ? '' : r
})

const isPlaceholderCallsign = computed(() =>
  stationCallsign.value.trim().toUpperCase().startsWith('N0CALL'),
)

// ─── Messaging: default outbound path ─────────────────────────────────────────
const outboundPath = ref('')
const PATH_REGEX = /^[A-Za-z0-9-]+(,[A-Za-z0-9-]+)*$/

const outboundPathError = computed(() => {
  const p = (outboundPath.value ?? '').trim()
  if (!p) return ''
  return PATH_REGEX.test(p) ? '' : 'Use comma-separated callsigns, e.g. WIDE1-1,WIDE2-1'
})

const PATH_PRESETS = [
  { label: 'WIDE1-1,WIDE2-1', value: 'WIDE1-1,WIDE2-1' },
  { label: 'WIDE2-1', value: 'WIDE2-1' },
  { label: 'WIDE1-1', value: 'WIDE1-1' },
  { label: 'Direct (no path)', value: '' },
]

// ─── Message retry ────────────────────────────────────────────────────────────
const maxRetryAttempts = ref<number>(3)
const initialRetryDelaySeconds = ref<number>(30)

const retryAttemptsRules = [
  (v: number) => (Number.isInteger(Number(v)) && v >= 1 && v <= 10) || 'Whole number, 1–10',
]
const retryDelayRules = [
  (v: number) => (Number.isInteger(Number(v)) && v >= 10 && v <= 600) || 'Whole number, 10–600 s',
]

const retryValid = computed(
  () =>
    retryAttemptsRules[0]!(maxRetryAttempts.value) === true &&
    retryDelayRules[0]!(initialRetryDelaySeconds.value) === true,
)

// ─── Dirty tracking (per save target) ─────────────────────────────────────────
const identity = useFormSnapshot(() => ({
  cs: stationCallsign.value.trim().toUpperCase(),
  home: home.value,
  homeValid: homeValid.value,
}))
const path = useFormSnapshot(() => (outboundPath.value ?? '').trim())
const retry = useFormSnapshot(() => [
  Number(maxRetryAttempts.value),
  Number(initialRetryDelaySeconds.value),
])
const dirty = computed(() => identity.dirty.value || path.dirty.value || retry.dirty.value)

const saveBlocked = computed(
  () =>
    !dirty.value ||
    (identity.dirty.value && (!!callsignError.value || !homeValid.value)) ||
    (path.dirty.value && !!outboundPathError.value) ||
    (retry.dirty.value && !retryValid.value),
)

const loadError = ref('')
const saveError = ref('')
const saving = ref(false)

async function save() {
  if (saveBlocked.value) return
  saving.value = true
  saveError.value = ''
  const callsignChanged =
    identity.dirty.value && stationCallsign.value.trim().toUpperCase() !== savedCallsign.value
  try {
    if (identity.dirty.value) {
      const cs = stationCallsign.value.trim().toUpperCase()
      await updateStationIdentity({
        callsign: cs,
        homeLat: home.value?.lat ?? null,
        homeLon: home.value?.lon ?? null,
      })
      stationCallsign.value = cs
      savedCallsign.value = cs
      identity.markClean()
      void healthStore.refreshSettings()
    }
    if (path.dirty.value) {
      await updateOutboundPath((outboundPath.value ?? '').trim())
      path.markClean()
    }
    if (retry.dirty.value) {
      await updateMessageRetry({
        maxRetryAttempts: Number(maxRetryAttempts.value),
        initialRetryDelaySeconds: Number(initialRetryDelaySeconds.value),
      })
      retry.markClean()
    }
    if (callsignChanged)
      toastStore.toast(
        'Callsign saved. Reconnecting APRS-IS and restarting PMS and terminal listeners…',
        'info',
      )
    else toastStore.toast('Station settings saved', 'success', 3000)
  } catch (e) {
    saveError.value = `Save failed: ${apiErrorDetail(e)}`
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const s = await getSettings()
    stationCallsign.value = s.ourCallsign
    savedCallsign.value = s.ourCallsign.trim().toUpperCase()
    home.value = s.homePosition ? { lat: s.homePosition.lat, lon: s.homePosition.lon } : null
    homeValid.value = true
    outboundPath.value = s.outboundPath ?? ''
    maxRetryAttempts.value = s.maxRetryAttempts
    initialRetryDelaySeconds.value = s.initialRetryDelaySeconds
    identity.markClean()
    path.markClean()
    retry.markClean()
  } catch (e) {
    loadError.value = `Couldn't load station settings: ${apiErrorDetail(e)}`
  }
})
</script>

<template>
  <div class="settings-grid">
    <div class="settings-section">
      <v-alert
        v-if="isPlaceholderCallsign"
        type="error"
        variant="tonal"
        density="compact"
        class="mb-4"
      >
        Your callsign is still the placeholder N0CALL. Set your own callsign below before
        transmitting or connecting to APRS-IS. DireControl identifies with it on air.
      </v-alert>
      <v-alert v-if="loadError" type="error" variant="tonal" density="compact" class="mb-4">
        {{ loadError }}
      </v-alert>

      <!-- Station identity -->
      <div class="section-header d-flex align-center mb-2">
        <span class="text-h6">Station Identity</span>
      </div>

      <v-card variant="outlined" class="mb-6 pa-4">
        <div class="text-subtitle-2 font-weight-medium mb-1">Callsign</div>
        <div class="text-caption text-medium-emphasis mb-2">
          Stamped on every transmitted packet and used for the APRS-IS login.
        </div>
        <v-text-field
          v-model="stationCallsign"
          label="Station callsign"
          placeholder="e.g. W3UWU-10"
          :rules="[callsignRule]"
          hide-details="auto"
          class="mb-4"
          style="max-width: 280px"
          autocapitalize="characters"
        />

        <div class="text-subtitle-2 font-weight-medium mb-1">Home position</div>
        <div class="text-caption text-medium-emphasis mb-2">
          Beacon position. Also the origin for range rings and distances.
        </div>
        <HomePicker v-model="home" v-model:valid="homeValid" />
      </v-card>
    </div>

    <div class="settings-section">
      <!-- Messaging -->
      <div class="section-header d-flex align-center mb-2">
        <span class="text-h6">Messaging</span>
      </div>

      <v-card variant="outlined" class="mb-6 pa-4">
        <div class="text-subtitle-2 font-weight-medium mb-1">Default outbound path</div>
        <v-text-field
          v-model="outboundPath"
          clearable
          :error-messages="outboundPathError"
          hide-details="auto"
          placeholder="e.g. WIDE1-1,WIDE2-1"
          class="mb-2"
          style="max-width: 360px"
          aria-label="Default outbound path"
        />
        <div class="d-flex align-center flex-wrap ga-2 mb-3">
          <span class="text-caption text-medium-emphasis mr-1">Common paths:</span>
          <v-btn
            v-for="p in PATH_PRESETS"
            :key="p.label"
            size="small"
            variant="tonal"
            @click="outboundPath = p.value"
          >
            {{ p.label }}
          </v-btn>
        </div>
        <div class="text-body-2 text-medium-emphasis">
          Added to all outbound messages. <code>WIDE1-1,WIDE2-1</code> is recommended for most fixed
          and mobile stations. Leave blank to transmit direct with no digipeating.
        </div>

        <v-divider class="my-4" />

        <div class="text-subtitle-2 font-weight-medium mb-1">Message retries</div>
        <div class="text-caption text-medium-emphasis mb-3">
          Unacknowledged messages are re-sent; the delay doubles after each attempt.
        </div>
        <div class="d-flex flex-wrap ga-4">
          <v-text-field
            v-model.number="maxRetryAttempts"
            label="Max attempts"
            type="number"
            min="1"
            max="10"
            :rules="retryAttemptsRules"
            hint="1–10"
            persistent-hint
            style="max-width: 180px"
          />
          <v-text-field
            v-model.number="initialRetryDelaySeconds"
            label="First retry after (s)"
            type="number"
            min="10"
            max="600"
            :rules="retryDelayRules"
            hint="10–600 s; doubles after each attempt"
            persistent-hint
            style="max-width: 240px"
          />
        </div>
      </v-card>
    </div>
  </div>

  <v-alert v-if="saveError" type="error" variant="tonal" density="compact" class="mb-2">
    {{ saveError }}
  </v-alert>
  <SettingsSaveBar
    :restart="meta.restart"
    :dirty="dirty"
    :saving="saving"
    :disabled="saveBlocked"
    save-label="Save station"
    @save="save"
  />
</template>

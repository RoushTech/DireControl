<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useHealthStore } from '@/stores/health'
import { useUiStore } from '@/stores/uiStore'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { updateStationIdentity } from '@/api/stationsApi'
import { pttTest } from '@/api/radiosApi'
import { apiErrorDetail } from '@/api/axios'
import HomePicker from '@/components/HomePicker.vue'

/**
 * First-run checklist (review 1.8): Callsign → Home position → Radio and
 * audio → PTT test ("did it key?") → APRS-IS (optional). Derived live from
 * settings, radios and status; it can be hidden but never blocks anything —
 * transmit is blocked server-side while the callsign is N0CALL regardless.
 */
const health = useHealthStore()
const ui = useUiStore()
const radiosStore = useRadiosStore()
const toast = useToastStore()

const steps = computed(() => health.setupSteps)
const firstOpen = computed(() => steps.value.find((s) => !s.done && !s.optional)?.key ?? null)
const open = ref<string | null>(null)
watch(
  firstOpen,
  (k) => {
    if (open.value === null) open.value = k
  },
  { immediate: true },
)

// ── Callsign ──
const CALL_RE = /^[A-Z0-9]{3,6}(-([0-9]|1[0-5]))?$/
const callsign = ref('')
watch(
  () => health.callsign,
  (c) => {
    if (c && !callsign.value) callsign.value = c
  },
  { immediate: true },
)
const callsignRules = [
  (v: string) => !!v || 'Enter your callsign',
  (v: string) => !v.toUpperCase().startsWith('N0CALL') || 'N0CALL is a placeholder, not a callsign',
  (v: string) => CALL_RE.test(v.toUpperCase()) || 'Callsign with optional -SSID (0–15)',
]
const savingCall = ref(false)
async function saveCallsign() {
  const v = callsign.value.trim().toUpperCase()
  if (callsignRules.some((r) => r(v) !== true)) return
  savingCall.value = true
  try {
    await updateStationIdentity({
      callsign: v,
      homeLat: health.home?.lat ?? null,
      homeLon: health.home?.lon ?? null,
    })
    toast.toast(
      'Callsign saved — APRS-IS reconnects and PMS/terminal listeners restart.',
      'success',
    )
    await health.refreshSettings()
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Could not save the callsign'), 'error')
  } finally {
    savingCall.value = false
  }
}

// ── Home position ──
const home = ref<{ lat: number; lon: number } | null>(null)
watch(
  () => health.home,
  (h) => {
    if (h && !home.value) home.value = { ...h }
  },
  { immediate: true },
)
const savingHome = ref(false)
async function saveHome() {
  if (!home.value || !health.callsign) return
  savingHome.value = true
  try {
    await updateStationIdentity({
      callsign: health.callsign,
      homeLat: home.value.lat,
      homeLon: home.value.lon,
    })
    toast.toast('Home position saved.', 'success')
    await health.refreshSettings()
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Could not save the home position'), 'error')
  } finally {
    savingHome.value = false
  }
}

// ── PTT test ──
const txRadios = computed(() =>
  radiosStore.activeRadios.filter((r) => r.modem.modemEnabled && r.modem.txEnabled),
)
const pttRadioId = ref<string | null>(null)
watch(
  txRadios,
  (rs) => {
    if (!pttRadioId.value && rs[0]) pttRadioId.value = rs[0].id
  },
  { immediate: true },
)
const pttBusy = ref(false)
/** null = not run; 'ask' = keyed, waiting for the operator's "did it key?"; 'no' = operator says no. */
const pttPhase = ref<null | 'ask' | 'no'>(null)
const pttError = ref<string | null>(null)

async function runPttTest() {
  if (!pttRadioId.value) return
  pttBusy.value = true
  pttError.value = null
  pttPhase.value = null
  try {
    const r = await pttTest(pttRadioId.value)
    if (r.keyed) pttPhase.value = 'ask'
    else pttError.value = r.reason ?? 'The keyup did not complete.'
  } catch (e) {
    pttError.value = apiErrorDetail(e)
  } finally {
    pttBusy.value = false
  }
}

function confirmKeyed(yes: boolean) {
  if (yes) {
    health.markPttTested()
    pttPhase.value = null
    toast.toast('PTT confirmed — the radio keys and releases.', 'success')
  } else pttPhase.value = 'no'
}
</script>

<template>
  <div class="setup-view">
    <div class="setup-inner">
      <header class="mb-4">
        <h2 class="text-h6">Setup checklist</h2>
        <p class="text-body-2 text-medium-emphasis mb-0">
          {{ health.setupDone }} of {{ health.setupRequired.length }} required steps done. It never
          blocks anything; transmit stays blocked only while the callsign is N0CALL.
        </p>
      </header>

      <v-alert
        v-if="health.health.txBlockedReason"
        type="warning"
        variant="tonal"
        density="compact"
        class="mb-4"
      >
        Transmit is blocked: {{ health.health.txBlockedReason }}
      </v-alert>

      <v-expansion-panels v-model="open" variant="accordion" class="setup-steps">
        <v-expansion-panel v-for="(step, i) in steps" :key="step.key" :value="step.key">
          <v-expansion-panel-title>
            <div class="d-flex align-center ga-3 setup-title">
              <v-avatar
                size="30"
                :color="step.done ? 'success' : step.bad ? 'error' : undefined"
                :variant="step.done || step.bad ? 'flat' : 'outlined'"
              >
                <v-icon v-if="step.done" icon="mdi-check" size="18" />
                <v-icon v-else-if="step.bad" icon="mdi-alert" size="18" />
                <span v-else class="text-body-2">{{ i + 1 }}</span>
              </v-avatar>
              <div class="min-w-0">
                <div class="font-weight-medium">{{ step.label }}</div>
                <div class="text-caption text-medium-emphasis setup-detail">{{ step.detail }}</div>
              </div>
            </div>
          </v-expansion-panel-title>

          <v-expansion-panel-text>
            <!-- Callsign -->
            <v-form v-if="step.key === 'callsign'" @submit.prevent="saveCallsign">
              <v-text-field
                v-model="callsign"
                label="Station callsign"
                hint="Base call plus SSID, e.g. N7DCX-10. Radios inherit it unless they set their own."
                persistent-hint
                :rules="callsignRules"
                class="mb-3 font-mono"
                @update:model-value="(v: string) => (callsign = v.toUpperCase())"
              />
              <v-btn type="submit" color="primary" :loading="savingCall">Save callsign</v-btn>
            </v-form>

            <!-- Home -->
            <div v-else-if="step.key === 'home'">
              <p class="text-body-2 mb-2">
                Tap the map, type the coordinates, or enter a grid square. There is no GPS on this
                station.
              </p>
              <HomePicker v-model="home" />
              <v-btn
                color="primary"
                class="mt-3"
                :disabled="!home || !health.callsign"
                :loading="savingHome"
                @click="saveHome"
              >
                Save home position
              </v-btn>
            </div>

            <!-- Radio -->
            <div v-else-if="step.key === 'radio'">
              <p class="text-body-2 mb-3">
                Add the radio and pick its sound card (capture and playback) and PTT method in the
                radio editor.
              </p>
              <v-btn color="primary" to="/settings/radios">Open Radios</v-btn>
            </div>

            <!-- PTT -->
            <div v-else-if="step.key === 'ptt'">
              <template v-if="txRadios.length === 0">
                <p class="text-body-2 mb-3">
                  Enable TX on a sound-modem radio (with its PTT method) and save, then come back to
                  key it.
                </p>
                <v-btn color="primary" to="/settings/radios">Open Radios</v-btn>
              </template>
              <template v-else>
                <p class="text-body-2 mb-3">
                  Keys the radio for 1 s with no audio and no ID, then releases it. Watch the
                  radio's TX light.
                </p>
                <div class="d-flex flex-wrap align-center ga-3">
                  <v-select
                    v-if="txRadios.length > 1"
                    v-model="pttRadioId"
                    :items="txRadios"
                    item-title="name"
                    item-value="id"
                    label="Radio"
                    hide-details
                    density="compact"
                    class="setup-select"
                  />
                  <v-btn
                    color="primary"
                    prepend-icon="mdi-radio-handheld"
                    :loading="pttBusy"
                    :disabled="!!health.health.txBlockedReason"
                    @click="runPttTest"
                  >
                    Key for 1 s
                  </v-btn>
                </div>
                <v-alert
                  v-if="pttPhase === 'ask'"
                  type="info"
                  variant="tonal"
                  density="compact"
                  class="mt-3"
                >
                  DireControl keyed and released PTT. Did the radio actually transmit?
                  <div class="d-flex ga-2 mt-2">
                    <v-btn size="small" color="success" variant="flat" @click="confirmKeyed(true)">
                      Yes, it keyed
                    </v-btn>
                    <v-btn size="small" variant="tonal" @click="confirmKeyed(false)"> No </v-btn>
                  </div>
                </v-alert>
                <v-alert
                  v-else-if="pttPhase === 'no'"
                  type="warning"
                  variant="tonal"
                  density="compact"
                  class="mt-3"
                >
                  The software side worked but the radio didn't key: check the PTT method and its
                  device (serial port / CM108 pin / GPIO line / rigctld) in the radio editor, and
                  the cable to the radio's data jack.
                  <template #append>
                    <v-btn variant="text" to="/settings/radios">Radio editor</v-btn>
                  </template>
                </v-alert>
                <v-alert
                  v-if="pttError"
                  type="error"
                  variant="tonal"
                  density="compact"
                  class="mt-3"
                >
                  PTT test failed: {{ pttError }}
                </v-alert>
              </template>
            </div>

            <!-- APRS-IS -->
            <div v-else-if="step.key === 'aprsis'">
              <p class="text-body-2 mb-3">
                Optional. Shows stations beyond RF range and lets the station iGate. Saving tests
                the login and shows the result inline.
              </p>
              <v-btn color="primary" to="/settings/aprsis">Open APRS-IS settings</v-btn>
            </div>
          </v-expansion-panel-text>
        </v-expansion-panel>
      </v-expansion-panels>

      <div class="d-flex flex-wrap ga-2 mt-4">
        <v-btn
          v-if="!ui.setupDismissed && !health.setupComplete"
          variant="text"
          @click="ui.setupDismissed = true"
        >
          Hide setup reminders
        </v-btn>
        <v-btn v-else-if="ui.setupDismissed" variant="text" @click="ui.setupDismissed = false">
          Show setup reminders again
        </v-btn>
      </div>
    </div>
  </div>
</template>

<style scoped>
.setup-view {
  height: 100%;
  overflow-y: auto;
}
.setup-inner {
  max-width: 760px;
  margin: 0 auto;
  padding: 24px 16px;
}
.setup-title {
  min-width: 0;
}
.min-w-0 {
  min-width: 0;
}
.setup-detail {
  white-space: normal;
}
.setup-select {
  max-width: 240px;
}
.font-mono :deep(input) {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
}
</style>

<script setup lang="ts">
import { audioLevelColor } from '@/utils/audioLevel'
import { ref, shallowRef, onMounted, onUnmounted, computed, watch, nextTick } from 'vue'
import {
  getRadios,
  createRadio,
  updateRadio,
  deleteRadio,
  toggleRadioActive,
} from '@/api/radiosApi'
import { defaultRadioModemConfig, type RadioDto, type RadioModemConfig } from '@/types/radio'
import {
  getModemDevices,
  sendTestTone,
  setModemTxLevel,
  decodeSpectrumPayload,
  PttMethods,
  TestToneKinds,
  type ModemDevicesDto,
  type ModemLevelDto,
  type ModemSpectrumDto,
  type TestToneKind,
} from '@/api/modemApi'
import { apiErrorDetail } from '@/api/axios'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { useTxStore } from '@/stores/tx'
import { useHealthStore } from '@/stores/health'
import { TxFrameStates } from '@/api/txApi'
import WaterfallCanvas from '@/components/WaterfallCanvas.vue'
import { getSymbolStyle } from '@/utils/aprsIcon'
import AprsSymbolPicker from '@/components/AprsSymbolPicker.vue'
import SettingsSaveBar from './SettingsSaveBar.vue'
import ConfirmDeleteDialog from './ConfirmDeleteDialog.vue'
import { settingsSection } from './sections'

const meta = settingsSection('radios')

// ─── Modem device lists (for the per-radio dialog) ───────────────────────────
const modemDevices = ref<ModemDevicesDto>({ audio: [], serialPorts: [], hidDevices: [] })

const pttMethodItems = [
  { title: 'None (VOX)', value: PttMethods.None },
  { title: 'Serial RTS/DTR', value: PttMethods.SerialRtsDtr },
  { title: 'CM108 USB HID (DigiRig)', value: PttMethods.Cm108 },
  { title: 'Linux GPIO', value: PttMethods.Gpio },
  { title: 'Hamlib rigctld', value: PttMethods.Rigctld },
]

/** Receive bit repair depth: how many doubtful bits the modem may correct on a bad checksum. */
const rxBitRepairItems = [
  { title: 'Off', value: 0 },
  { title: '1 bit (recommended)', value: 1 },
  { title: '2 bits', value: 2 },
]
const rxBitRepairHint = computed(() => {
  switch (rModem.value.rxBitRepair) {
    case 0:
      return 'Packets that fail their checksum are dropped.'
    case 2:
      return 'Recovers the most weak packets, but about 1 in 400 recovered packets is wrong. Recovered packets are marked.'
    default:
      return 'Recovers weak packets that fail their checksum by one doubtful bit. Recovered packets are marked.'
  }
})

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

// ─── Radios ───────────────────────────────────────────────────────────────────
const radios = ref<RadioDto[]>([])
const radioDialogOpen = ref(false)
const editingRadioId = ref<string | null>(null)
const radioSaving = ref(false)
const radioSaveError = ref('')
const radioFormDirty = ref(false)

// ─── Live calibration (edit dialog) ──────────────────────────────────────────
// While editing a saved radio, the dialog shows the live meter + waterfall so
// audio levels can be set against what the modem actually hears/sends.
const packetHub = usePacketHubStore()
const radiosStore = useRadiosStore()
const toastStore = useToastStore()
const txStore = useTxStore()
const healthStore = useHealthStore()

/** A radio save restarts that radio's modem session (KISS clients stay connected). */
function announceModemRestart(what: string) {
  toastStore.toast(`${what} — restarting that radio's modem…`, 'info', 4000)
}
const editorLevel = ref<ModemLevelDto | null>(null)
const editorWaterfall = ref<InstanceType<typeof WaterfallCanvas> | null>(null)

function onEditorModemLevel(batch: ModemLevelDto[]) {
  const level = batch.find((l) => l.radioId === editingRadioId.value)
  if (level) editorLevel.value = level
}

function onEditorModemSpectrum(batch: ModemSpectrumDto[]) {
  for (const spectrum of batch) {
    if (spectrum.radioId === editingRadioId.value) {
      editorWaterfall.value?.drawRow(decodeSpectrumPayload(spectrum.bins))
    }
  }
}

watch(radioDialogOpen, (open) => {
  if (open) radioSection.value = 'identity'
  if (open && editingRadioId.value) {
    packetHub.on('modemLevel', onEditorModemLevel)
    packetHub.on('modemSpectrum', onEditorModemSpectrum)
  } else {
    packetHub.off('modemLevel', onEditorModemLevel)
    packetHub.off('modemSpectrum', onEditorModemSpectrum)
    editorLevel.value = null
  }
})

// Test-tone calibration — only for a saved radio with a live TX-enabled modem.
const toneSending = ref(false)
const TEST_TONE_MS = 2000

async function doTestTone(kind: TestToneKind) {
  if (!editingRadioId.value) return
  toneSending.value = true
  try {
    const res = await sendTestTone(editingRadioId.value, kind, TEST_TONE_MS)
    const f = res.txFrameIds[0] ? await txStore.waitFor(res.txFrameIds[0], 30_000) : undefined
    if (f && f.state !== TxFrameStates.Sent)
      toastStore.toast(`Test tone not sent: ${f.reason ?? 'unknown reason'}`, 'error', 10_000)
  } catch (e) {
    toastStore.toast(apiErrorDetail(e, 'Test tone failed'), 'error')
  } finally {
    setTimeout(() => {
      toneSending.value = false
    }, TEST_TONE_MS)
  }
}
/** PTT/TX fields differ from what is saved — the test path uses the saved config. */
const pttFieldsDirty = computed(() => {
  const saved = radios.value.find((r) => r.id === editingRadioId.value)?.modem
  if (!saved) return true
  const keys = [
    'txEnabled',
    'modemPlaybackDevice',
    'pttMethod',
    'pttSerialPort',
    'pttSerialUseRts',
    'pttSerialUseDtr',
    'pttHidDevice',
    'pttHidPin',
    'pttGpioChip',
    'pttGpioLine',
    'pttGpioActiveLow',
    'pttRigctldHost',
    'pttRigctldPort',
  ] as const
  return keys.some((k) => (rModem.value[k] ?? null) !== (saved[k] ?? null))
})
const pttTesting = ref(false)

async function doTestPtt() {
  if (!editingRadioId.value) return
  pttTesting.value = true
  try {
    const res = await sendTestTone(editingRadioId.value, TestToneKinds.PttOnly, 1000)
    // Queued is not keyed: wait for the keyup to actually finish.
    const f = res.txFrameIds[0] ? await txStore.waitFor(res.txFrameIds[0], 30_000) : undefined
    if (f?.state === TxFrameStates.Sent) {
      toastStore.toast('Keyed ✓ — PTT held for 1 s and released.', 'success', 5000)
      healthStore.markPttTested()
    } else
      toastStore.toast(
        `PTT test did not key: ${f?.reason ?? 'no result within 30 s'}`,
        'error',
        10_000,
      )
  } catch (e) {
    toastStore.toast(`PTT test failed: ${apiErrorDetail(e)}`, 'error')
  } finally {
    setTimeout(() => {
      pttTesting.value = false
    }, 1000)
  }
}

// Unsaved-changes guard: the dialog is persistent, so outside clicks and
// Escape no longer throw edits away; Cancel asks when the form is dirty.
const discardConfirmOpen = ref(false)

function requestCloseRadioDialog() {
  if (radioFormDirty.value) discardConfirmOpen.value = true
  else radioDialogOpen.value = false
}

function discardRadioChanges() {
  discardConfirmOpen.value = false
  radioDialogOpen.value = false
}

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

// TX gain applies live while dragging (debounced) so the change is audible on
// the next test tone — and it still saves with the radio. Must be registered
// after the rModem declaration: watch() invokes the getter synchronously.
let txGainTimer: ReturnType<typeof setTimeout> | null = null
watch(
  () => rModem.value.txAudioLevelPct,
  (value) => {
    if (!radioDialogOpen.value || !editingRadioId.value) return
    if (txGainTimer) clearTimeout(txGainTimer)
    txGainTimer = setTimeout(() => {
      setModemTxLevel(editingRadioId.value!, Math.round(value)).catch(() => {
        /* transient — next adjustment retries */
      })
    }, 250)
  },
)

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

// ─── Radio editor sections — rail + pane, not one long scroll ────────────────
type RadioSectionKey = 'identity' | 'beaconing' | 'modem' | 'calibration' | 'ptt'
const radioSection = ref<RadioSectionKey>('identity')

const radioSectionItems = computed(() => [
  {
    key: 'identity' as const,
    label: 'Identity',
    icon: 'mdi-card-account-details-outline',
    disabled: false,
    alert: !radioFormValid.value || !!ssidError.value,
  },
  {
    key: 'beaconing' as const,
    label: 'Beaconing',
    icon: 'mdi-broadcast',
    disabled: false,
    alert: false,
  },
  {
    key: 'modem' as const,
    label: 'Sound Modem',
    icon: 'mdi-waveform',
    disabled: false,
    alert: false,
  },
  {
    key: 'calibration' as const,
    label: 'Calibration',
    icon: 'mdi-tune',
    disabled: !rModem.value.modemEnabled,
    alert: false,
  },
  {
    key: 'ptt' as const,
    label: 'PTT & Timing',
    icon: 'mdi-serial-port',
    disabled: !(rModem.value.modemEnabled && rModem.value.txEnabled),
    alert: false,
  },
])

// If the active section becomes unavailable (modem or TX switched off), fall
// back to the Sound Modem pane where those switches live.
watch(radioSectionItems, (items) => {
  if (items.find((s) => s.key === radioSection.value)?.disabled) radioSection.value = 'modem'
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
    announceModemRestart(`${payload.name} saved`)
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

// Active switch asks first — turning a radio off stops its RX and TX.
const toggleTarget = ref<RadioDto | null>(null)
const toggleConfirmOpen = ref(false)
const toggleBusy = ref(false)
const toggleError = ref('')

function requestToggleActive(radio: RadioDto) {
  toggleTarget.value = radio
  toggleError.value = ''
  toggleConfirmOpen.value = true
}

async function confirmToggleActive() {
  const radio = toggleTarget.value
  if (!radio) return
  toggleBusy.value = true
  toggleError.value = ''
  try {
    const updated = await toggleRadioActive(radio.id)
    const idx = radios.value.findIndex((r) => r.id === radio.id)
    if (idx !== -1) radios.value[idx] = updated
    toggleConfirmOpen.value = false
    announceModemRestart(`${radio.name} ${updated.isActive ? 'started' : 'stopped'}`)
  } catch (e) {
    toggleError.value = apiErrorDetail(e)
  } finally {
    toggleBusy.value = false
  }
}

// ─── Beacon now ───────────────────────────────────────────────────────────────
const beaconing = ref<Record<string, boolean>>({})

async function doBeaconNow(radio: RadioDto) {
  beaconing.value[radio.id] = true
  try {
    await txStore.beaconNow(radio.id, radio.fullCallsign)
  } finally {
    beaconing.value[radio.id] = false
  }
}

// ─── Delete (shared confirm dialog handles radios too) ────────────────────────
function promptDeleteRadio(radio: RadioDto) {
  const historyNote =
    radio.beaconCount > 0
      ? ` This radio has ${radio.beaconCount} beacon records. Deleting will remove all history.`
      : ''
  deleteConfirmMessage.value = `Delete radio "${radio.name}"?${historyNote} This cannot be undone.`
  deleteAction.value = async () => {
    await deleteRadio(radio.id)
    radios.value = radios.value.filter((r) => r.id !== radio.id)
    announceModemRestart(`${radio.name} deleted`)
  }
  deleteConfirmOpen.value = true
}

const deleteConfirmOpen = ref(false)
const deleteConfirmMessage = ref('')
const deleteAction = shallowRef<(() => Promise<void>) | null>(null)

onMounted(async () => {
  try {
    modemDevices.value = await getModemDevices()
  } catch {
    /* device lists stay empty; fields still accept typed names */
  }
  await loadRadios()
})

onUnmounted(() => {
  packetHub.off('modemLevel', onEditorModemLevel)
  packetHub.off('modemSpectrum', onEditorModemSpectrum)
  if (txGainTimer) clearTimeout(txGainTimer)
})
</script>

<template>
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
              <v-chip v-if="radio.modem.modemEnabled" size="x-small" variant="tonal" color="green">
                {{ radio.modem.txEnabled ? 'RX/TX' : 'RX' }}
              </v-chip>
              <span v-else class="text-medium-emphasis">—</span>
            </td>
            <td>{{ radio.beaconCount }}</td>
            <td>
              <v-switch
                :model-value="radio.isActive"
                color="success"
                density="compact"
                hide-details
                inset
                :aria-label="`${radio.name} active`"
                @update:model-value="requestToggleActive(radio)"
              />
            </td>
            <td class="text-right" style="white-space: nowrap">
              <v-btn
                :aria-label="`Beacon now as ${radio.fullCallsign}`"
                icon="mdi-access-point"
                size="small"
                variant="text"
                color="primary"
                :loading="beaconing[radio.id]"
                :disabled="!radio.isActive"
                @click="doBeaconNow(radio)"
              />
              <v-btn
                :aria-label="`Edit ${radio.name}`"
                icon="mdi-pencil"
                size="small"
                variant="text"
                @click="openEditRadio(radio)"
              />
              <v-btn
                :aria-label="`Delete ${radio.name}`"
                icon="mdi-delete"
                size="small"
                variant="text"
                color="error"
                @click="promptDeleteRadio(radio)"
              />
            </td>
          </tr>
        </tbody>
      </v-table>
      <div v-else class="text-center text-medium-emphasis py-6">
        <div class="mb-3">No radios configured — add your radio to start decoding.</div>
        <v-btn color="primary" variant="tonal" prepend-icon="mdi-plus" @click="openAddRadio">
          Add Radio
        </v-btn>
      </div>
    </v-card>
    <SettingsSaveBar :restart="meta.restart" />
  </div>
  <!-- ── Dialogs ── -->
  <!-- Add / Edit Radio dialog -->
  <v-dialog v-model="radioDialogOpen" max-width="880" scrollable persistent>
    <v-card>
      <v-card-title class="d-flex align-center ga-2">
        {{ editingRadioId ? 'Edit Radio' : 'Add Radio' }}
        <span v-if="computedFullCallsign" class="text-caption text-medium-emphasis">
          {{ computedFullCallsign }}
        </span>
      </v-card-title>
      <v-divider />
      <v-card-text class="pa-0">
        <div class="radio-dialog-layout">
          <!-- Section rail — grouped panes instead of one long scroll -->
          <nav class="radio-dialog-rail">
            <button
              v-for="s in radioSectionItems"
              :key="s.key"
              type="button"
              class="radio-rail-btn"
              :class="{ 'radio-rail-btn--active': radioSection === s.key }"
              :disabled="s.disabled"
              @click="radioSection = s.key"
            >
              <v-icon size="16" class="mr-2">{{ s.icon }}</v-icon>
              {{ s.label }}
              <span v-if="s.alert" class="radio-rail-dot" />
            </button>
          </nav>

          <div class="radio-dialog-pane">
            <!-- Duplicate warning -->
            <v-alert
              v-if="duplicateRadio"
              type="warning"
              variant="tonal"
              density="compact"
              class="mb-3"
            >
              {{ computedFullCallsign }} is already configured as "{{ duplicateRadio.name }}". Are
              you sure?
            </v-alert>

            <template v-if="radioSection === 'identity'">
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
                    :rules="[
                      (v: string) => /^[A-Z0-9]{3,6}$/i.test(v.trim()) || '3–6 letters/digits',
                    ]"
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
                  <v-text-field
                    v-model="rMode"
                    label="Mode"
                    density="compact"
                    placeholder="e.g. FM"
                  />
                  <v-text-field
                    v-model.number="rExpectedInterval"
                    label="Expected beacon interval (s)"
                    density="compact"
                    type="number"
                  />
                  <v-text-field v-model="rNotes" label="Notes" density="compact" />
                </div>
              </div>
            </template>

            <template v-if="radioSection === 'beaconing'">
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
                  <v-text-field
                    v-model="rBeaconComment"
                    label="Comment"
                    density="compact"
                    hint="{version} expands to the app version; blank sends “DireControl v{version}”"
                    persistent-hint
                  />
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
            </template>

            <template v-if="radioSection === 'modem'">
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
                    <v-combobox
                      v-if="rModem.txEnabled"
                      v-model="rModem.modemPlaybackDevice"
                      :items="modemPlaybackDeviceItems"
                      label="Playback device"
                      density="compact"
                      :return-object="false"
                    />
                  </div>
                  <div class="radio-form-row">
                    <v-select
                      v-model="rModem.rxBitRepair"
                      :items="rxBitRepairItems"
                      label="Bit repair"
                      density="compact"
                      :hint="rxBitRepairHint"
                      persistent-hint
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
                  <div class="text-caption text-medium-emphasis">
                    Audio levels, waterfall, and test tones live in the Calibration section.
                  </div>
                </template>
              </div>
            </template>

            <template v-if="radioSection === 'calibration'">
              <div class="radio-form-section">
                <div class="text-subtitle-2 font-weight-medium">Calibration</div>
                <div class="text-caption text-medium-emphasis mb-2">
                  Set levels against the live modem — what it hears and what it sends.
                </div>

                <!-- Live calibration — mirrors the Radio page so levels are set
                   against what the modem actually hears and sends. -->
                <div v-if="editingRadioId" class="calibration-box">
                  <div class="d-flex align-center ga-2 mb-2">
                    <v-chip
                      v-if="
                        editingRadioId && radiosStore.carrierHeldSeconds(editingRadioId) !== null
                      "
                      color="warning"
                      size="x-small"
                      variant="tonal"
                      title="Carrier is on with nothing decoding — squelch open or input too hot"
                    >
                      carrier held {{ radiosStore.carrierHeldSeconds(editingRadioId) }} s
                    </v-chip>
                    <v-chip
                      v-else
                      :color="editorLevel?.carrierDetected ? 'success' : 'grey'"
                      size="x-small"
                      variant="tonal"
                    >
                      {{ editorLevel?.carrierDetected ? 'carrier' : 'quiet' }}
                    </v-chip>
                    <v-chip
                      v-if="editorLevel?.transmitting"
                      color="error"
                      size="x-small"
                      variant="tonal"
                    >
                      TX
                    </v-chip>
                  </div>

                  <div class="d-flex align-center justify-space-between mb-1">
                    <span class="text-caption text-medium-emphasis">Audio level (RX)</span>
                    <span class="text-caption text-medium-emphasis">
                      {{ (editorLevel?.audioLevel ?? 0).toFixed(2) }}
                    </span>
                  </div>
                  <v-progress-linear
                    :model-value="Math.min(100, (editorLevel?.audioLevel ?? 0) * 100)"
                    :color="audioLevelColor(editorLevel?.audioLevel ?? 0)"
                    height="6"
                    rounded
                    class="mb-3"
                  />

                  <WaterfallCanvas ref="editorWaterfall" :height="80" class="mb-3" />

                  <template v-if="rModem.txEnabled">
                    <div class="d-flex align-center ga-2 mb-1 flex-nowrap">
                      <span class="text-caption text-medium-emphasis flex-shrink-0">TX gain</span>
                      <v-slider
                        v-model.number="rModem.txAudioLevelPct"
                        :min="1"
                        :max="100"
                        :step="1"
                        color="primary"
                        density="compact"
                        hide-details
                        thumb-label
                      />
                      <span class="text-caption text-medium-emphasis flex-shrink-0">
                        {{ Math.round(rModem.txAudioLevelPct) }}%
                      </span>
                    </div>
                    <div class="d-flex align-center ga-2 flex-wrap">
                      <span class="text-caption text-medium-emphasis">Test tones</span>
                      <v-btn
                        size="small"
                        variant="outlined"
                        class="px-4"
                        :loading="toneSending"
                        @click="doTestTone(TestToneKinds.Mark)"
                      >
                        Mark
                      </v-btn>
                      <v-btn
                        size="small"
                        variant="outlined"
                        class="px-4"
                        :disabled="toneSending"
                        @click="doTestTone(TestToneKinds.Space)"
                      >
                        Space
                      </v-btn>
                      <v-btn
                        size="small"
                        variant="outlined"
                        class="px-4"
                        :disabled="toneSending"
                        @click="doTestTone(TestToneKinds.Alternating)"
                      >
                        Alt
                      </v-btn>
                      <span class="text-caption text-medium-emphasis">
                        keys TX ~2s · gain applies live while you drag
                      </span>
                    </div>
                    <div class="d-flex align-center ga-2 flex-wrap mt-2">
                      <v-btn
                        size="small"
                        variant="outlined"
                        prepend-icon="mdi-radio-handheld"
                        :disabled="pttFieldsDirty || toneSending"
                        :loading="pttTesting"
                        @click="doTestPtt"
                      >
                        Test PTT (key 1 s)
                      </v-btn>
                      <span v-if="pttFieldsDirty" class="text-caption text-warning">
                        Save first — the test uses the saved PTT settings
                      </span>
                      <span v-else class="text-caption text-medium-emphasis">
                        no tone — wiring check
                      </span>
                    </div>
                  </template>
                </div>

                <template v-else>
                  <v-alert type="info" variant="tonal" density="compact" class="mb-3">
                    Save the radio first — the live meter and waterfall need the modem running on
                    the saved config.
                  </v-alert>
                  <div v-if="rModem.txEnabled" class="radio-form-row">
                    <v-text-field
                      v-model.number="rModem.txAudioLevelPct"
                      label="TX level (%)"
                      density="compact"
                      type="number"
                      hint="Starting TX gain — calibrate live after saving"
                      persistent-hint
                    />
                  </div>
                </template>
              </div>
            </template>

            <template v-if="radioSection === 'ptt'">
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
                  size="small"
                  variant="text"
                  color="primary"
                  class="mt-2"
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
                  <v-text-field
                    v-model.number="rModem.txSpaceBoostDb"
                    label="Space boost (dB)"
                    density="compact"
                    type="number"
                    min="0"
                    max="6"
                    step="0.5"
                    :rules="[(v: number) => (v >= 0 && v <= 6) || '0–6 dB']"
                    hint="2200 Hz louder than 1200 Hz. 0 = equal; ~4–5 for a flat data jack"
                    persistent-hint
                  />
                </div>
              </div>
            </template>
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
        <span class="text-caption text-medium-emphasis d-inline-flex align-center ml-2">
          <v-icon size="14" class="mr-1">mdi-restart</v-icon>{{ meta.restart }}
        </span>
        <v-spacer />
        <span v-if="radioFormDirty" class="text-caption text-warning font-weight-medium mr-2">
          Unsaved changes
        </span>
        <v-btn variant="text" @click="requestCloseRadioDialog">Cancel</v-btn>
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

  <!-- Discard unsaved radio edits? -->
  <v-dialog v-model="discardConfirmOpen" max-width="420">
    <v-card>
      <v-card-title class="text-subtitle-1">Discard unsaved changes?</v-card-title>
      <v-card-text>This radio has edits that have not been saved.</v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="discardConfirmOpen = false">Keep editing</v-btn>
        <v-btn color="error" variant="tonal" @click="discardRadioChanges">Discard</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <!-- Turn a radio on/off? -->
  <v-dialog v-model="toggleConfirmOpen" max-width="440">
    <v-card v-if="toggleTarget">
      <v-card-title class="text-subtitle-1">
        {{ toggleTarget.isActive ? 'Turn off' : 'Turn on' }} {{ toggleTarget.name }}?
      </v-card-title>
      <v-card-text>
        {{
          toggleTarget.isActive
            ? 'Stops receive and transmit on this radio. Its modem session restarts; other radios are unaffected.'
            : 'Starts the modem on this radio.'
        }}
        <v-alert v-if="toggleError" type="error" variant="tonal" density="compact" class="mt-3">
          {{ toggleError }}
        </v-alert>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="toggleConfirmOpen = false">Cancel</v-btn>
        <v-btn
          :color="toggleTarget.isActive ? 'error' : 'primary'"
          variant="tonal"
          :loading="toggleBusy"
          @click="confirmToggleActive"
        >
          {{ toggleTarget.isActive ? 'Turn off' : 'Turn on' }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <ConfirmDeleteDialog
    v-model="deleteConfirmOpen"
    :message="deleteConfirmMessage"
    :action="deleteAction"
  />
</template>

<style scoped>
.calibration-box {
  border: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  border-radius: 10px;
  padding: 12px 14px;
  margin-bottom: 8px;
  background: rgba(var(--v-theme-on-surface), 0.03);
}

/* ── Radio editor: section rail + pane ── */
.radio-dialog-layout {
  display: flex;
  height: min(600px, 78vh);
}

.radio-dialog-rail {
  width: 172px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 10px 8px;
  border-right: 1px solid rgba(var(--v-theme-on-surface), 0.12);
}

.radio-rail-btn {
  position: relative;
  display: flex;
  align-items: center;
  text-align: left;
  padding: 8px 10px;
  border: none;
  border-radius: 8px;
  background: none;
  font-size: 0.83rem;
  font-weight: 500;
  color: rgba(var(--v-theme-on-surface), 0.7);
  cursor: pointer;
  transition:
    background 0.15s,
    color 0.15s;
}

.radio-rail-btn:hover:not(:disabled) {
  background: rgba(var(--v-theme-on-surface), 0.06);
}

.radio-rail-btn:disabled {
  opacity: 0.38;
  cursor: default;
}

.radio-rail-btn--active {
  background: rgba(var(--v-theme-primary), 0.12);
  color: rgb(var(--v-theme-primary));
  font-weight: 600;
}

.radio-rail-dot {
  position: absolute;
  right: 10px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: rgb(var(--v-theme-warning));
}

.radio-dialog-pane {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 16px;
}

@media (max-width: 640px) {
  .radio-dialog-layout {
    flex-direction: column;
    height: 78vh;
  }

  .radio-dialog-rail {
    width: 100%;
    flex-direction: row;
    overflow-x: auto;
    padding: 6px 8px;
    border-right: none;
    border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  }

  .radio-rail-btn {
    flex-shrink: 0;
  }

  .radio-rail-dot {
    position: static;
    margin-left: 6px;
  }
}

.radio-form-section {
  margin-bottom: 18px;
}

.radio-form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  /* Real row gap — persistent hints hang below the input and collide with the
     next row's label without it. */
  gap: 10px 12px;
  align-items: start;
}

.radio-symbol-icon {
  image-rendering: pixelated;
  flex-shrink: 0;
}
</style>

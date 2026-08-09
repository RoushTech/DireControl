<script setup lang="ts">
import { ref, reactive, computed, onMounted, onUnmounted } from 'vue'

// Named so <keep-alive include="RadioView"> in App.vue keeps this view (and its
// live SignalR connection, packet buffer, and waterfall history) mounted while
// the user navigates elsewhere.
defineOptions({ name: 'RadioView' })
import { useRouter } from 'vue-router'
import {
  getModemStatus,
  setModemTxLevel,
  sendTestTone,
  restartModem,
  TestToneKinds,
  decodeSpectrumPayload,
  ModemStates,
  modemStateLabels,
  PttMethods,
  type ModemLevelDto,
  type ModemSpectrumDto,
  type ModemStatusDto,
  type TestToneKind,
} from '@/api/modemApi'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { getStatus, type StatusDto } from '@/api/statusApi'
import { getSettings, getPacketsSince } from '@/api/stationsApi'
import { getRadios, beaconNow } from '@/api/radiosApi'
import type { RadioDto, LastBeaconDto } from '@/types/radio'
import type { SettingsDto } from '@/types/station'
import {
  PACKET_TYPE_LABELS,
  parsedTypeFromString,
  packetDtoToBroadcast,
  PacketSource,
  type PacketBroadcastDto,
} from '@/types/packet'
import WaterfallCanvas from '@/components/WaterfallCanvas.vue'
import PacketInspectionDialog from '@/components/PacketInspectionDialog.vue'
import BeaconHistoryModal from '@/components/BeaconHistoryModal.vue'
import { timeAgo, formatUtc } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'

const MAX_FEED = 100
const router = useRouter()
const { now } = useTick(5000)
const hub = usePacketHubStore()
const radiosStore = useRadiosStore()
const toastStore = useToastStore()

const status = ref<StatusDto | null>(null)
const modemStatuses = ref<ModemStatusDto[]>([])
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[]>([])
const packets = ref<PacketBroadcastDto[]>([])

// Live meters per radio — updated at 10 Hz over SignalR, keyed by radio id.
const levels = ref<Record<string, ModemLevelDto>>({})

// TX audio level (gain) per radio, adjustable live. Seeded from each radio's
// persisted config; writes are debounced so dragging the slider doesn't flood
// the API, and applied live by the backend with no modem restart.
const txGain = reactive<Record<string, number>>({})
const txGainTimers: Record<string, ReturnType<typeof setTimeout>> = {}

function seedTxGain() {
  for (const r of radios.value) txGain[r.id] = r.modem.txAudioLevelPct
}

function onTxGainInput(radioId: string, value: number) {
  txGain[radioId] = value
  clearTimeout(txGainTimers[radioId])
  txGainTimers[radioId] = setTimeout(() => {
    setModemTxLevel(radioId, Math.round(value)).catch(() => {
      /* transient — the next adjustment retries */
    })
  }, 200)
}

// Which radio is currently sending a test tone (disables its buttons briefly).
const toneSending = reactive<Record<string, boolean>>({})
const TEST_TONE_MS = 2000

async function doTestTone(radioId: string, kind: TestToneKind) {
  toneSending[radioId] = true
  try {
    await sendTestTone(radioId, kind, TEST_TONE_MS)
  } catch {
    /* surfaced by modem status / logs */
  } finally {
    // Re-enable after roughly the tone duration so the buttons reflect PTT.
    setTimeout(() => {
      toneSending[radioId] = false
    }, TEST_TONE_MS)
  }
}

type WaterfallInstance = InstanceType<typeof WaterfallCanvas>
const waterfalls = new Map<string, WaterfallInstance>()

function setWaterfallRef(radioId: string, el: unknown) {
  if (el) waterfalls.set(radioId, el as WaterfallInstance)
  else waterfalls.delete(radioId)
}

let statusTimer: ReturnType<typeof setInterval> | null = null

// ── Modem restart ─────────────────────────────────────────────────────────────
const modemRestarting = ref(false)

async function doRestartModem() {
  modemRestarting.value = true
  try {
    await restartModem()
    toastStore.toast('Modem restarting — audio devices are being re-opened…', 'info')
    // Give the backend a moment, then refresh so the state chip catches up.
    setTimeout(refresh, 1500)
  } catch {
    toastStore.toast('Modem restart failed — the backend may be unreachable.', 'error')
  } finally {
    modemRestarting.value = false
  }
}

// ── Beacon now ────────────────────────────────────────────────────────────────
const beaconing = reactive<Record<string, boolean>>({})

async function doBeaconNow(m: ModemStatusDto) {
  beaconing[m.radioId] = true
  try {
    await beaconNow(m.radioId)
    toastStore.toast(`Beacon sent for ${m.fullCallsign}.`, 'success')
    void radiosStore.fetchLastBeacon(m.radioId)
  } catch {
    toastStore.toast(
      `Beacon failed for ${m.fullCallsign} — check that TX is enabled and home position is set.`,
      'error',
    )
  } finally {
    beaconing[m.radioId] = false
  }
}

// ── Beacon history modal ──────────────────────────────────────────────────────
const historyOpen = ref(false)
const historyRadio = ref<{ id: string; name: string } | null>(null)

function openHistory(m: ModemStatusDto) {
  historyRadio.value = { id: m.radioId, name: m.radioName }
  historyOpen.value = true
}

// ── Packet inspection ─────────────────────────────────────────────────────────
const inspectedPacketId = ref<number | null>(null)

function goToStation(callsign: string) {
  router.push(`/stations/${encodeURIComponent(callsign)}`)
}

// ── Shared hub handlers (named so they can be unregistered on unmount) ───────
function onHubModemLevel(batch: ModemLevelDto[]) {
  for (const level of batch) levels.value[level.radioId] = level
}

function onHubModemSpectrum(batch: ModemSpectrumDto[]) {
  for (const spectrum of batch)
    waterfalls.get(spectrum.radioId)?.drawRow(decodeSpectrumPayload(spectrum.bins))
}

function onHubModemStatusChanged(statuses: ModemStatusDto[]) {
  modemStatuses.value = statuses
}

function onHubPacketReceived(p: PacketBroadcastDto) {
  packets.value.unshift(p)
  if (packets.value.length > MAX_FEED) packets.value.splice(MAX_FEED)
}

function onHubPacketSourceUpgraded(upgrade: { id: number; source: PacketBroadcastDto['source'] }) {
  const entry = packets.value.find((p) => p.id === upgrade.id)
  if (entry) entry.source = upgrade.source
}

/** Radios without a modem instance — shown in the plain radio list. */
const radiosWithoutModem = computed(() =>
  radios.value.filter((r) => !modemStatuses.value.some((m) => m.radioId === r.id)),
)

/** Mock wording: a running modem is "Receiving". */
function stateLabel(m: ModemStatusDto): string {
  if (m.state === ModemStates.Running) return 'Receiving'
  return modemStateLabels[m.state] ?? 'Unknown'
}

function stateColor(m: ModemStatusDto): string {
  switch (m.state) {
    case ModemStates.Running:
      return 'success'
    case ModemStates.Error:
      return 'error'
    default:
      return 'grey'
  }
}

function radioFor(m: ModemStatusDto): RadioDto | undefined {
  return radios.value.find((r) => r.id === m.radioId)
}

/** One compact identity line, mock-style: "W3UWU · ch 0 · 144.390 MHz FM". */
function identityLabel(m: ModemStatusDto): string {
  const parts = [m.fullCallsign, `ch ${m.channel}`]
  const freq = frequencyLabel(m)
  if (freq) parts.push(freq)
  return parts.join(' · ')
}

/** Rigctld live frequency wins; manual radio frequency is the fallback. */
function frequencyLabel(m: ModemStatusDto): string | null {
  if (m.rigFrequencyHz) return `${(m.rigFrequencyHz / 1_000_000).toFixed(4)} MHz`
  const radio = radioFor(m)
  if (radio?.frequencyMhz) {
    const mode = radio.mode ? ` ${radio.mode}` : ''
    return `${radio.frequencyMhz.toFixed(3)} MHz${mode}`
  }
  return null
}

const PTT_LABELS: Record<number, string> = {
  [PttMethods.None]: 'VOX / none',
  [PttMethods.SerialRtsDtr]: 'Serial RTS/DTR',
  [PttMethods.Cm108]: 'CM108 HID',
  [PttMethods.Gpio]: 'GPIO',
  [PttMethods.Rigctld]: 'rigctld',
}

function pttLabel(m: ModemStatusDto): string | null {
  const radio = radioFor(m)
  if (!radio || !radio.modem.txEnabled) return null
  const label = PTT_LABELS[radio.modem.pttMethod] ?? null
  if (radio.modem.pttMethod === PttMethods.SerialRtsDtr && radio.modem.pttSerialPort) {
    return `${label} · ${radio.modem.pttSerialPort.replace('/dev/', '')}`
  }
  return label
}

function lastBeaconFor(m: ModemStatusDto): LastBeaconDto | undefined {
  return radiosStore.getLastBeaconForRadio(m.radioId)
}

function lastBeaconLabel(m: ModemStatusDto): string {
  const b = lastBeaconFor(m)
  if (!b?.beaconedAt) return 'never'
  return `${timeAgo(b.beaconedAt, now.value)}${b.heard ? ' · heard ✓' : ''}`
}

function levelFor(m: ModemStatusDto): ModemLevelDto {
  return (
    levels.value[m.radioId] ?? {
      radioId: m.radioId,
      channel: m.channel,
      audioLevel: m.audioLevel,
      carrierDetected: m.carrierDetected,
      transmitting: m.transmitting,
    }
  )
}

function levelColor(level: number): string {
  if (level > 0.9) return 'error'
  if (level > 0.05) return 'success'
  return 'grey'
}

function typeLabel(p: PacketBroadcastDto): string {
  return PACKET_TYPE_LABELS[parsedTypeFromString(p.parsedType)] ?? 'Unknown'
}

function sourceLabel(p: PacketBroadcastDto): string {
  return p.source === PacketSource.AprsIs ? 'IS' : 'RF'
}

// Staleness tracking: the page keeps showing last-known data on failure, but
// says so instead of silently presenting stale numbers as live.
const lastRefreshAt = ref<number | null>(null)
const refreshFailed = ref(false)
const radiosLoadFailed = ref(false)

const staleLabel = computed(() => {
  if (lastRefreshAt.value === null) return 'never loaded'
  return `data from ${timeAgo(new Date(lastRefreshAt.value).toISOString(), now.value)}`
})

const updatedLabel = computed(() => {
  if (lastRefreshAt.value === null) return ''
  return `updated ${timeAgo(new Date(lastRefreshAt.value).toISOString(), now.value)}`
})

async function refresh() {
  try {
    const [s, m] = await Promise.all([getStatus(), getModemStatus()])
    status.value = s
    modemStatuses.value = m
    lastRefreshAt.value = serverNow()
    refreshFailed.value = false
  } catch {
    refreshFailed.value = true
  }
}

// RF stack entries as name + chip + caption, mock-style.
interface RfStackEntry {
  name: string
  chip: string
  color: string
  note: string
}

const rfStack = computed<RfStackEntry[]>(() => {
  const s = status.value
  const cfg = settings.value
  const entries: RfStackEntry[] = []

  const running = modemStatuses.value.filter((m) => m.state === ModemStates.Running)
  const decoded = modemStatuses.value.reduce((sum, m) => sum + m.decodedFrames, 0)
  const transmitted = modemStatuses.value.reduce((sum, m) => sum + m.transmittedFrames, 0)
  entries.push({
    name: 'Sound modem',
    chip: running.length > 0 ? 'running' : modemStatuses.value.length > 0 ? 'stopped' : 'off',
    color: running.length > 0 ? 'success' : modemStatuses.value.length > 0 ? 'error' : 'grey',
    note:
      modemStatuses.value.length > 0
        ? `${decoded.toLocaleString()} decoded · ${transmitted.toLocaleString()} TX`
        : 'no audio feed configured',
  })

  entries.push({
    name: 'External TNC (KISS)',
    chip: s?.direwolfConnected ? 'connected' : cfg?.direwolfEnabled ? 'disconnected' : 'off',
    color: s?.direwolfConnected ? 'success' : cfg?.direwolfEnabled ? 'warning' : 'grey',
    note: cfg?.direwolfEnabled
      ? `${cfg.direwolfHost ?? 'localhost'}:${cfg.direwolfPort ?? 8001}`
      : 'native modem carries RF',
  })

  entries.push({
    name: 'APRS-IS',
    chip: (s?.aprsIsState ?? '…').toLowerCase(),
    color:
      s?.aprsIsState === 'Connected'
        ? 'success'
        : s?.aprsIsState === 'Disabled'
          ? 'grey'
          : 'warning',
    note: s?.aprsIsServerName ?? 'enable in Settings → APRS-IS',
  })

  entries.push({
    name: 'Digipeater',
    chip: cfg?.digipeaterEnabled ? 'on' : 'off',
    color: cfg?.digipeaterEnabled ? 'success' : 'grey',
    note: cfg?.digipeaterEnabled
      ? `${(s?.digipeatedFrames ?? 0).toLocaleString()} repeated`
      : 'not repeating',
  })

  entries.push({
    name: 'KISS Server',
    chip: cfg?.kissServerEnabled ? 'on' : 'off',
    color: cfg?.kissServerEnabled ? 'success' : 'grey',
    note: cfg?.kissServerEnabled
      ? `port ${cfg.kissServerPort} · ${s?.kissServerClients ?? 0} client${(s?.kissServerClients ?? 0) === 1 ? '' : 's'}`
      : `port ${cfg?.kissServerPort ?? 8010} closed`,
  })

  const gating = cfg?.rfToIsGatingEnabled || cfg?.isToRfGatingEnabled
  const gateParts: string[] = []
  if (cfg?.rfToIsGatingEnabled)
    gateParts.push(`RF→IS (${(s?.rfToIsGatedLines ?? 0).toLocaleString()} gated)`)
  if (cfg?.isToRfGatingEnabled) gateParts.push('IS→RF')
  entries.push({
    name: 'iGate',
    chip: gating ? 'on' : 'off',
    color: gating ? 'success' : 'grey',
    note: gating ? gateParts.join(' · ') : 'not gating',
  })

  return entries
})

onMounted(async () => {
  await refresh()
  try {
    settings.value = await getSettings()
  } catch {
    /* ignore */
  }
  try {
    radios.value = await getRadios()
    seedTxGain()
    radiosLoadFailed.value = false
  } catch {
    radiosLoadFailed.value = true
  }
  // Last-beacon info per radio — kept live afterwards by the shared hub events
  // the radios store subscribes to.
  radiosStore.radios = radios.value
  await radiosStore.fetchAllLastBeacons()
  try {
    // Seed the feed so the page isn't blank until the next live packet arrives.
    const since = new Date(Date.now() - 60 * 60 * 1000).toISOString()
    const recent = await getPacketsSince(since, MAX_FEED)
    packets.value = recent.map(packetDtoToBroadcast)
  } catch {
    /* ignore — feed fills from live packets */
  }

  statusTimer = setInterval(refresh, 5000)

  hub.on('modemLevel', onHubModemLevel)
  hub.on('modemSpectrum', onHubModemSpectrum)
  hub.on('modemStatusChanged', onHubModemStatusChanged)
  hub.on('packetReceived', onHubPacketReceived)
  hub.on('packetSourceUpgraded', onHubPacketSourceUpgraded)
})

onUnmounted(() => {
  if (statusTimer) clearInterval(statusTimer)
  for (const t of Object.values(txGainTimers)) clearTimeout(t)
  hub.off('modemLevel', onHubModemLevel)
  hub.off('modemSpectrum', onHubModemSpectrum)
  hub.off('modemStatusChanged', onHubModemStatusChanged)
  hub.off('packetReceived', onHubPacketReceived)
  hub.off('packetSourceUpgraded', onHubPacketSourceUpgraded)
})
</script>

<template>
  <div class="radio-view pa-4">
    <div class="d-flex align-center ga-3 mb-4 flex-wrap">
      <span class="text-h5 font-weight-bold">Radio</span>
      <v-chip v-if="refreshFailed" color="warning" size="small" variant="tonal">
        <v-icon start size="14">mdi-lan-disconnect</v-icon>
        Backend unreachable · {{ staleLabel }}
      </v-chip>
    </div>

    <div class="radio-layout">
      <!-- ── RF stack column ── -->
      <div class="stack-column">
        <!-- One card per radio with a modem instance -->
        <v-card v-for="m in modemStatuses" :key="m.radioId" variant="outlined" class="mb-4">
          <div class="d-flex align-center flex-wrap ga-2 px-4 pt-3 pb-2">
            <span class="text-subtitle-1 font-weight-medium">{{ m.radioName }}</span>
            <v-chip size="x-small" variant="tonal" class="identity-chip">
              {{ identityLabel(m) }}
            </v-chip>
            <v-chip :color="stateColor(m)" size="x-small" variant="tonal">
              {{ stateLabel(m) }}
            </v-chip>
            <v-spacer />
            <span class="text-caption text-medium-emphasis">{{ updatedLabel }}</span>
          </div>
          <v-divider />

          <div class="pa-4 pt-3">
            <v-alert
              v-if="m.state === ModemStates.Error && m.errorMessage"
              type="error"
              density="compact"
              class="mb-3"
            >
              <div class="d-flex align-center ga-3 flex-wrap">
                <span>{{ m.errorMessage }}</span>
                <v-btn
                  size="small"
                  variant="tonal"
                  color="error"
                  prepend-icon="mdi-restart"
                  :loading="modemRestarting"
                  @click="doRestartModem"
                >
                  Restart modem
                </v-btn>
              </div>
            </v-alert>

            <template v-if="m.state === ModemStates.Running">
              <div class="modem-body">
                <!-- Left: meters + waterfall -->
                <div class="modem-meters">
                  <!-- Mock style: labeled thin meter, value right -->
                  <div class="d-flex align-center justify-space-between mb-1">
                    <span class="text-caption text-medium-emphasis">Audio level</span>
                    <span class="text-caption text-medium-emphasis meter-pct">
                      {{ levelFor(m).audioLevel.toFixed(2) }}
                    </span>
                  </div>
                  <v-progress-linear
                    :model-value="Math.min(100, levelFor(m).audioLevel * 100)"
                    :color="levelColor(levelFor(m).audioLevel)"
                    height="6"
                    rounded
                    class="mb-3"
                  />

                  <div v-if="m.txEnabled" class="d-flex align-center ga-2 mb-1 flex-nowrap">
                    <span class="text-caption text-medium-emphasis flex-shrink-0">TX gain</span>
                    <v-slider
                      :model-value="txGain[m.radioId] ?? 80"
                      :min="1"
                      :max="100"
                      :step="1"
                      color="primary"
                      density="compact"
                      hide-details
                      thumb-label
                      @update:model-value="(v: number) => onTxGainInput(m.radioId, v)"
                    />
                    <span class="text-caption text-medium-emphasis meter-pct">
                      {{ Math.round(txGain[m.radioId] ?? 80) }}%
                    </span>
                  </div>

                  <div v-if="m.txEnabled" class="d-flex align-center ga-2 mb-2 flex-wrap">
                    <span class="text-caption text-medium-emphasis">Test tones</span>
                    <v-btn-group density="compact" variant="outlined" divided>
                      <v-btn
                        size="x-small"
                        :loading="toneSending[m.radioId]"
                        @click="doTestTone(m.radioId, TestToneKinds.Mark)"
                      >
                        Mark
                      </v-btn>
                      <v-btn
                        size="x-small"
                        :disabled="toneSending[m.radioId]"
                        @click="doTestTone(m.radioId, TestToneKinds.Space)"
                      >
                        Space
                      </v-btn>
                      <v-btn
                        size="x-small"
                        :disabled="toneSending[m.radioId]"
                        @click="doTestTone(m.radioId, TestToneKinds.Alternating)"
                      >
                        Alt
                      </v-btn>
                    </v-btn-group>
                    <span class="text-caption text-medium-emphasis">keys TX ~2s</span>
                  </div>

                  <div class="text-caption text-medium-emphasis mt-1 mb-1">Waterfall</div>
                  <WaterfallCanvas :ref="(el) => setWaterfallRef(m.radioId, el)" :height="100" />
                </div>

                <!-- Right: stats -->
                <dl class="modem-kv">
                  <dt>Decoded frames</dt>
                  <dd>{{ m.decodedFrames.toLocaleString() }}</dd>
                  <dt>Bad CRC</dt>
                  <dd>{{ m.invalidFrames.toLocaleString() }}</dd>
                  <template v-if="m.txEnabled">
                    <dt>Transmitted</dt>
                    <dd>{{ m.transmittedFrames.toLocaleString() }}</dd>
                  </template>
                  <dt>Carrier</dt>
                  <dd>
                    <v-chip
                      :color="levelFor(m).carrierDetected ? 'success' : 'grey'"
                      size="x-small"
                      variant="tonal"
                    >
                      {{ levelFor(m).carrierDetected ? 'detected' : 'quiet' }}
                    </v-chip>
                  </dd>
                  <template v-if="pttLabel(m)">
                    <dt>PTT</dt>
                    <dd class="text-caption">{{ pttLabel(m) }}</dd>
                  </template>
                  <template v-if="m.captureDevice">
                    <dt>Capture</dt>
                    <dd class="text-caption text-truncate" :title="m.captureDevice">
                      {{ m.captureDevice }}
                    </dd>
                  </template>
                  <dt>Last beacon</dt>
                  <dd class="text-caption">{{ lastBeaconLabel(m) }}</dd>
                </dl>
              </div>
            </template>

            <!-- Action row -->
            <div class="d-flex align-center ga-2 flex-wrap mt-3">
              <v-btn
                size="small"
                variant="tonal"
                color="primary"
                prepend-icon="mdi-access-point"
                :loading="beaconing[m.radioId]"
                :disabled="!m.txEnabled"
                :title="
                  m.txEnabled
                    ? 'Transmit a position beacon now'
                    : 'TX is not enabled for this radio'
                "
                @click="doBeaconNow(m)"
              >
                Beacon now
              </v-btn>
              <v-btn
                size="small"
                variant="outlined"
                prepend-icon="mdi-restart"
                :loading="modemRestarting"
                title="Tear down and re-open the modem audio devices"
                @click="doRestartModem"
              >
                Restart modem
              </v-btn>
              <v-btn
                size="small"
                variant="outlined"
                prepend-icon="mdi-history"
                @click="openHistory(m)"
              >
                History
              </v-btn>
            </div>
          </div>
        </v-card>

        <v-card v-if="modemStatuses.length === 0" variant="outlined" class="mb-4 pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-1">Sound Modem</div>
          <template v-if="refreshFailed || radiosLoadFailed">
            <div class="text-caption text-medium-emphasis mb-2">
              Couldn't reach the backend — modem status is unknown.
            </div>
            <v-btn size="small" color="primary" variant="tonal" @click="refresh">Retry</v-btn>
          </template>
          <div v-else class="text-caption text-medium-emphasis">
            No radios have a modem audio feed configured — enable one in a radio's settings.
          </div>
        </v-card>

        <!-- Other RF stack services -->
        <v-card variant="outlined" class="mb-4">
          <div class="d-flex align-center px-4 pt-3 pb-2">
            <span class="text-subtitle-1 font-weight-medium">RF Stack</span>
            <v-spacer />
            <span class="text-caption text-medium-emphasis">{{ updatedLabel }}</span>
          </div>
          <v-divider />
          <div class="rf-stack pa-3">
            <div v-for="entry in rfStack" :key="entry.name" class="rf-item">
              <div class="d-flex align-center justify-space-between ga-2">
                <span class="text-body-2 font-weight-medium">{{ entry.name }}</span>
                <v-chip :color="entry.color" size="x-small" variant="tonal">{{
                  entry.chip
                }}</v-chip>
              </div>
              <span class="text-caption text-medium-emphasis">{{ entry.note }}</span>
            </div>
          </div>
        </v-card>

        <!-- Radios without a modem feed -->
        <v-card v-if="radiosWithoutModem.length > 0" variant="outlined" class="pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-2">Other Radios</div>
          <div
            v-for="radio in radiosWithoutModem"
            :key="radio.id"
            class="d-flex align-center ga-2 mb-1 flex-wrap"
          >
            <v-chip :color="radio.isActive ? 'success' : 'grey'" size="x-small" variant="tonal">
              {{ radio.fullCallsign }}
            </v-chip>
            <span class="text-body-2">{{ radio.name }}</span>
            <span class="text-caption text-medium-emphasis">ch {{ radio.channelNumber }}</span>
            <span v-if="radio.frequencyMhz" class="text-caption text-medium-emphasis">
              · {{ radio.frequencyMhz.toFixed(3) }} MHz{{ radio.mode ? ` ${radio.mode}` : '' }}
            </span>
          </div>
        </v-card>
      </div>

      <!-- ── Live packet feed ── -->
      <v-card variant="outlined" class="feed-column pa-0">
        <div class="d-flex align-center ga-2 pa-3 pb-2">
          <span class="text-subtitle-1 font-weight-medium">Incoming Packets</span>
          <v-chip size="x-small" variant="tonal" color="primary">live</v-chip>
          <v-spacer />
          <span class="text-caption text-medium-emphasis">click a row to inspect</span>
        </div>
        <v-divider />
        <div class="feed-scroll">
          <div v-if="packets.length === 0" class="text-caption text-medium-emphasis pa-4">
            Waiting for packets…
          </div>
          <div
            v-for="p in packets"
            :key="p.id"
            class="feed-row"
            role="button"
            tabindex="0"
            @click="inspectedPacketId = p.id"
            @keydown.enter="inspectedPacketId = p.id"
          >
            <!-- Mock format: time | "CALLSIGN · Type · summary" | source badge -->
            <span
              class="text-caption text-medium-emphasis feed-time"
              :title="formatUtc(p.receivedAt)"
              >{{ timeAgo(p.receivedAt, now) }}</span
            >
            <span class="feed-summary text-body-2" :title="`${typeLabel(p)} · ${p.summary}`">
              <a
                class="callsign-link font-weight-medium"
                @click.stop.prevent="goToStation(p.callsign)"
              >
                {{ p.callsign }}
              </a>
              <span class="text-medium-emphasis">
                · {{ typeLabel(p) }}<template v-if="p.summary"> · {{ p.summary }}</template>
              </span>
            </span>
            <v-chip
              size="x-small"
              variant="tonal"
              class="feed-source"
              :color="sourceLabel(p) === 'RF' ? 'rf' : 'is'"
            >
              {{ sourceLabel(p) }}
            </v-chip>
          </div>
        </div>
      </v-card>
    </div>

    <PacketInspectionDialog
      :packet-id="inspectedPacketId"
      @close="inspectedPacketId = null"
      @select-station="goToStation"
    />

    <BeaconHistoryModal
      v-if="historyRadio"
      v-model="historyOpen"
      :radio-id="historyRadio.id"
      :radio-name="historyRadio.name"
    />
  </div>
</template>

<style scoped>
.radio-view {
  height: 100%;
  overflow-y: auto;
}

/* Mock proportions: the radio card is the dominant column (7:5). */
.radio-layout {
  display: grid;
  grid-template-columns: minmax(420px, 7fr) minmax(320px, 5fr);
  gap: 16px;
  align-items: start;
}

@media (max-width: 900px) {
  .radio-layout {
    grid-template-columns: 1fr;
  }
}

.stack-column {
  min-width: 0;
}

.identity-chip {
  font-variant-numeric: tabular-nums;
}

.modem-body {
  display: flex;
  gap: 18px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.modem-meters {
  flex: 1 1 240px;
  min-width: 220px;
}

.meter-pct {
  flex-shrink: 0;
  width: 40px;
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.modem-kv {
  flex: 0 1 170px;
  min-width: 150px;
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 12px;
  align-items: center;
  margin: 0;
}

.modem-kv dt {
  font-size: 0.75rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
}

.modem-kv dd {
  margin: 0;
  text-align: right;
  font-size: 0.8rem;
  font-variant-numeric: tabular-nums;
  min-width: 0;
}

.rf-stack {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}

@media (max-width: 560px) {
  .rf-stack {
    grid-template-columns: 1fr;
  }
}

.rf-item {
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-radius: 8px;
  padding: 8px 12px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.feed-column {
  min-width: 0;
  display: flex;
  flex-direction: column;
  max-height: calc(100vh - 140px);
}

.feed-scroll {
  overflow-y: auto;
  flex: 1;
}

.feed-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 12px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  cursor: pointer;
}

.feed-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.feed-time {
  width: 76px;
  flex-shrink: 0;
}

.feed-summary {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
}

.feed-source {
  flex-shrink: 0;
  margin-left: auto;
}

.callsign-link {
  color: rgba(var(--v-theme-primary), 1);
  cursor: pointer;
  text-decoration: none;
}

.callsign-link:hover {
  text-decoration: underline;
}
</style>

<script setup lang="ts">
import { audioLevelColor } from '@/utils/audioLevel'
import { useOwnCallsign } from '@/composables/useOwnCallsign'
import { sourceChip, isOwnCallsign, type SourceChip } from '@/types/packet'
import { ref, reactive, computed, onMounted, onUnmounted } from 'vue'

// Named so <keep-alive include="RadioView"> in App.vue keeps this view (and its
// live SignalR connection, packet buffer, and waterfall history) mounted while
// the user navigates elsewhere.
defineOptions({ name: 'RadioView' })
import { useRouter } from 'vue-router'
import {
  getModemStatus,
  restartModem,
  forceUnkey,
  decodeSpectrumPayload,
  ModemStates,
  modemStateLabels,
  PttMethods,
  type ModemLevelDto,
  type ModemSpectrumDto,
  type ModemStatusDto,
} from '@/api/modemApi'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore, LEVEL_STALE_MS } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { getStatus, AprsIsStates, aprsIsStateLabels, type StatusDto } from '@/api/statusApi'
import { getSettings, getPacketsSince } from '@/api/stationsApi'
import { getRadios } from '@/api/radiosApi'
import { apiErrorDetail } from '@/api/axios'
import { useTxStore } from '@/stores/tx'
import { useHealthStore } from '@/stores/health'
import { useUiStore } from '@/stores/uiStore'
import TxLifecycle from '@/components/tx/TxLifecycle.vue'
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
import { timeAgo, formatDateTime } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'

const MAX_FEED = 100
const router = useRouter()
const { now } = useTick(5000)
const hub = usePacketHubStore()
const radiosStore = useRadiosStore()
const toastStore = useToastStore()
const txStore = useTxStore()
const healthStore = useHealthStore()
const ui = useUiStore()

const status = ref<StatusDto | null>(null)
const modemStatuses = ref<ModemStatusDto[]>([])
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[]>([])
const packets = ref<PacketBroadcastDto[]>([])

// Live meters per radio — updated at 10 Hz over SignalR, keyed by radio id.
const levels = ref<Record<string, ModemLevelDto>>({})
// When each radio's last level frame arrived; a meter with nothing recent is
// greyed out rather than frozen on its last reading.
const levelsAt = ref<Record<string, number>>({})
const { now: levelClock } = useTick(1000)

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
    toastStore.toast('Modem restarting. Audio devices are being re-opened…', 'info')
    // Give the backend a moment, then refresh so the state chip catches up.
    setTimeout(refresh, 1500)
  } catch {
    toastStore.toast('Modem restart failed. The backend may be unreachable.', 'error')
  } finally {
    modemRestarting.value = false
  }
}

// ── Beacon now ────────────────────────────────────────────────────────────────
const beaconing = reactive<Record<string, boolean>>({})

async function doBeaconNow(m: ModemStatusDto) {
  beaconing[m.radioId] = true
  try {
    // Queued, not sent: the transmit pipeline below shows when it goes out.
    if (await txStore.beaconNow(m.radioId, m.fullCallsign))
      void radiosStore.fetchLastBeacon(m.radioId)
  } finally {
    beaconing[m.radioId] = false
  }
}

/** Why this radio cannot transmit right now (button stays visible with the reason). */
function txBlockedFor(m: ModemStatusDto): string | null {
  if (!m.txEnabled) return 'TX is not enabled for this radio.'
  if (m.pttStuck) return 'PTT is stuck keyed. Force unkey first.'
  if (m.state !== ModemStates.Running) return 'The modem is not running.'
  return healthStore.health.txBlockedReason
}

/** This radio's recent frames, newest first, for the transmit pipeline card. */
function framesFor(radioId: string) {
  return txStore.list.filter((f) => f.radioId === radioId).slice(0, 6)
}

const forceUnkeying = reactive<Record<string, boolean>>({})
async function doForceUnkey(m: ModemStatusDto) {
  forceUnkeying[m.radioId] = true
  try {
    await forceUnkey(m.radioId)
    toastStore.toast('PTT released', 'success')
    await radiosStore.fetchModemStatus()
  } catch (e) {
    toastStore.toast(apiErrorDetail(e, 'Force unkey failed'), 'error', 10_000)
  } finally {
    forceUnkeying[m.radioId] = false
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
  const at = serverNow()
  for (const level of batch) {
    levels.value[level.radioId] = level
    levelsAt.value[level.radioId] = at
  }
}

function csmaWaitSeconds(m: ModemStatusDto): number | null {
  if (!m.csmaWaitingSince) return null
  return Math.max(0, Math.round((levelClock.value - new Date(m.csmaWaitingSince).getTime()) / 1000))
}

function levelStale(m: ModemStatusDto): boolean {
  const at = levelsAt.value[m.radioId]
  return at === undefined || levelClock.value - at > LEVEL_STALE_MS
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

/** a running modem is "Receiving". */
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

/** One compact identity line: "W3UWU · ch 0 · 144.390 MHz FM". */
function identityLabel(m: ModemStatusDto): string {
  const parts = [m.fullCallsign, `ch ${m.channel}`]
  const freq = frequencyLabel(m)
  if (freq) parts.push(freq)
  return parts.join(' · ')
}

/** Rigctld live frequency wins; manual radio frequency is the fallback.
 *  Format: "144.390 FM" — the MHz unit is implied. */
function frequencyLabel(m: ModemStatusDto): string | null {
  if (m.rigFrequencyHz) return (m.rigFrequencyHz / 1_000_000).toFixed(4)
  const radio = radioFor(m)
  if (radio?.frequencyMhz) {
    const mode = radio.mode ? ` ${radio.mode}` : ''
    return `${radio.frequencyMhz.toFixed(3)}${mode}`
  }
  return null
}

// Short labels — the kv column is narrow and the layout shows "RTS/DTR ttyUSB0".
const PTT_LABELS: Record<number, string> = {
  [PttMethods.None]: 'VOX / none',
  [PttMethods.SerialRtsDtr]: 'RTS/DTR',
  [PttMethods.Cm108]: 'CM108',
  [PttMethods.Gpio]: 'GPIO',
  [PttMethods.Rigctld]: 'rigctld',
}

function pttLabel(m: ModemStatusDto): string | null {
  const radio = radioFor(m)
  if (!radio || !radio.modem.txEnabled) return null
  const label = PTT_LABELS[radio.modem.pttMethod] ?? null
  if (radio.modem.pttMethod === PttMethods.SerialRtsDtr && radio.modem.pttSerialPort) {
    return `${label} ${radio.modem.pttSerialPort.replace('/dev/', '')}`
  }
  return label
}

// feed times are bare ("now", "28s", "1m") — no " ago" suffix.
function feedTime(receivedAt: string): string {
  const diff = Math.max(0, Math.floor((now.value - new Date(receivedAt).getTime()) / 1000))
  if (diff < 10) return 'now'
  return timeAgo(receivedAt, now.value).replace(/ ago$/, '')
}

function lastBeaconFor(m: ModemStatusDto): LastBeaconDto | undefined {
  return radiosStore.getLastBeaconForRadio(m.radioId)
}

function lastBeaconLabel(m: ModemStatusDto): string {
  const b = lastBeaconFor(m)
  if (!b?.beaconedAt) return 'never'
  return `${timeAgo(b.beaconedAt, now.value)}${b.heard ? ' · heard ✓' : ' · unconfirmed'}`
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

const levelColor = audioLevelColor

function typeLabel(p: PacketBroadcastDto): string {
  return PACKET_TYPE_LABELS[parsedTypeFromString(p.parsedType)] ?? 'Unknown'
}

const { ownCallsign } = useOwnCallsign()

const feedFilter = ref<'rf' | 'all'>('rf')
const feedPackets = computed(() =>
  feedFilter.value === 'all'
    ? packets.value
    : packets.value.filter((p) => p.source !== PacketSource.AprsIs),
)
function feedSource(p: PacketBroadcastDto): SourceChip {
  return sourceChip(p.source, isOwnCallsign(p.callsign, ownCallsign.value))
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

// RF stack entries as name + chip + caption.
interface RfStackEntry {
  name: string
  chip: string
  color: string
  note: string
  /** Settings tab to fix this entry, when it is off or unconfigured. */
  to?: string
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
    name: 'APRS-IS',
    chip: s ? aprsIsStateLabels[s.aprsIsState].toLowerCase() : '…',
    color:
      s?.aprsIsState === AprsIsStates.Connected
        ? 'success'
        : s?.aprsIsState === AprsIsStates.Disabled
          ? 'grey'
          : s?.aprsIsState === AprsIsStates.AuthFailed
            ? 'error'
            : 'warning',
    note: s?.aprsIsServerName ?? 'not connected',
    to: s?.aprsIsState === AprsIsStates.Connected ? undefined : '/settings/aprsis',
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
    name: 'KISS server',
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

  entries.push({
    name: 'External TNC (KISS)',
    chip: s?.externalTncConnected ? 'connected' : cfg?.direwolfEnabled ? 'disconnected' : 'off',
    color: s?.externalTncConnected ? 'success' : cfg?.direwolfEnabled ? 'warning' : 'grey',
    note: cfg?.direwolfEnabled
      ? `${cfg.direwolfHost ?? 'localhost'}:${cfg.direwolfPort ?? 8001}`
      : 'native modem carries RF',
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
      <span class="page-title">Radio</span>
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
          <div class="d-flex align-center flex-wrap ga-2 card-head">
            <span class="card-title">{{ m.radioName }}</span>
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
              <v-alert
                v-if="m.pttStuck"
                type="error"
                density="compact"
                variant="tonal"
                class="mb-3"
                icon="mdi-alert-octagon"
              >
                PTT could not be released. The radio may still be transmitting. Check the PTT
                interface and the radio now.
              </v-alert>
              <v-alert
                v-else-if="m.txError"
                type="warning"
                density="compact"
                variant="tonal"
                class="mb-3"
              >
                Last transmit failed: {{ m.txError }}. Receive is unaffected; TX retries shortly.
              </v-alert>
              <v-alert
                v-if="radiosStore.carrierHeldSeconds(m.radioId) !== null"
                type="warning"
                density="compact"
                variant="tonal"
                class="mb-3"
              >
                Carrier detect has been on for
                {{ radiosStore.carrierHeldSeconds(m.radioId) }} s without decoding anything, so
                transmissions are waiting for a clear channel. Usually an open squelch or receive
                audio set too high. Close the squelch or lower the input level.
              </v-alert>
              <div class="modem-body">
                <!-- Left: meters + waterfall -->
                <div class="modem-meters">
                  <!-- style: labeled thin meter, value right -->
                  <div class="d-flex align-center justify-space-between mb-1">
                    <span class="text-caption text-medium-emphasis">Audio level</span>
                    <span class="text-caption text-medium-emphasis meter-pct">
                      {{ levelFor(m).audioLevel.toFixed(2) }}
                    </span>
                  </div>
                  <v-progress-linear
                    :model-value="levelStale(m) ? 0 : Math.min(100, levelFor(m).audioLevel * 100)"
                    :color="levelStale(m) ? 'grey' : levelColor(levelFor(m).audioLevel)"
                    height="6"
                    rounded
                    class="mb-3"
                  />
                  <v-chip
                    v-if="levelStale(m)"
                    size="x-small"
                    variant="tonal"
                    color="grey"
                    prepend-icon="mdi-signal-off"
                    class="mb-3"
                  >
                    no level data
                  </v-chip>

                  <div class="text-caption text-medium-emphasis mt-1 mb-1">Waterfall</div>
                  <WaterfallCanvas :ref="(el) => setWaterfallRef(m.radioId, el)" :height="100" />
                </div>

                <!-- Right: stats -->
                <dl class="modem-kv">
                  <dt>Decoded frames</dt>
                  <dd>{{ m.decodedFrames.toLocaleString() }}</dd>
                  <dt>Bad CRC</dt>
                  <dd>{{ m.invalidFrames.toLocaleString() }}</dd>
                  <template v-if="m.repairedFrames">
                    <dt>Recovered by bit repair</dt>
                    <dd>{{ m.repairedFrames.toLocaleString() }}</dd>
                  </template>
                  <template v-if="m.decodedByProfile && Object.keys(m.decodedByProfile).length > 1">
                    <dt>By profile</dt>
                    <dd class="kv-mono">
                      {{
                        Object.entries(m.decodedByProfile)
                          .map(([name, n]) => `${name} ${n.toLocaleString()}`)
                          .join(' · ')
                      }}
                    </dd>
                  </template>
                  <template v-if="m.txEnabled">
                    <dt>Transmitted</dt>
                    <dd>{{ m.transmittedFrames.toLocaleString() }}</dd>
                    <dt>TX</dt>
                    <dd>
                      <v-chip
                        :color="levelFor(m).transmitting ? 'error' : 'grey'"
                        size="x-small"
                        variant="tonal"
                      >
                        {{ levelFor(m).transmitting ? 'transmitting' : 'idle' }}
                      </v-chip>
                    </dd>
                    <dt>TX queue</dt>
                    <dd>
                      {{ m.txQueueDepth }} frame(s)
                      <span v-if="m.txDropped > 0" class="text-warning">
                        · {{ m.txDropped }} dropped
                      </span>
                    </dd>
                    <dt>Last TX</dt>
                    <dd>{{ m.lastTxAt ? timeAgo(m.lastTxAt, now) : 'never' }}</dd>
                    <template v-if="csmaWaitSeconds(m) !== null">
                      <dt>Channel</dt>
                      <dd>
                        <span class="text-warning">
                          Waiting for clear channel {{ csmaWaitSeconds(m) }} s
                        </span>
                        <v-progress-linear indeterminate color="warning" height="2" class="mt-1" />
                      </dd>
                    </template>
                  </template>
                  <dt>Carrier</dt>
                  <dd>
                    <v-chip
                      v-if="radiosStore.carrierHeldSeconds(m.radioId) !== null"
                      color="warning"
                      size="x-small"
                      variant="tonal"
                      prepend-icon="mdi-alert"
                    >
                      Carrier held {{ radiosStore.carrierHeldSeconds(m.radioId) }} s · TX deferred
                    </v-chip>
                    <v-chip
                      v-else
                      :color="!levelStale(m) && levelFor(m).carrierDetected ? 'success' : 'grey'"
                      size="x-small"
                      variant="tonal"
                    >
                      {{
                        levelStale(m)
                          ? 'unknown'
                          : levelFor(m).carrierDetected
                            ? 'detected'
                            : 'quiet'
                      }}
                    </v-chip>
                  </dd>
                  <template v-if="pttLabel(m)">
                    <dt>PTT</dt>
                    <dd class="kv-mono" :title="m.captureDevice ?? undefined">
                      {{ pttLabel(m) }}
                    </dd>
                  </template>
                  <dt>Last beacon</dt>
                  <dd class="kv-wrap">{{ lastBeaconLabel(m) }}</dd>
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
                :disabled="!!txBlockedFor(m)"
                @click="doBeaconNow(m)"
              >
                Beacon now
              </v-btn>
              <v-btn
                v-if="m.pttStuck"
                size="small"
                variant="flat"
                color="error"
                prepend-icon="mdi-radio-off"
                :loading="forceUnkeying[m.radioId]"
                @click="doForceUnkey(m)"
              >
                Force unkey
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
            <div v-if="txBlockedFor(m)" class="text-caption text-warning mt-1">
              {{ txBlockedFor(m) }}
            </div>

            <!-- Transmit pipeline: this radio's recent frames, queued → on air → sent -->
            <div v-if="m.txEnabled" class="tx-pipeline mt-3">
              <div class="d-flex align-center mb-1">
                <span class="text-caption font-weight-medium">Transmit pipeline</span>
                <v-spacer />
                <v-btn size="x-small" variant="text" @click="ui.txSheetOpen = true">
                  TX queue
                </v-btn>
              </div>
              <div
                v-if="framesFor(m.radioId).length === 0"
                class="text-caption text-medium-emphasis"
              >
                Nothing sent this session.
              </div>
              <div v-for="f in framesFor(m.radioId)" :key="f.id" class="tx-pipe-row">
                <span class="tx-pipe-title">{{ f.title }}</span>
                <TxLifecycle :item="f" compact />
                <div v-if="f.reason" class="text-caption text-error">{{ f.reason }}</div>
              </div>
            </div>
          </div>
        </v-card>

        <v-card v-if="modemStatuses.length === 0" variant="outlined" class="mb-4 pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-1">Sound Modem</div>
          <template v-if="refreshFailed || radiosLoadFailed">
            <div class="text-caption text-medium-emphasis mb-2">
              Couldn't reach the backend. Modem status is unknown.
            </div>
            <v-btn size="small" color="primary" variant="tonal" @click="refresh">Retry</v-btn>
          </template>
          <div v-else class="text-caption text-medium-emphasis">
            No radios have a modem audio feed configured.
            <v-btn variant="text" size="small" to="/settings/radios">Set up a radio</v-btn>
          </div>
        </v-card>

        <!-- Other RF stack services -->
        <v-card variant="outlined" class="mb-4">
          <div class="d-flex align-center ga-2 card-head">
            <span class="card-title">RF Stack</span>
            <v-spacer />
            <span class="text-caption text-medium-emphasis">{{ updatedLabel }}</span>
          </div>
          <v-divider />
          <div class="rf-stack">
            <div v-for="entry in rfStack" :key="entry.name" class="rf-item">
              <div class="d-flex align-center justify-space-between ga-2">
                <span class="rf-name">{{ entry.name }}</span>
                <v-chip :color="entry.color" size="x-small" variant="tonal">{{
                  entry.chip
                }}</v-chip>
              </div>
              <span class="rf-note">
                {{ entry.note }}
                <v-btn
                  v-if="entry.to"
                  :to="entry.to"
                  variant="text"
                  size="x-small"
                  density="compact"
                  class="px-1"
                >
                  Settings
                </v-btn>
              </span>
            </div>
          </div>
        </v-card>

        <!-- Radios without a modem feed -->
        <v-card v-if="radiosWithoutModem.length > 0" variant="outlined" class="pa-4">
          <div class="card-title mb-2">Other radios</div>
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
        <div class="d-flex align-center ga-2 card-head">
          <span class="card-title">Incoming packets</span>
          <v-chip size="x-small" variant="tonal" color="primary">live</v-chip>
          <v-spacer />
          <!-- This page is about the radio: APRS-IS traffic is opt-in here. -->
          <v-btn-toggle
            v-model="feedFilter"
            density="compact"
            variant="outlined"
            divided
            mandatory
            class="mr-2"
          >
            <v-btn value="rf" size="x-small">RF / Own</v-btn>
            <v-btn value="all" size="x-small">All</v-btn>
          </v-btn-toggle>
          <span class="text-caption text-medium-emphasis">click a row to inspect</span>
        </div>
        <v-divider />
        <div class="feed-scroll">
          <div v-if="feedPackets.length === 0" class="text-caption text-medium-emphasis pa-4">
            Waiting for packets…
          </div>
          <div
            v-for="p in feedPackets"
            :key="p.id"
            class="feed-row"
            role="button"
            tabindex="0"
            @click="inspectedPacketId = p.id"
            @keydown.enter="inspectedPacketId = p.id"
          >
            <!-- Format: time | "CALLSIGN · Type · summary" | source badge -->
            <span
              class="text-caption text-medium-emphasis feed-time"
              :title="formatDateTime(p.receivedAt)"
              >{{ feedTime(p.receivedAt) }}</span
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
            <v-chip size="x-small" variant="tonal" class="feed-source" :color="feedSource(p).color">
              {{ feedSource(p).label }}
            </v-chip>
          </div>
        </div>
      </v-card>
    </div>

    <!-- Capture, auto-capture, the log pack and the RF internals moved to Diagnostics. -->
    <div class="mt-4">
      <v-btn variant="tonal" prepend-icon="mdi-stethoscope" to="/radio/diagnostics">
        Diagnostics: carrier, TX lanes, PTT, capture &amp; log pack
      </v-btn>
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
.tx-pipe-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 12px;
  padding: 4px 0;
  border-top: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.tx-pipe-title {
  font-size: 0.8125rem;
  min-width: 140px;
}

.radio-view {
  height: 100%;
  overflow-y: auto;
}

/* Type scale: page h2 17px/650, card h3 14px/650, header pad 12px 16px. */
.page-title {
  font-size: 17px;
  font-weight: 650;
  line-height: 1.3;
}

.card-head {
  padding: 12px 16px;
}

.card-title {
  font-size: 14px;
  font-weight: 650;
}

/* proportions: the radio card is the dominant column (7:5). */
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
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
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

/* kv: fixed 190px column, 12.5px, labels muted, values right-aligned.
   Wide enough that "6m ago · unconfirmed" and "RTS/DTR ttyUSB0" never wrap. */
.modem-kv {
  flex: 0 0 190px;
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 14px;
  align-items: center;
  align-self: flex-start;
  margin: 0;
}

.modem-kv dt {
  font-size: 12.5px;
  color: rgba(var(--v-theme-on-surface), 0.6);
  white-space: nowrap;
}

.modem-kv dd {
  margin: 0;
  text-align: right;
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.kv-mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11.5px !important;
}

/* Values that carry state ("6m ago · unconfirmed") wrap at word boundaries
   instead of ellipsizing away the state. */
.kv-wrap {
  white-space: normal;
  overflow: visible;
  text-overflow: clip;
}

.rf-stack {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
  padding: 14px 16px;
}

@media (max-width: 560px) {
  .rf-stack {
    grid-template-columns: 1fr;
  }
}

/* rfitem: 12.5px bold name, 11.5px muted note, 9px radius. */
.rf-item {
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-radius: 9px;
  padding: 9px 12px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.rf-name {
  font-size: 12.5px;
  font-weight: 650;
}

.rf-note {
  font-size: 11.5px;
  color: rgba(var(--v-theme-on-surface), 0.55);
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

/* feed rows: 13px, mono 64px time column, 8px vertical rhythm. */
.feed-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 14px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  cursor: pointer;
}

.feed-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.feed-time {
  width: 64px;
  flex-shrink: 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
}

.feed-summary {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
  font-size: 13px !important;
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
